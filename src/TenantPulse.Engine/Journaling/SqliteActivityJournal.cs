using Microsoft.Data.Sqlite;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;

namespace TenantPulse.Engine.Journaling;

/// <summary>
/// SQLite-backed activity journal. Records every intent tenant-pulse acted on, including the Graph
/// path needed to delete whatever it created — which is what makes the simulator reversible.
/// </summary>
public sealed class SqliteActivityJournal(TenantPulseOptions options) : IActivityJournal
{
    private readonly string _connectionString = BuildConnectionString(options.Simulation.JournalPath);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;

    private static string BuildConnectionString(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Private, not Shared. Shared-cache locking fails on a network filesystem, and the
            // journal is single-writer anyway: writes are serialised by _writeGate, and the
            // simulator is deliberately capped at one replica.
            Cache = SqliteCacheMode.Private,

            // A pooled connection keeps the file handle — and its locks — open after use. At a few
            // small writes a minute there is nothing to gain from pooling, and releasing the file
            // promptly is what makes the database safe to snapshot and replace.
            Pooling = false
        }.ToString();
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        RestoreFromSnapshot();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS activity_journal (
                row_id       INTEGER PRIMARY KEY AUTOINCREMENT,
                intent_id    TEXT    NOT NULL,
                occurred_utc TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                actor_upn    TEXT    NOT NULL,
                storyline_id TEXT    NULL,
                topic        TEXT    NOT NULL,
                outcome      TEXT    NOT NULL,
                resource_id  TEXT    NULL,
                purge_path   TEXT    NULL,
                detail       TEXT    NULL,
                error        TEXT    NULL,
                web_link     TEXT    NULL,
                purged       INTEGER NOT NULL DEFAULT 0
            );
            """, cancellationToken).ConfigureAwait(false);

        await AddColumnIfMissingAsync(connection, "web_link", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "CREATE INDEX IF NOT EXISTS ix_journal_occurred ON activity_journal (occurred_utc);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "CREATE INDEX IF NOT EXISTS ix_journal_actor_day ON activity_journal (actor_upn, occurred_utc);",
            cancellationToken).ConfigureAwait(false);

        // Intents are idempotent by id: replaying a plan must not duplicate activity.
        await ExecuteAsync(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_intent ON activity_journal (intent_id);",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAsync(ActivityIntent intent, ActivityResult result, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // A replayed intent updates its row rather than creating a second one.
            command.CommandText = """
                INSERT INTO activity_journal
                    (intent_id, occurred_utc, kind, actor_upn, storyline_id, topic, outcome,
                     resource_id, purge_path, detail, error, web_link, purged)
                VALUES
                    ($intent, $occurred, $kind, $actor, $storyline, $topic, $outcome,
                     $resource, $purge, $detail, $error, $weblink, 0)
                ON CONFLICT(intent_id) DO UPDATE SET
                    occurred_utc = excluded.occurred_utc,
                    outcome      = excluded.outcome,
                    resource_id  = COALESCE(excluded.resource_id, activity_journal.resource_id),
                    purge_path   = COALESCE(excluded.purge_path, activity_journal.purge_path),
                    web_link     = COALESCE(excluded.web_link, activity_journal.web_link),
                    detail       = excluded.detail,
                    error        = excluded.error;
                """;

            command.Parameters.AddWithValue("$intent", intent.Id);
            command.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$kind", intent.Kind.ToString());
            command.Parameters.AddWithValue("$actor", intent.Actor.UserPrincipalName);
            command.Parameters.AddWithValue("$storyline", (object?)intent.StorylineId ?? DBNull.Value);
            command.Parameters.AddWithValue("$topic", intent.Topic);
            command.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
            command.Parameters.AddWithValue("$resource", (object?)result.ResourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$purge", (object?)result.PurgePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)result.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$weblink", (object?)result.WebLink ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<JournalSummary> SummariseAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT kind, actor_upn, outcome
            FROM activity_journal
            WHERE occurred_utc >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));

        var summary = new JournalSummaryBuilder();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = Enum.TryParse<ActivityKind>(reader.GetString(0), out var parsedKind)
                ? parsedKind
                : ActivityKind.SendMail;

            var outcome = Enum.TryParse<ActivityOutcome>(reader.GetString(2), out var parsedOutcome)
                ? parsedOutcome
                : ActivityOutcome.Failed;

            summary.Add(kind, reader.GetString(1), outcome);
        }

        return summary.Build();
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(
        JournalQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var filters = new List<string>();

        if (query.Since > DateTimeOffset.MinValue)
        {
            filters.Add("occurred_utc >= $since");
            command.Parameters.AddWithValue("$since", query.Since.ToString("O"));
        }

        if (!string.IsNullOrWhiteSpace(query.ActorUpn))
        {
            filters.Add("actor_upn = $actor COLLATE NOCASE");
            command.Parameters.AddWithValue("$actor", query.ActorUpn);
        }

        if (query.Kind is { } kind)
        {
            filters.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", kind.ToString());
        }

        if (query.Outcome is { } outcome)
        {
            filters.Add("outcome = $outcome");
            command.Parameters.AddWithValue("$outcome", outcome.ToString());
        }

        var where = filters.Count > 0 ? $"WHERE {string.Join(" AND ", filters)}" : string.Empty;

        command.CommandText = $"{SelectColumns} {where} ORDER BY row_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalEntry>> PurgeableAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            {SelectColumns}
            WHERE purged = 0
              AND purge_path IS NOT NULL
              AND outcome = 'Executed'
              AND occurred_utc >= $since
            ORDER BY row_id DESC;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkPurgedAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE activity_journal SET purged = 1 WHERE row_id = $id;";
            command.Parameters.AddWithValue("$id", entry.RowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> CountForActorOnDayAsync(
        string actorUpn,
        DateOnly utcDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var start = new DateTimeOffset(utcDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        command.CommandText = """
            SELECT COUNT(*)
            FROM activity_journal
            WHERE actor_upn = $actor
              AND outcome IN ('Executed', 'Simulated')
              AND occurred_utc >= $from
              AND occurred_utc < $to;
            """;
        command.Parameters.AddWithValue("$actor", actorUpn);
        command.Parameters.AddWithValue("$from", start.ToString("O"));
        command.Parameters.AddWithValue("$to", start.AddDays(1).ToString("O"));

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> HasExecutedAsync(ActivityIntent intent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM activity_journal
            WHERE intent_id = $intent AND outcome = 'Executed';
            """;
        command.Parameters.AddWithValue("$intent", intent.Id);

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private const string SelectColumns = """
        SELECT row_id, intent_id, occurred_utc, kind, actor_upn, storyline_id, topic,
               outcome, resource_id, purge_path, detail, error, purged, web_link
        FROM activity_journal
        """;

    /// <summary>
    /// Adds a column to a journal that predates it. <c>CREATE TABLE IF NOT EXISTS</c> leaves an
    /// existing table alone, so a database written by an older build would otherwise keep failing
    /// on the new column.
    /// </summary>
    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string column,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('activity_journal') WHERE name = $name;";
        check.Parameters.AddWithValue("$name", column);

        var scalar = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        await ExecuteAsync(connection, $"ALTER TABLE activity_journal ADD COLUMN {column} TEXT NULL;",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JournalEntry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var entries = new List<JournalEntry>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new JournalEntry
            {
                RowId = reader.GetInt64(0),
                IntentId = reader.GetString(1),
                OccurredUtc = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                Kind = Enum.TryParse<ActivityKind>(reader.GetString(3), out var k) ? k : ActivityKind.SendMail,
                ActorUpn = reader.GetString(4),
                StorylineId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Topic = reader.GetString(6),
                Outcome = Enum.TryParse<ActivityOutcome>(reader.GetString(7), out var o) ? o : ActivityOutcome.Failed,
                ResourceId = reader.IsDBNull(8) ? null : reader.GetString(8),
                PurgePath = reader.IsDBNull(9) ? null : reader.GetString(9),
                Detail = reader.IsDBNull(10) ? null : reader.GetString(10),
                Error = reader.IsDBNull(11) ? null : reader.GetString(11),
                Purged = reader.GetInt32(12) != 0,
                WebLink = reader.IsDBNull(13) ? null : reader.GetString(13),
                StorageKey = reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        return entries;
    }

    /// <summary>
    /// Brings back the durable copy when the working journal is missing — which is every cold start
    /// in a container, because the working copy lives on disposable local disk.
    /// </summary>
    private void RestoreFromSnapshot()
    {
        var snapshot = options.Simulation.JournalSnapshotPath;
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return;
        }

        var working = Path.GetFullPath(options.Simulation.JournalPath);
        var durable = Path.GetFullPath(snapshot);

        if (string.Equals(working, durable, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(working) || !File.Exists(durable))
        {
            return;
        }

        var directory = Path.GetDirectoryName(working);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(durable, working, overwrite: false);
    }

    /// <inheritdoc />
    public async Task SnapshotAsync(CancellationToken cancellationToken, bool force = false)
    {
        var snapshot = options.Simulation.JournalSnapshotPath;
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return;
        }

        var working = Path.GetFullPath(options.Simulation.JournalPath);
        var durable = Path.GetFullPath(snapshot);

        if (string.Equals(working, durable, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.Simulation.JournalSnapshotIntervalSeconds));
            if (!force && DateTimeOffset.UtcNow - _lastSnapshot < interval)
            {
                return;
            }

            var durableDirectory = Path.GetDirectoryName(durable);
            if (!string.IsNullOrWhiteSpace(durableDirectory))
            {
                Directory.CreateDirectory(durableDirectory);
            }

            // Stage beside the working database, never on the durable target. VACUUM INTO produces
            // a consistent copy of a live database, but it is still SQLite doing the writing, so it
            // needs a filesystem SQLite can lock — which an SMB share is not.
            var staging = working + ".snapshot";
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }

            await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $target;";
                command.Parameters.AddWithValue("$target", staging);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A plain stream copy, which SMB handles fine. Copy rather than move: the two paths are
            // usually on different filesystems.
            File.Copy(staging, durable, overwrite: true);
            File.Delete(staging);

            _lastSnapshot = DateTimeOffset.UtcNow;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The journal often lives on network storage — an Azure Files share when the simulator runs
        // in Container Apps, which is how it survives a restart and keeps purge possible.
        //
        // SMB cannot support the memory-mapped locking WAL needs, and SQLite reports the failure as
        // the famously unhelpful "database is locked". A rollback journal works over SMB, and a
        // busy timeout absorbs the slower lock handovers. Costs nothing locally: this journal takes
        // a handful of small writes per minute.
        await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=15000;", cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

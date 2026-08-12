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
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
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
                purged       INTEGER NOT NULL DEFAULT 0
            );
            """, cancellationToken).ConfigureAwait(false);

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
                     resource_id, purge_path, detail, error, purged)
                VALUES
                    ($intent, $occurred, $kind, $actor, $storyline, $topic, $outcome,
                     $resource, $purge, $detail, $error, 0)
                ON CONFLICT(intent_id) DO UPDATE SET
                    occurred_utc = excluded.occurred_utc,
                    outcome      = excluded.outcome,
                    resource_id  = COALESCE(excluded.resource_id, activity_journal.resource_id),
                    purge_path   = COALESCE(excluded.purge_path, activity_journal.purge_path),
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

        var byKind = new Dictionary<ActivityKind, int>();
        var byActor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0, executed = 0, simulated = 0, skipped = 0, failed = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total++;

            if (Enum.TryParse<ActivityKind>(reader.GetString(0), out var kind))
            {
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            }

            var actor = reader.GetString(1);
            byActor[actor] = byActor.GetValueOrDefault(actor) + 1;

            switch (reader.GetString(2))
            {
                case nameof(ActivityOutcome.Executed): executed++; break;
                case nameof(ActivityOutcome.Simulated): simulated++; break;
                case nameof(ActivityOutcome.Skipped): skipped++; break;
                case nameof(ActivityOutcome.Failed): failed++; break;
            }
        }

        return new JournalSummary(total, executed, simulated, skipped, failed, byKind, byActor);
    }

    public async Task<IReadOnlyList<JournalEntry>> RecentAsync(int count, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"{SelectColumns} ORDER BY row_id DESC LIMIT $count;";
        command.Parameters.AddWithValue("$count", count);

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

    public async Task MarkPurgedAsync(long rowId, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE activity_journal SET purged = 1 WHERE row_id = $id;";
            command.Parameters.AddWithValue("$id", rowId);
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

    public async Task<bool> HasExecutedAsync(string intentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM activity_journal
            WHERE intent_id = $intent AND outcome = 'Executed';
            """;
        command.Parameters.AddWithValue("$intent", intentId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private const string SelectColumns = """
        SELECT row_id, intent_id, occurred_utc, kind, actor_upn, storyline_id, topic,
               outcome, resource_id, purge_path, detail, error, purged
        FROM activity_journal
        """;

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
                Purged = reader.GetInt32(12) != 0
            });
        }

        return entries;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

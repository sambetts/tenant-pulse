using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;

namespace TenantPulse.Engine.Journaling;

/// <summary>
/// Azure Table Storage activity journal.
/// <para>
/// The reason this exists: the SQLite journal is only readable from wherever the database file
/// happens to be, which in a container is disposable local disk. A table is readable from anywhere
/// the operator can reach the storage account, so <c>report</c> and <c>purge</c> work from a laptop
/// against a simulator running in Azure — and it removes the SMB snapshot dance entirely, because a
/// table is already durable.
/// </para>
/// <para>
/// Layout: partition per UTC day of the intent's <em>scheduled</em> time, row key per intent id.
/// Scheduled rather than recorded time because it is stable across replays, which keeps the upsert
/// idempotent, and because it makes both hot-path reads — "has this intent run?" and "how much has
/// this persona done today?" — single-partition queries rather than table scans.
/// </para>
/// </summary>
public sealed class AzureTableActivityJournal : IActivityJournal
{
    /// <summary>
    /// How far back a newest-first read will walk looking for entries. Reads step backwards one day
    /// partition at a time, so an unbounded query with nothing to find would page forever.
    /// </summary>
    private const int MaxLookbackDays = 400;

    private readonly TableClient _table;

    public AzureTableActivityJournal(TenantPulseOptions options)
    {
        var journal = options.Simulation.JournalTable;
        var tableName = string.IsNullOrWhiteSpace(journal.TableName) ? "TenantPulseJournal" : journal.TableName;

        // A connection string covers the emulator and quick local runs. An endpoint plus Entra
        // credentials is what production uses, because shared-key access to storage is commonly
        // switched off by policy.
        _table = !string.IsNullOrWhiteSpace(journal.ConnectionString)
            ? new TableClient(journal.ConnectionString, tableName)
            : !string.IsNullOrWhiteSpace(journal.Endpoint)
                ? new TableClient(new Uri(journal.Endpoint), tableName, new DefaultAzureCredential())
                : throw new InvalidOperationException(
                    "The Azure Table journal needs either Simulation:JournalTable:ConnectionString " +
                    "or Simulation:JournalTable:Endpoint.");
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken) =>
        await _table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

    public async Task RecordAsync(
        ActivityIntent intent,
        ActivityResult result,
        CancellationToken cancellationToken)
    {
        var entity = new TableEntity(Partition(intent.ScheduledUtc), Row(intent.Id))
        {
            ["IntentId"] = intent.Id,
            ["OccurredUtc"] = DateTimeOffset.UtcNow,
            ["ScheduledUtc"] = intent.ScheduledUtc,
            ["Kind"] = intent.Kind.ToString(),
            ["ActorUpn"] = intent.Actor.UserPrincipalName,
            ["StorylineId"] = intent.StorylineId,
            ["Topic"] = intent.Topic,
            ["Outcome"] = result.Outcome.ToString(),
            ["ResourceId"] = result.ResourceId,
            ["PurgePath"] = result.PurgePath,
            ["Detail"] = result.Detail,
            ["Error"] = result.Error,
            ["WebLink"] = result.WebLink,
            ["Purged"] = false
        };

        // Replace, not merge: a replayed intent should end up with exactly the new outcome rather
        // than a blend of both attempts.
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JournalSummary> SummariseAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var summary = new JournalSummaryBuilder();

        // Aggregation does not care about order, so this is one server-side filtered scan rather
        // than the day-by-day walk the newest-first reads need.
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {Partition(since)}");

        await foreach (var entity in _table
            .QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var entry = ToEntry(entity);
            if (entry.OccurredUtc < since)
            {
                continue;
            }

            summary.Add(entry.Kind, entry.ActorUpn, entry.Outcome);
        }

        return summary.Build();
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(
        JournalQuery query,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.ActorUpn))
        {
            conditions.Add(TableClient.CreateQueryFilter($"ActorUpn eq {query.ActorUpn}"));
        }

        if (query.Kind is { } kind)
        {
            conditions.Add(TableClient.CreateQueryFilter($"Kind eq {kind.ToString()}"));
        }

        if (query.Outcome is { } outcome)
        {
            conditions.Add(TableClient.CreateQueryFilter($"Outcome eq {outcome.ToString()}"));
        }

        var results = new List<JournalEntry>();
        var limit = Math.Max(1, query.Limit);

        await foreach (var entry in ReadNewestFirstAsync(query.Since, conditions, cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(entry);
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<JournalEntry>> PurgeableAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>
        {
            TableClient.CreateQueryFilter($"Purged eq {false}"),
            TableClient.CreateQueryFilter($"Outcome eq {nameof(ActivityOutcome.Executed)}")
        };

        var results = new List<JournalEntry>();

        await foreach (var entry in ReadNewestFirstAsync(since, conditions, cancellationToken)
            .ConfigureAwait(false))
        {
            // Not every executed activity leaves something deletable behind — reading mail, for
            // instance. Filtered here because a table query cannot test for an absent property.
            if (!string.IsNullOrWhiteSpace(entry.PurgePath))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    public async Task MarkPurgedAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var (partition, row) = SplitStorageKey(entry);

        try
        {
            await _table.UpdateEntityAsync(
                new TableEntity(partition, row) { ["Purged"] = true },
                ETag.All,
                TableUpdateMode.Merge,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The row has gone. Purge is about reaching a clean tenant, not a clean journal.
        }
    }

    public async Task<int> CountForActorOnDayAsync(
        string actorUpn,
        DateOnly utcDay,
        CancellationToken cancellationToken)
    {
        var day = utcDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {day} and ActorUpn eq {actorUpn}");

        var count = 0;

        await foreach (var entity in _table
            .QueryAsync<TableEntity>(filter, select: ["Outcome"], cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var outcome = entity.GetString("Outcome");
            if (outcome is nameof(ActivityOutcome.Executed) or nameof(ActivityOutcome.Simulated))
            {
                count++;
            }
        }

        return count;
    }

    public async Task<bool> HasExecutedAsync(ActivityIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(
                Partition(intent.ScheduledUtc),
                Row(intent.Id),
                select: ["Outcome"],
                cancellationToken).ConfigureAwait(false);

            return response.Value.GetString("Outcome") == nameof(ActivityOutcome.Executed);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>No-op: a table is already durable, which is most of the point of using one.</summary>
    public Task SnapshotAsync(CancellationToken cancellationToken, bool force = false) =>
        Task.CompletedTask;

    /// <summary>
    /// Walks day partitions backwards from today, newest first. Azure Table only ever returns rows
    /// in ascending partition/row key order, so "most recent 20" has to be assembled this way —
    /// but each step is a cheap single-partition query, and callers stop as soon as they have
    /// enough.
    /// </summary>
    private async IAsyncEnumerable<JournalEntry> ReadNewestFirstAsync(
        DateTimeOffset since,
        IReadOnlyList<string> conditions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var floor = since <= DateTimeOffset.MinValue
            ? today.AddDays(-MaxLookbackDays)
            : since.UtcDateTime.Date;

        if (floor < today.AddDays(-MaxLookbackDays))
        {
            floor = today.AddDays(-MaxLookbackDays);
        }

        // A day's activity is scheduled in that day's partition but can be recorded just after
        // midnight, so start one day ahead of today to avoid missing those late rows.
        for (var day = today.AddDays(1); day >= floor; day = day.AddDays(-1))
        {
            var partition = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            var all = new List<string>(conditions.Count + 1)
            {
                TableClient.CreateQueryFilter($"PartitionKey eq {partition}")
            };
            all.AddRange(conditions);

            var page = new List<JournalEntry>();

            await foreach (var entity in _table
                .QueryAsync<TableEntity>(string.Join(" and ", all), cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                var entry = ToEntry(entity);
                if (entry.OccurredUtc >= since)
                {
                    page.Add(entry);
                }
            }

            foreach (var entry in page.OrderByDescending(e => e.OccurredUtc))
            {
                yield return entry;
            }
        }
    }

    private static string Partition(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Azure Table forbids <c>/ \ # ?</c> and control characters in a row key. Intent ids are
    /// generated, not user input, but they are the one part of this layout the planner controls.
    /// </summary>
    private static string Row(string intentId)
    {
        Span<char> buffer = stackalloc char[intentId.Length];

        for (var i = 0; i < intentId.Length; i++)
        {
            var c = intentId[i];
            buffer[i] = c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '_' : c;
        }

        return new string(buffer);
    }

    private static (string Partition, string Row) SplitStorageKey(JournalEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.StorageKey))
        {
            var separator = entry.StorageKey.IndexOf('|', StringComparison.Ordinal);
            if (separator > 0)
            {
                return (entry.StorageKey[..separator], entry.StorageKey[(separator + 1)..]);
            }
        }

        // An entry that did not come from this store: the recorded day is the best guess available.
        return (Partition(entry.OccurredUtc), Row(entry.IntentId));
    }

    private static JournalEntry ToEntry(TableEntity entity) => new()
    {
        IntentId = entity.GetString("IntentId") ?? entity.RowKey,
        OccurredUtc = entity.GetDateTimeOffset("OccurredUtc") ?? entity.Timestamp ?? DateTimeOffset.MinValue,
        Kind = Enum.TryParse<ActivityKind>(entity.GetString("Kind"), out var kind) ? kind : ActivityKind.SendMail,
        ActorUpn = entity.GetString("ActorUpn") ?? string.Empty,
        StorylineId = entity.GetString("StorylineId"),
        Topic = entity.GetString("Topic") ?? string.Empty,
        Outcome = Enum.TryParse<ActivityOutcome>(entity.GetString("Outcome"), out var outcome)
            ? outcome
            : ActivityOutcome.Failed,
        ResourceId = entity.GetString("ResourceId"),
        PurgePath = entity.GetString("PurgePath"),
        Detail = entity.GetString("Detail"),
        Error = entity.GetString("Error"),
        WebLink = entity.GetString("WebLink"),
        Purged = entity.GetBoolean("Purged") ?? false,
        StorageKey = $"{entity.PartitionKey}|{entity.RowKey}"
    };
}

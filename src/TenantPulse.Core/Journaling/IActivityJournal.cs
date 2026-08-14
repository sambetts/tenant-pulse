using TenantPulse.Core.Activities;

namespace TenantPulse.Core.Journaling;

/// <summary>One recorded simulated action.</summary>
public sealed record JournalEntry
{
    public long RowId { get; init; }

    public required string IntentId { get; init; }

    public required DateTimeOffset OccurredUtc { get; init; }

    public required ActivityKind Kind { get; init; }

    public required string ActorUpn { get; init; }

    public string? StorylineId { get; init; }

    public required string Topic { get; init; }

    public required ActivityOutcome Outcome { get; init; }

    /// <summary>Identifier of the created artefact, when the activity created one.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Graph path used to delete the artefact during purge.</summary>
    public string? PurgePath { get; init; }

    /// <summary>Browser link to the artefact, so a report can point straight at it.</summary>
    public string? WebLink { get; init; }

    public string? Detail { get; init; }

    public string? Error { get; init; }

    /// <summary>Set once the artefact has been removed by <c>purge</c>.</summary>
    public bool Purged { get; init; }

    /// <summary>
    /// Opaque address of this row in whichever store produced it, so it can be updated later
    /// without the caller knowing the storage layout. SQLite uses <see cref="RowId"/>; Azure Table
    /// uses a partition/row key pair. Null for entries that were not read from a store.
    /// </summary>
    public string? StorageKey { get; init; }
}

/// <summary>What one persona did over the reported window, split by outcome.</summary>
public sealed record ActorTally(
    string ActorUpn,
    int Total,
    int Executed,
    int Simulated,
    int Skipped,
    int Failed);

public sealed record JournalSummary(
    int Total,
    int Executed,
    int Simulated,
    int Skipped,
    int Failed,
    IReadOnlyDictionary<ActivityKind, int> ByKind,
    IReadOnlyList<ActorTally> ByActor);

/// <summary>
/// Filter for reading the journal back. Every field is optional; the default reads the most recent
/// activity regardless of persona, kind or outcome.
/// </summary>
public sealed record JournalQuery
{
    /// <summary>Only entries recorded at or after this instant.</summary>
    public DateTimeOffset Since { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Restrict to one persona. Matched case-insensitively.</summary>
    public string? ActorUpn { get; init; }

    public ActivityKind? Kind { get; init; }

    public ActivityOutcome? Outcome { get; init; }

    /// <summary>Maximum number of entries to return, newest first.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// Durable record of everything tenant-pulse has done. Two jobs: it lets you report on what the
/// simulator has been up to, and it makes the whole thing reversible — every artefact that can be
/// deleted is recorded with the path needed to delete it.
/// </summary>
public interface IActivityJournal
{
    Task InitialiseAsync(CancellationToken cancellationToken);

    Task RecordAsync(ActivityIntent intent, ActivityResult result, CancellationToken cancellationToken);

    Task<JournalSummary> SummariseAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken);

    /// <summary>Entries that created a deletable artefact and have not yet been purged.</summary>
    Task<IReadOnlyList<JournalEntry>> PurgeableAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Flags an entry as purged. Takes the entry rather than an id because the two backing stores
    /// address a row differently — SQLite by <see cref="JournalEntry.RowId"/>, Azure Table by the
    /// partition/row key pair derived from <see cref="JournalEntry.OccurredUtc"/> and
    /// <see cref="JournalEntry.IntentId"/>.
    /// </summary>
    Task MarkPurgedAsync(JournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Count of activities already performed by a persona on a given UTC day.</summary>
    Task<int> CountForActorOnDayAsync(string actorUpn, DateOnly utcDay, CancellationToken cancellationToken);

    /// <summary>
    /// True when this intent has already been executed (makes replays idempotent). Takes the whole
    /// intent, not just its id, because a partitioned store needs the scheduled day to turn this
    /// into a point lookup rather than a table scan — and this runs for every planned activity.
    /// </summary>
    Task<bool> HasExecutedAsync(ActivityIntent intent, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the journal to its configured durable location, if one is set.
    /// <para>
    /// Exists because SQLite cannot run directly on an SMB file share — the byte-range locking it
    /// needs is unsupported, and every statement fails with "database is locked". So in a container
    /// the live journal sits on fast local disk and is snapshotted onto the mounted share, which is
    /// what keeps <c>purge</c> able to clean up after a restart.
    /// </para>
    /// </summary>
    /// <param name="force">Ignore the debounce interval. Used on shutdown.</param>
    Task SnapshotAsync(CancellationToken cancellationToken, bool force = false);
}

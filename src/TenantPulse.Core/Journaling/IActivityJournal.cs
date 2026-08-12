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

    public string? Detail { get; init; }

    public string? Error { get; init; }

    /// <summary>Set once the artefact has been removed by <c>purge</c>.</summary>
    public bool Purged { get; init; }
}

public sealed record JournalSummary(
    int Total,
    int Executed,
    int Simulated,
    int Skipped,
    int Failed,
    IReadOnlyDictionary<ActivityKind, int> ByKind,
    IReadOnlyDictionary<string, int> ByActor);

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

    Task<IReadOnlyList<JournalEntry>> RecentAsync(int count, CancellationToken cancellationToken);

    /// <summary>Entries that created a deletable artefact and have not yet been purged.</summary>
    Task<IReadOnlyList<JournalEntry>> PurgeableAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task MarkPurgedAsync(long rowId, CancellationToken cancellationToken);

    /// <summary>Count of activities already performed by a persona on a given UTC day.</summary>
    Task<int> CountForActorOnDayAsync(string actorUpn, DateOnly utcDay, CancellationToken cancellationToken);

    /// <summary>True when this intent has already been executed (makes replays idempotent).</summary>
    Task<bool> HasExecutedAsync(string intentId, CancellationToken cancellationToken);
}

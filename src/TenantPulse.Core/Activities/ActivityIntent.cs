using TenantPulse.Core.Personas;

namespace TenantPulse.Core.Activities;

/// <summary>
/// A single scheduled unit of simulated work: "at 09:42, Megan emails Alex about the Contoso RFP".
/// Intents are produced by the planner and consumed by executors; they carry everything the
/// executor and the content generator need, and nothing tenant-specific.
/// </summary>
public sealed record ActivityIntent
{
    public required string Id { get; init; }

    /// <summary>When this should happen (UTC).</summary>
    public required DateTimeOffset ScheduledUtc { get; init; }

    public required ActivityKind Kind { get; init; }

    /// <summary>The persona performing the activity.</summary>
    public required Persona Actor { get; init; }

    /// <summary>Recipients / participants. May be empty (e.g. a Copilot prompt).</summary>
    public IReadOnlyList<Persona> Targets { get; init; } = [];

    /// <summary>The storyline this intent belongs to, if any. Free-form activity has none.</summary>
    public string? StorylineId { get; init; }

    public string? BeatId { get; init; }

    /// <summary>Short human topic used to seed content generation, e.g. "Contoso RFP pricing review".</summary>
    public required string Topic { get; init; }

    /// <summary>
    /// Extra hints for the content generator and executor (e.g. "tone":"urgent",
    /// "channel":"General", "fileName":"Q3-forecast.docx").
    /// </summary>
    public IReadOnlyDictionary<string, string> Hints { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Workload Workload => Kind.ToWorkload();

    public string? Hint(string key) => Hints.TryGetValue(key, out var v) ? v : null;

    public override string ToString() =>
        $"{ScheduledUtc:u} {Kind} by {Actor.UserPrincipalName}" +
        (Targets.Count > 0 ? $" → {string.Join(", ", Targets.Select(t => t.UserPrincipalName))}" : string.Empty) +
        $" [{Topic}]";
}

public enum ActivityOutcome
{
    /// <summary>The activity was performed against the tenant.</summary>
    Executed,

    /// <summary>Dry run — nothing was written to the tenant.</summary>
    Simulated,

    /// <summary>Deliberately not performed (rate limited, no licence, nothing to reply to, ...).</summary>
    Skipped,

    /// <summary>Attempted and failed.</summary>
    Failed
}

/// <summary>
/// The result of attempting one <see cref="ActivityIntent"/>.
/// </summary>
public sealed record ActivityResult
{
    public required ActivityOutcome Outcome { get; init; }

    /// <summary>
    /// Identifier of the thing created in the tenant (message id, driveItem id, event id...),
    /// recorded in the journal so <c>purge</c> can remove it later.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>Graph resource path used to delete the artefact during purge, when deletable.</summary>
    public string? PurgePath { get; init; }

    /// <summary>
    /// Browser link to the artefact — the <c>webUrl</c> or <c>webLink</c> Graph returns when it
    /// creates something. Recorded so a report can point straight at the mail, document, meeting or
    /// Teams message rather than just naming it.
    /// </summary>
    public string? WebLink { get; init; }

    public string? Detail { get; init; }

    public string? Error { get; init; }

    public static ActivityResult Executed(
        string? resourceId = null,
        string? purgePath = null,
        string? detail = null,
        string? webLink = null) =>
        new()
        {
            Outcome = ActivityOutcome.Executed,
            ResourceId = resourceId,
            PurgePath = purgePath,
            Detail = detail,
            WebLink = webLink
        };

    public static ActivityResult Simulated(string? detail = null) =>
        new() { Outcome = ActivityOutcome.Simulated, Detail = detail };

    public static ActivityResult Skipped(string reason) =>
        new() { Outcome = ActivityOutcome.Skipped, Detail = reason };

    public static ActivityResult Failed(string error) =>
        new() { Outcome = ActivityOutcome.Failed, Error = error };
}

/// <summary>
/// Performs one kind of simulated activity against the tenant.
/// Implementations must honour <see cref="ExecutionContext.DryRun"/> and must never throw for
/// an expected condition — return <see cref="ActivityResult.Skipped"/> instead.
/// </summary>
public interface IActivityExecutor
{
    ActivityKind Kind { get; }

    Task<ActivityResult> ExecuteAsync(ActivityIntent intent, ExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Ambient state passed to every executor.
/// </summary>
public sealed record ExecutionContext
{
    /// <summary>When true, executors must not write anything to the tenant.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Deterministic seed for this run, so a given seed replays the same simulation.</summary>
    public required int Seed { get; init; }
}

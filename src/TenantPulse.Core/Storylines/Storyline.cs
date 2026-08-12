using TenantPulse.Core.Activities;

namespace TenantPulse.Core.Storylines;

/// <summary>
/// A named part played within a storyline (e.g. "bid-lead", "pricing-analyst"). Roles are matched
/// to real personas once, at storyline casting time, so the same people stay involved throughout —
/// which is what makes the resulting mail/chat/files read like a real thread of work.
/// </summary>
public sealed record StorylineRole
{
    public required string Name { get; init; }

    /// <summary>Archetypes that can plausibly play this role. Empty = anyone.</summary>
    public IReadOnlyList<PersonaArchetypeName> PreferredArchetypes { get; init; } = [];

    /// <summary>Optional department hint used to prefer a more plausible casting.</summary>
    public string? PreferredDepartment { get; init; }
}

/// <summary>
/// Archetype reference by name so storyline JSON stays readable and tolerant of unknown values.
/// </summary>
public readonly record struct PersonaArchetypeName(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// One scripted moment in a storyline.
/// </summary>
public sealed record StorylineBeat
{
    public required string Id { get; init; }

    /// <summary>Day offset from the storyline's start; multiple beats can share a day.</summary>
    public int DayOffset { get; init; }

    public required ActivityKind Kind { get; init; }

    /// <summary>Role name of whoever performs the activity.</summary>
    public required string ActorRole { get; init; }

    /// <summary>Role names of recipients/participants.</summary>
    public IReadOnlyList<string> TargetRoles { get; init; } = [];

    /// <summary>Human-readable topic; supports {role} placeholders resolved at casting time.</summary>
    public required string Topic { get; init; }

    /// <summary>Preferred local hour-of-day (24h). When null the scheduler picks a plausible slot.</summary>
    public int? PreferredHour { get; init; }

    public IReadOnlyDictionary<string, string> Hints { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A multi-day thread of business activity — an RFP, a product launch, an office move.
/// Storylines are what stop the tenant looking like a random noise generator: the same cast of
/// people work on the same named things across mail, Teams, files and Copilot prompts.
/// </summary>
public sealed record Storyline
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>One-paragraph description handed to the content generator as background.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<StorylineRole> Roles { get; init; } = [];

    public IReadOnlyList<StorylineBeat> Beats { get; init; } = [];

    /// <summary>Relative likelihood this storyline is chosen when starting a new one.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>Total days the storyline spans, derived from its beats.</summary>
    public int DurationDays => Beats.Count == 0 ? 0 : Beats.Max(b => b.DayOffset) + 1;
}

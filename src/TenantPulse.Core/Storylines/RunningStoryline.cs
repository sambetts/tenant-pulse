using TenantPulse.Core.Personas;

namespace TenantPulse.Core.Storylines;

/// <summary>
/// A storyline that has been cast with real personas and pinned to real dates.
/// </summary>
public sealed record RunningStoryline
{
    public required string InstanceId { get; init; }

    public required Storyline Storyline { get; init; }

    /// <summary>The date the storyline's day-0 beats occur on.</summary>
    public required DateOnly StartDate { get; init; }

    /// <summary>Role name → the persona playing it.</summary>
    public required IReadOnlyDictionary<string, Persona> Cast { get; init; }

    public DateOnly EndDate => StartDate.AddDays(Math.Max(0, Storyline.DurationDays - 1));

    public bool IsActiveOn(DateOnly date) => date >= StartDate && date <= EndDate;

    public Persona? Actor(string role) => Cast.TryGetValue(role, out var p) ? p : null;

    /// <summary>Replaces {role} placeholders in a beat topic with the cast member's first name.</summary>
    public string ResolveTopic(string topic)
    {
        if (!topic.Contains('{', StringComparison.Ordinal))
        {
            return topic;
        }

        var resolved = topic;
        foreach (var (role, persona) in Cast)
        {
            resolved = resolved.Replace($"{{{role}}}", persona.FirstName, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }
}

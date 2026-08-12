using TenantPulse.Core.Personas;

namespace TenantPulse.Core.Storylines;

/// <summary>
/// Assigns real personas to storyline roles. Casting is deterministic for a given seed and
/// instance id, so re-planning the same day produces the same cast — important because a storyline
/// spans days and the same people must keep showing up in the same thread of work.
/// </summary>
public sealed class CastingDirector
{
    private readonly IReadOnlyList<Persona> _personas;

    public CastingDirector(IEnumerable<Persona> personas)
    {
        _personas = personas.Where(p => !p.Excluded).ToList();
    }

    /// <summary>
    /// Casts <paramref name="storyline"/>, preferring personas whose archetype/department match the
    /// role. Returns null when there are not enough distinct personas to fill every role.
    /// </summary>
    public RunningStoryline? Cast(Storyline storyline, DateOnly startDate, string instanceId, int seed)
    {
        if (_personas.Count == 0)
        {
            return null;
        }

        var rng = DeterministicRandom.For(seed, instanceId);
        var cast = new Dictionary<string, Persona>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in storyline.Roles)
        {
            var candidate = PickForRole(role, taken, rng);
            if (candidate is null)
            {
                return null;
            }

            cast[role.Name] = candidate;
            taken.Add(candidate.Id);
        }

        return new RunningStoryline
        {
            InstanceId = instanceId,
            Storyline = storyline,
            StartDate = startDate,
            Cast = cast
        };
    }

    private Persona? PickForRole(StorylineRole role, HashSet<string> taken, Random rng)
    {
        var available = _personas.Where(p => !taken.Contains(p.Id)).ToList();
        if (available.Count == 0)
        {
            return null;
        }

        var scored = available
            .Select(p => (Persona: p, Score: ScoreFor(p, role), Tie: rng.NextDouble()))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tie)
            .ToList();

        // Choose randomly among the joint-best candidates so casting varies between storylines.
        var best = scored[0].Score;
        var bestBand = scored.Where(x => Math.Abs(x.Score - best) < 0.001).ToList();
        return bestBand[rng.Next(bestBand.Count)].Persona;
    }

    private static double ScoreFor(Persona persona, StorylineRole role)
    {
        double score = 0;

        if (role.PreferredArchetypes.Count > 0)
        {
            var archetypeName = persona.Archetype.ToString();
            if (role.PreferredArchetypes.Any(a => string.Equals(a.Value, archetypeName, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2;
            }
        }
        else
        {
            score += 0.5;
        }

        if (!string.IsNullOrWhiteSpace(role.PreferredDepartment) &&
            !string.IsNullOrWhiteSpace(persona.Department) &&
            persona.Department.Contains(role.PreferredDepartment, StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }
}

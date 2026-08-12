using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;

namespace TenantPulse.Core.Storylines;

/// <summary>
/// Keeps a rolling set of storylines running: as one finishes another starts, so the tenant always
/// has several threads of work in flight. Deterministic for a given seed — the same day always
/// yields the same running storylines, which is what lets the planner be re-run safely.
/// </summary>
public sealed class StorylineScheduler(TenantPulseOptions options, IReadOnlyList<Storyline> catalogue)
{
    /// <summary>
    /// Returns the storylines that should be in flight on <paramref name="date"/>, cast with real
    /// personas. Storylines are staggered so they don't all start and end together.
    /// </summary>
    public IReadOnlyList<RunningStoryline> ActiveOn(DateOnly date, IReadOnlyList<Persona> personas)
    {
        if (catalogue.Count == 0 || personas.Count == 0)
        {
            return [];
        }

        var director = new CastingDirector(personas);
        var slots = Math.Max(1, options.Simulation.ConcurrentStorylines);
        var running = new List<RunningStoryline>();

        for (var slot = 0; slot < slots; slot++)
        {
            var instance = ResolveSlot(date, slot, director);
            if (instance is not null)
            {
                running.Add(instance);
            }
        }

        return running;
    }

    /// <summary>
    /// Each "slot" runs one storyline at a time, back to back. Given a date we work out which
    /// storyline that slot is currently on by walking deterministically from an epoch.
    /// </summary>
    private RunningStoryline? ResolveSlot(DateOnly date, int slot, CastingDirector director)
    {
        // Stagger slot start dates so storylines overlap rather than marching in lockstep.
        var epoch = new DateOnly(2026, 1, 5);
        var slotOffsetDays = slot * 5;
        var cursor = epoch.AddDays(slotOffsetDays);

        if (date < cursor)
        {
            cursor = date;
        }

        // Walk forward through consecutive storylines until we cover the requested date.
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var rng = DeterministicRandom.For(
                options.Simulation.Seed, "storyline-slot", slot.ToString(), cursor.ToString("O"));

            var storyline = rng.WeightedPick(catalogue, s => s.Weight);
            if (storyline is null)
            {
                return null;
            }

            var duration = Math.Max(1, storyline.DurationDays);
            var end = cursor.AddDays(duration - 1);

            if (date <= end)
            {
                var instanceId = $"slot{slot}:{storyline.Id}:{cursor:yyyyMMdd}";
                return director.Cast(storyline, cursor, instanceId, options.Simulation.Seed);
            }

            // Short breather between storylines, deterministically chosen.
            cursor = end.AddDays(1 + rng.Next(0, 3));
        }

        return null;
    }
}

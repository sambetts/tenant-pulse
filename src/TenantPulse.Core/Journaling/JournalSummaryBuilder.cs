using TenantPulse.Core.Activities;

namespace TenantPulse.Core.Journaling;

/// <summary>
/// Accumulates a <see cref="JournalSummary"/> one entry at a time. Shared so the SQLite and Azure
/// Table journals cannot drift into reporting the same run differently.
/// </summary>
public sealed class JournalSummaryBuilder
{
    private readonly Dictionary<ActivityKind, int> _byKind = [];
    private readonly Dictionary<string, int[]> _byActor = new(StringComparer.OrdinalIgnoreCase);

    private int _total;
    private int _executed;
    private int _simulated;
    private int _skipped;
    private int _failed;

    public void Add(ActivityKind kind, string actorUpn, ActivityOutcome outcome)
    {
        _total++;
        _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;

        if (!_byActor.TryGetValue(actorUpn, out var tally))
        {
            tally = new int[4];
            _byActor[actorUpn] = tally;
        }

        switch (outcome)
        {
            case ActivityOutcome.Executed: _executed++; tally[0]++; break;
            case ActivityOutcome.Simulated: _simulated++; tally[1]++; break;
            case ActivityOutcome.Skipped: _skipped++; tally[2]++; break;
            case ActivityOutcome.Failed: _failed++; tally[3]++; break;
        }
    }

    public JournalSummary Build()
    {
        var actors = _byActor
            .Select(a => new ActorTally(
                a.Key,
                a.Value[0] + a.Value[1] + a.Value[2] + a.Value[3],
                a.Value[0],
                a.Value[1],
                a.Value[2],
                a.Value[3]))
            .OrderByDescending(a => a.Total)
            .ThenBy(a => a.ActorUpn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new JournalSummary(_total, _executed, _simulated, _skipped, _failed, _byKind, actors);
    }
}

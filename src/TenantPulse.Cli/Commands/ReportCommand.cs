using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Journaling;

namespace TenantPulse.Cli.Commands;

/// <summary>Summarises what the simulator has actually done.</summary>
internal sealed class ReportCommand(IServiceProvider services, ILogger logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var journal = services.GetRequiredService<IActivityJournal>();
        await journal.InitialiseAsync(cancellationToken).ConfigureAwait(false);

        var days = Math.Max(1, commandLine.IntValue("since", 7));
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        var summary = await journal.SummariseAsync(since, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"tenant-pulse activity — last {days} day(s)");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"  Total       {summary.Total}");
        Console.WriteLine($"  Executed    {summary.Executed}");
        Console.WriteLine($"  Simulated   {summary.Simulated}  (dry run)");
        Console.WriteLine($"  Skipped     {summary.Skipped}");
        Console.WriteLine($"  Failed      {summary.Failed}");

        if (summary.ByKind.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  By activity");
            foreach (var (kind, count) in summary.ByKind.OrderByDescending(k => k.Value))
            {
                Console.WriteLine($"    {kind,-18} {count}");
            }
        }

        if (summary.ByActor.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Most active");
            foreach (var (actor, count) in summary.ByActor.OrderByDescending(a => a.Value).Take(10))
            {
                Console.WriteLine($"    {actor,-38} {count}");
            }
        }

        var recentCount = commandLine.IntValue("recent", 0);
        if (recentCount > 0)
        {
            var recent = await journal.RecentAsync(recentCount, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"  Most recent {recent.Count}");
            foreach (var entry in recent)
            {
                Console.WriteLine($"    {entry.OccurredUtc:yyyy-MM-dd HH:mm} {entry.Outcome,-10} " +
                                  $"{entry.Kind,-16} {entry.ActorUpn,-32} {entry.Topic}");
            }
        }

        Console.WriteLine();

        if (summary.Total == 0)
        {
            logger.LogInformation("Nothing recorded yet. Run 'tenant-pulse once --live' to get started.");
        }

        return 0;
    }
}

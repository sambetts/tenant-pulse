using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;
using TenantPulse.Core.Safety;
using TenantPulse.Engine.Graph;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Undoes the simulator. Every artefact tenant-pulse created was journalled together with the Graph
/// path needed to delete it, so a demo tenant can be handed back clean.
/// <para>
/// Lists by default; only deletes with <c>--live</c>.
/// </para>
/// </summary>
internal sealed class PurgeCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        Services.GetRequiredService<SafetyGovernor>().AssertTenantAllowed();

        var journal = Services.GetRequiredService<IActivityJournal>();
        await journal.InitialiseAsync(cancellationToken).ConfigureAwait(false);

        var days = Math.Max(1, commandLine.IntValue("since", 30));
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        var entries = await journal.PurgeableAsync(since, cancellationToken).ConfigureAwait(false);

        if (entries.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Nothing to purge in the last {days} day(s).");
            Console.WriteLine();
            return 0;
        }

        var live = !Options.Simulation.DryRun;

        Console.WriteLine();
        Console.WriteLine($"  {entries.Count} artefact(s) created in the last {days} day(s)" +
                          (live ? " — DELETING" : " — listing only (pass --live to delete)"));
        Console.WriteLine();

        if (!live)
        {
            foreach (var entry in entries.Take(50))
            {
                Console.WriteLine($"    {entry.OccurredUtc:yyyy-MM-dd HH:mm} {entry.Kind,-16} " +
                                  $"{entry.ActorUpn,-32} {entry.PurgePath}");
            }

            if (entries.Count > 50)
            {
                Console.WriteLine($"    … and {entries.Count - 50} more");
            }

            Console.WriteLine();
            return 0;
        }

        var graph = Services.GetRequiredService<IGraphClient>();
        int deleted = 0, failed = 0;

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested || entry.PurgePath is null)
            {
                break;
            }

            try
            {
                // A softDelete purge path is an action, not a resource, so it is POSTed.
                if (entry.PurgePath.EndsWith("/softDelete", StringComparison.OrdinalIgnoreCase))
                {
                    await graph.PostAsync(entry.ActorUpn, entry.PurgePath, new { }, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await graph.DeleteAsync(entry.ActorUpn, entry.PurgePath, cancellationToken)
                        .ConfigureAwait(false);
                }

                await journal.MarkPurgedAsync(entry, cancellationToken).ConfigureAwait(false);
                deleted++;
                Logger.LogDebug("Purged {Path}", entry.PurgePath);
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
                // Already gone — count it as done so it stops being reported.
                await journal.MarkPurgedAsync(entry, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                Logger.LogWarning("Could not purge {Path}: {Message}",
                    entry.PurgePath, ex.GetBaseException().Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  Purged {deleted}, failed {failed}.");
        Console.WriteLine();

        return failed == 0 ? 0 : 1;
    }
}

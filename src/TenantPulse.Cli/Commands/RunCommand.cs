using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Engine;

namespace TenantPulse.Cli.Commands;

/// <summary>The continuous mode: plays the plan out in real time, day after day.</summary>
internal sealed class RunCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);
        var storylines = await LoadStorylinesAsync(cancellationToken).ConfigureAwait(false);

        var usable = personas.Where(p => !p.Excluded).ToList();
        if (usable.Count == 0)
        {
            Logger.LogError("No usable personas — nothing to simulate.");
            return 1;
        }

        if (Options.Simulation.DryRun)
        {
            Logger.LogWarning(
                "DRY RUN: activity will be planned and logged but nothing will be written to the " +
                "tenant. Pass --live when you're ready.");
        }

        var engine = Services.GetRequiredService<PulseEngine>();
        await engine.RunAsync(usable, storylines, cancellationToken).ConfigureAwait(false);

        return 0;
    }
}

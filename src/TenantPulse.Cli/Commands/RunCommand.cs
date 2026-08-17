using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Cli.Admin;
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
        // Settings an operator changed in the admin UI outlive the container they changed them in,
        // so they have to be reapplied before the first day is planned.
        await ApplyPersistedSettingsAsync(cancellationToken).ConfigureAwait(false);

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

        var admin = await StartAdminAsync(commandLine, personas, storylines, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var engine = Services.GetRequiredService<PulseEngine>();
            await engine.RunAsync(usable, storylines, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (admin is not null)
            {
                await admin.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await admin.DisposeAsync().ConfigureAwait(false);
            }
        }

        return 0;
    }

    private async Task ApplyPersistedSettingsAsync(CancellationToken cancellationToken)
    {
        var store = Services.GetRequiredService<IRuntimeSettingsStore>();
        var persisted = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (persisted is null || persisted.IsEmpty)
        {
            return;
        }

        persisted.ApplyTo(Options);

        Logger.LogInformation(
            "Applied runtime settings saved by {By}: intensity {Intensity}, {PerUser}/user/day, " +
            "{PerHour}/tenant/hour.",
            persisted.UpdatedBy ?? "an operator",
            Options.Simulation.ActivityIntensity,
            Options.Limits.MaxActivitiesPerUserPerDay,
            Options.Limits.MaxActivitiesPerTenantPerHour);
    }

    /// <summary>
    /// Starts the admin web unless it is switched off. A failure here must never take the simulator
    /// down with it — the tenant looking lived-in matters more than being able to watch it.
    /// </summary>
    private async Task<Microsoft.AspNetCore.Builder.WebApplication?> StartAdminAsync(
        CommandLine commandLine,
        IReadOnlyList<Core.Personas.Persona> personas,
        IReadOnlyList<Core.Storylines.Storyline> storylines,
        CancellationToken cancellationToken)
    {
        if (commandLine.Has("no-admin"))
        {
            return null;
        }

        var port = commandLine.IntValue("admin-port", Options.Admin.Port);
        if (!Options.Admin.Enabled && !commandLine.Has("admin"))
        {
            return null;
        }

        try
        {
            var server = new AdminServer(Services, Options, personas, storylines, Logger);
            return await server.StartAsync(port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The admin web could not start; continuing without it.");
            return null;
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Safety;
using TenantPulse.Engine.Auth;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Enrols demo users so their activity can be attributed to them.
/// <para>
/// Device code is the supported route and needs one interactive sign-in per user — tedious for 25
/// users, but done once. Username/password enrols everyone unattended and usually works in a demo
/// tenant, at the cost of relying on a flow Microsoft has deprecated.
/// </para>
/// </summary>
internal sealed class BootstrapCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        Services.GetRequiredService<SafetyGovernor>().AssertTenantAllowed();

        var broker = Services.GetRequiredService<UserTokenBroker>();
        var targets = await ResolveTargetsAsync(commandLine, cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0)
        {
            Logger.LogError(
                "Nothing to enrol. Pass --user <upn>, or --all with --as <upn-of-an-already-enrolled-user>.");
            return 64;
        }

        Logger.LogInformation("Enrolling {Count} user(s) using {Mode}.", targets.Count, Options.Auth.Mode);

        var succeeded = 0;
        var failed = new List<string>();

        foreach (var upn in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (await broker.IsEnrolledAsync(upn, cancellationToken).ConfigureAwait(false))
            {
                Logger.LogInformation("· {Upn} already enrolled.", upn);
                succeeded++;
                continue;
            }

            try
            {
                if (Options.Auth.Mode == AuthMode.UsernamePassword)
                {
                    await broker.EnrolByPasswordAsync(upn, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await broker.EnrolByDeviceCodeAsync(upn, deviceCode =>
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  Sign in as {upn}:");
                        Console.WriteLine($"  {deviceCode.Message}");
                        Console.WriteLine();
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                }

                Logger.LogInformation("✓ {Upn} enrolled.", upn);
                succeeded++;
            }
            catch (UserNotEnrolledException ex)
            {
                Logger.LogWarning("✗ {Upn}: {Message}", upn, ex.Message);
                failed.Add(upn);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("✗ {Upn}: {Message}", upn, ex.GetBaseException().Message);
                failed.Add(upn);
            }
        }

        Logger.LogInformation("Enrolled {Succeeded}/{Total}.", succeeded, targets.Count);

        if (failed.Count > 0)
        {
            Logger.LogWarning("Failed: {Users}", string.Join(", ", failed));

            if (Options.Auth.Mode == AuthMode.UsernamePassword)
            {
                Logger.LogWarning(
                    "Username/password sign-in is blocked by MFA, Conditional Access and security " +
                    "defaults. If most users failed, switch Auth.Mode to DeviceCode.");
            }
        }

        return failed.Count == 0 ? 0 : 1;
    }

    private async Task<List<string>> ResolveTargetsAsync(
        CommandLine commandLine,
        CancellationToken cancellationToken)
    {
        if (commandLine.Value("user") is string single && !string.IsNullOrWhiteSpace(single))
        {
            return [single];
        }

        if (!commandLine.Has("all"))
        {
            return [];
        }

        // --all needs to read the directory, which itself needs a token. In username/password mode
        // we can mint one for the reader on the spot; in device-code mode somebody must already be
        // enrolled (or be named with --as and enrolled interactively first).
        var reader = await ResolveDirectoryReaderAsync(commandLine, cancellationToken).ConfigureAwait(false);

        if (reader is null)
        {
            Logger.LogError(
                "--all needs an enrolled user to read the directory. Enrol one first: " +
                "tenant-pulse bootstrap --user <upn>");
            return [];
        }

        if (Options.Auth.Mode == AuthMode.UsernamePassword)
        {
            var broker = Services.GetRequiredService<UserTokenBroker>();
            if (!await broker.IsEnrolledAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                await broker.EnrolByPasswordAsync(reader, cancellationToken).ConfigureAwait(false);
            }
        }

        var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);

        return [.. personas.Where(p => !p.Excluded).Select(p => p.UserPrincipalName)];
    }
}

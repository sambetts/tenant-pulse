using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Engine;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Runs a handful of activities right now, ignoring their scheduled times. This is the command you
/// use to prove the whole pipeline works before leaving <c>run</c> going for days.
/// </summary>
internal sealed class OnceCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);
        var storylines = await LoadStorylinesAsync(cancellationToken).ConfigureAwait(false);
        var engine = Services.GetRequiredService<PulseEngine>();

        var count = Math.Max(1, commandLine.IntValue("count", 5));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Look across a few days so a single quiet day doesn't starve the batch.
        var candidates = new List<ActivityIntent>();
        for (var offset = 0; offset < 5 && candidates.Count < count * 4; offset++)
        {
            candidates.AddRange(engine.PlanDay(today.AddDays(offset), personas, storylines));
        }

        if (commandLine.Value("kind") is string kindText && !string.IsNullOrWhiteSpace(kindText))
        {
            if (!Enum.TryParse<ActivityKind>(kindText, ignoreCase: true, out var kind))
            {
                Logger.LogError("Unknown activity kind '{Kind}'. Valid values: {Valid}",
                    kindText, string.Join(", ", Enum.GetNames<ActivityKind>()));
                return 64;
            }

            candidates = [.. candidates.Where(i => i.Kind == kind)];
        }

        if (commandLine.Value("user") is string upn && !string.IsNullOrWhiteSpace(upn))
        {
            candidates = [.. candidates.Where(i =>
                i.Actor.UserPrincipalName.Equals(upn, StringComparison.OrdinalIgnoreCase))];
        }

        var batch = candidates.Take(count).ToList();

        if (batch.Count == 0)
        {
            Logger.LogWarning("Nothing matched. Try a different --kind/--user, or check 'plan'.");
            return 1;
        }

        Logger.LogInformation("Executing {Count} activities immediately ({Mode}).",
            batch.Count, Options.Simulation.DryRun ? "dry run" : "LIVE");

        var results = await engine.RunBatchAsync(batch, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        foreach (var (intent, result) in results)
        {
            var symbol = result.Outcome switch
            {
                ActivityOutcome.Executed => "✓",
                ActivityOutcome.Simulated => "·",
                ActivityOutcome.Skipped => "–",
                _ => "✗"
            };

            Console.WriteLine($"  {symbol} {intent.Kind,-16} {intent.Actor.DisplayName,-22} {intent.Topic}");

            var note = result.Detail ?? result.Error;
            if (!string.IsNullOrWhiteSpace(note))
            {
                Console.WriteLine($"      {note}");
            }
        }

        Console.WriteLine();

        var failed = results.Count(r => r.Result.Outcome == ActivityOutcome.Failed);
        return failed == 0 ? 0 : 1;
    }
}

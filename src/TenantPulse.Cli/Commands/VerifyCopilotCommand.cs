using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Engine.Copilot;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Settles the one genuinely open question in this design: Microsoft documents neither that
/// Copilot Chat API prompts DO count as usage, nor that they don't. Rather than assume, this sends
/// a marked prompt and reads the interaction history back.
/// </summary>
internal sealed class VerifyCopilotCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var upn = commandLine.Value("user");

        if (string.IsNullOrWhiteSpace(upn))
        {
            var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);
            upn = personas.FirstOrDefault(p => !p.Excluded && p.HasCopilotLicence)?.UserPrincipalName;

            if (upn is null)
            {
                Logger.LogError(
                    "No Copilot-licensed user found. Pass --user <upn>, or add the Copilot add-on to " +
                    "the tenant in CDX.");
                return 1;
            }

            Logger.LogInformation("Using {Upn} (first Copilot-licensed persona).", upn);
        }

        if (Options.Simulation.DryRun)
        {
            Logger.LogWarning(
                "verify-copilot must write a real prompt to be meaningful. Re-run with --live.");
            return 1;
        }

        var verifier = Services.GetRequiredService<CopilotUsageVerifier>();
        var settle = TimeSpan.FromSeconds(Math.Max(5, commandLine.IntValue("wait", 60)));

        var appToken = commandLine.Value("app-token")
                       ?? Environment.GetEnvironmentVariable("TENANTPULSE_APP_TOKEN");

        var result = await verifier.VerifyAsync(
            upn,
            _ => Task.FromResult(appToken),
            settle,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Copilot usage verification");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"  User               {upn}");
        Console.WriteLine($"  Prompt sent        {(result.PromptSent ? "yes" : "no")}");
        Console.WriteLine($"  Conversation id    {result.ConversationId ?? "(none)"}");
        Console.WriteLine($"  Interactions read  {result.InteractionsFound}");
        Console.WriteLine($"  Marker found       {(result.FoundOurPrompt ? "YES" : "no")}");
        Console.WriteLine();
        Console.WriteLine($"  {result.Explanation}");
        Console.WriteLine();

        if (!result.PromptSent)
        {
            return 1;
        }

        if (!result.FoundOurPrompt)
        {
            Console.WriteLine("  Next steps if the marker never appears:");
            Console.WriteLine("    • Re-run with a longer --wait (the export pipeline can lag).");
            Console.WriteLine("    • Check the admin centre Copilot usage report tomorrow for this user.");
            Console.WriteLine("    • If it still doesn't register, set Copilot.UseGraphChatApi=false and");
            Console.WriteLine("      drive Copilot through the browser instead (see docs/copilot.md).");
            Console.WriteLine();
        }

        return 0;
    }
}

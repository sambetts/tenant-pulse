using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Safety;
using TenantPulse.Engine.Content;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Pre-flight check. Tells you precisely what is configured, what is missing and what would happen,
/// without touching the tenant. This is the first thing to run and the first thing to run again
/// when something stops working.
/// </summary>
internal sealed class DoctorCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    string configPath,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        Console.WriteLine();
        Console.WriteLine("tenant-pulse doctor");
        Console.WriteLine(new string('─', 60));

        // ---- configuration -------------------------------------------------
        Check("Config file", ConfigurationLoader.Exists(configPath)
            ? $"found ({configPath})"
            : $"NOT FOUND ({configPath})");

        if (!ConfigurationLoader.Exists(configPath))
        {
            problems.Add($"Create {configPath} — copy config/tenant-pulse.example.json and fill it in.");
        }

        Check("Tenant id", string.IsNullOrWhiteSpace(Options.Tenant.TenantId)
            ? "NOT SET"
            : Options.Tenant.TenantId);

        Check("Client id", string.IsNullOrWhiteSpace(Options.Tenant.ClientId)
            ? "NOT SET"
            : Options.Tenant.ClientId);

        if (string.IsNullOrWhiteSpace(Options.Tenant.ClientId))
        {
            problems.Add("Register a public-client Entra app and set Tenant.ClientId.");
        }

        // ---- the safety guardrail -------------------------------------------
        var governor = Services.GetRequiredService<SafetyGovernor>();
        try
        {
            governor.AssertTenantAllowed();
            Check("Tenant allow-list", "OK — target tenant is explicitly allowed");
        }
        catch (TenantNotAllowedException ex)
        {
            Check("Tenant allow-list", "REFUSED");
            problems.Add(ex.Message);
        }

        Check("Mode", Options.Simulation.DryRun
            ? "DRY RUN (nothing will be written) — pass --live to act"
            : "LIVE — activity will be written to the tenant");

        Check("Kill switch", governor.IsStopRequested()
            ? $"PRESENT ({Options.Simulation.KillSwitchFile}) — the simulator will not run"
            : $"clear ({Options.Simulation.KillSwitchFile})");

        Check("Limits", $"{Options.Limits.MaxActivitiesPerUserPerDay}/user/day, " +
                        $"{Options.Limits.MaxActivitiesPerTenantPerHour}/tenant/hour, " +
                        $"min {Options.Limits.MinSecondsBetweenUserActivities}s apart");

        // ---- content generation ---------------------------------------------
        if (Options.Content.Provider == ContentProvider.AzureOpenAI)
        {
            AzureOpenAIContentGenerator? generator = null;

            try
            {
                generator = new AzureOpenAIContentGenerator(
                    Options,
                    Services.GetRequiredService<ContentPromptBuilder>(),
                    Services.GetRequiredService<ILogger<AzureOpenAIContentGenerator>>());
            }
            catch (Exception ex)
            {
                Check("Content", "Azure OpenAI NOT USABLE — falling back to templates");
                warnings.Add($"Azure OpenAI is misconfigured, so content will fall back to templates: " +
                             $"{ex.GetBaseException().Message}");
            }

            if (generator is not null)
            {
                // Constructing the client proves nothing — a disabled key, a missing role
                // assignment or a wrong deployment name all construct perfectly and then fail on
                // every call, silently degrading to templates. So actually call it.
                try
                {
                    await generator.ProbeAsync(cancellationToken).ConfigureAwait(false);

                    Check("Content", $"Azure OpenAI ({generator.AuthenticationMode}) — " +
                                     $"{Options.Content.Deployment} @ {Options.Content.Endpoint}");
                }
                catch (Exception ex)
                {
                    Check("Content", $"Azure OpenAI ({generator.AuthenticationMode}) UNREACHABLE — " +
                                     "falling back to templates");
                    warnings.Add($"Azure OpenAI rejected a test prompt, so all content will fall back " +
                                 $"to templates: {ex.GetBaseException().Message}");

                    if (generator.AuthenticationMode == "API key")
                    {
                        warnings.Add("If that was 'AuthenticationTypeDisabled', the resource has " +
                                     "disableLocalAuth set. Set TenantPulse:Content:UseEntraAuth to true " +
                                     "and grant 'Cognitive Services OpenAI User' on the resource.");
                    }
                }
            }
        }
        else
        {
            Check("Content", "templates (no LLM)");
        }

        // ---- storylines -------------------------------------------------------
        try
        {
            var storylines = await LoadStorylinesAsync(cancellationToken).ConfigureAwait(false);
            Check("Storylines", $"{storylines.Count} loaded ({string.Join(", ", storylines.Take(3).Select(s => s.Id))}…)");

            if (storylines.Count == 0)
            {
                problems.Add("No storylines loaded — activity would be ambient noise only.");
            }
        }
        catch (Exception ex)
        {
            Check("Storylines", "FAILED");
            problems.Add($"Storyline catalogue could not be loaded: {ex.Message}");
        }

        // ---- enrolment --------------------------------------------------------
        var enrolled = CountEnrolledCaches();
        Check("Enrolled users", enrolled == 0
            ? "none — run 'tenant-pulse bootstrap'"
            : $"{enrolled} token cache(s) in {Options.Auth.CacheDirectory}");

        if (enrolled == 0)
        {
            problems.Add("No users are enrolled. Run 'tenant-pulse bootstrap --all --as <admin-upn>' " +
                         "(device code) or set Auth.Mode=UsernamePassword with a shared password.");
        }

        // ---- live directory probe --------------------------------------------
        if (enrolled > 0 && problems.Count == 0)
        {
            try
            {
                var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);
                var active = personas.Where(p => !p.Excluded).ToList();
                var copilot = active.Count(p => p.HasCopilotLicence);

                Check("Directory", $"{personas.Count} users, {active.Count} usable as personas, " +
                                   $"{copilot} Copilot-licensed");

                if (active.Count < 3)
                {
                    warnings.Add("Fewer than 3 usable personas — storylines need a cast and may not run.");
                }

                if (copilot == 0 && Options.Workloads.Copilot)
                {
                    warnings.Add("No user holds a Microsoft 365 Copilot licence, so no Copilot activity " +
                                 "will be generated. Add the Copilot add-on in CDX, or set Workloads.Copilot=false.");
                }

                var enrolledPersonas = await CountEnrolledPersonasAsync(active, cancellationToken)
                    .ConfigureAwait(false);

                Check("Ready to act", $"{enrolledPersonas}/{active.Count} personas have a usable token");

                if (enrolledPersonas < active.Count)
                {
                    warnings.Add($"{active.Count - enrolledPersonas} persona(s) still need enrolling — " +
                                 "they will be skipped until bootstrapped.");
                }
            }
            catch (Exception ex)
            {
                Check("Directory", "FAILED");
                problems.Add($"Could not read the directory: {ex.GetBaseException().Message}");
            }
        }

        // ---- verdict ------------------------------------------------------------
        Console.WriteLine();

        foreach (var warning in warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("  All checks passed. Try: tenant-pulse plan");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine("  Problems to fix:");
        foreach (var problem in problems)
        {
            Console.WriteLine($"    • {problem}");
        }

        Console.WriteLine();
        return 1;
    }

    private int CountEnrolledCaches()
    {
        if (!Directory.Exists(Options.Auth.CacheDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(Options.Auth.CacheDirectory, "*.msalcache").Count();
    }

    private async Task<int> CountEnrolledPersonasAsync(
        IReadOnlyList<Persona> personas,
        CancellationToken cancellationToken)
    {
        var tokenProvider = Services.GetRequiredService<IUserTokenProvider>();
        var count = 0;

        foreach (var persona in personas)
        {
            if (await tokenProvider.IsEnrolledAsync(persona.UserPrincipalName, cancellationToken)
                    .ConfigureAwait(false))
            {
                count++;
            }
        }

        return count;
    }

    private static void Check(string label, string value) =>
        Console.WriteLine($"  {label,-18} {value}");
}

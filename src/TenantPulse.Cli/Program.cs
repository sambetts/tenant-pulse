using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Cli;
using TenantPulse.Cli.Commands;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Safety;
using TenantPulse.Engine;

var commandLine = CommandLine.Parse(args);

if (commandLine.Command is "help" or "--help" or "-h" || commandLine.Has("help"))
{
    HelpCommand.Print();
    return 0;
}

var (options, configPath) = ConfigurationLoader.Load(commandLine.Value("config"), args);

ApplyOverrides(options, commandLine);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(commandLine.Has("verbose") ? LogLevel.Debug : LogLevel.Information);

    // MSAL and HttpClient are noisy at Information.
    builder.AddFilter("System.Net.Http", LogLevel.Warning);
    builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
});

var logger = loggerFactory.CreateLogger("tenant-pulse");

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(loggerFactory);
services.AddLogging();
services.AddTenantPulse(options);

await using var provider = services.BuildServiceProvider();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    logger.LogInformation("Stopping…");
    cancellation.Cancel();
};

try
{
    return commandLine.Command switch
    {
        "doctor" => await new DoctorCommand(provider, options, configPath, logger)
            .RunAsync(commandLine, cancellation.Token),

        "bootstrap" => await new BootstrapCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        "plan" => await new PlanCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        "run" => await new RunCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        "once" => await new OnceCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        "verify-copilot" => await new VerifyCopilotCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        "report" => await new ReportCommand(provider, logger)
            .RunAsync(commandLine, cancellation.Token),

        "purge" => await new PurgeCommand(provider, options, logger)
            .RunAsync(commandLine, cancellation.Token),

        var unknown => Unknown(unknown)
    };
}
catch (TenantNotAllowedException ex)
{
    logger.LogError("REFUSED: {Message}", ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    logger.LogInformation("Cancelled.");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Unhandled failure.");
    return 1;
}

int Unknown(string command)
{
    logger.LogError("Unknown command '{Command}'.", command);
    HelpCommand.Print();
    return 64;
}

static void ApplyOverrides(TenantPulseOptions options, CommandLine commandLine)
{
    // --live is deliberately explicit: dry run is the default everywhere else.
    if (commandLine.Has("live"))
    {
        options.Simulation.DryRun = false;
    }

    if (commandLine.Has("dry-run"))
    {
        options.Simulation.DryRun = true;
    }

    if (commandLine.Has("seed"))
    {
        options.Simulation.Seed = commandLine.IntValue("seed", options.Simulation.Seed);
    }

    if (commandLine.Value("tenant") is string tenant && !string.IsNullOrWhiteSpace(tenant))
    {
        options.Tenant.TenantId = tenant;
    }
}

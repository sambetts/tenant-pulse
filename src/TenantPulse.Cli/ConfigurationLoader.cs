using Microsoft.Extensions.Configuration;
using TenantPulse.Core.Configuration;

namespace TenantPulse.Cli;

/// <summary>
/// Loads configuration from (in increasing precedence): the JSON config file, environment variables
/// prefixed <c>TENANTPULSE_</c>, and command-line switches.
/// </summary>
internal static class ConfigurationLoader
{
    public const string DefaultConfigPath = "config/tenant-pulse.json";

    public static (TenantPulseOptions Options, string ConfigPath) Load(string? configPath, string[] args)
    {
        var path = configPath ?? DefaultConfigPath;

        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("TENANTPULSE_")
            .AddCommandLine(args);

        var configuration = builder.Build();

        var options = new TenantPulseOptions();
        configuration.GetSection(TenantPulseOptions.SectionName).Bind(options);

        // Convenience: allow the Azure OpenAI key and the shared demo password to live only in the
        // environment, so a working config file never has to contain a secret.
        options.Content.ApiKey ??= Environment.GetEnvironmentVariable("TENANTPULSE_AOAI_KEY");
        options.Auth.SharedPassword ??= Environment.GetEnvironmentVariable("TENANTPULSE_SHARED_PASSWORD");

        return (options, path);
    }

    /// <summary>True when the config file exists on disk.</summary>
    public static bool Exists(string path) => File.Exists(path);
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Personas;
using TenantPulse.Engine.Storylines;
using TenantPulse.Core.Storylines;
namespace TenantPulse.Cli.Commands;

/// <summary>
/// Shared plumbing for commands that need the persona list and storyline catalogue.
/// </summary>
internal abstract class CommandBase(IServiceProvider services, TenantPulseOptions options, ILogger logger)
{
    protected IServiceProvider Services { get; } = services;

    protected TenantPulseOptions Options { get; } = options;

    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// Resolves which enrolled user's token is used for directory reads. Any enrolled user can read
    /// the user list, so we prefer an explicit --as, then the first user with a usable cached token.
    /// </summary>
    protected async Task<string?> ResolveDirectoryReaderAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        if (commandLine.Value("as") is string explicitUser && !string.IsNullOrWhiteSpace(explicitUser))
        {
            return explicitUser;
        }

        var cacheDirectory = Options.Auth.CacheDirectory;
        if (!Directory.Exists(cacheDirectory))
        {
            return null;
        }

        var tokenProvider = Services.GetRequiredService<IUserTokenProvider>();

        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.msalcache"))
        {
            var upn = Path.GetFileNameWithoutExtension(file);

            if (await tokenProvider.IsEnrolledAsync(upn, cancellationToken).ConfigureAwait(false))
            {
                return upn;
            }
        }

        return null;
    }

    protected async Task<IReadOnlyList<Persona>> LoadPersonasAsync(
        CommandLine commandLine,
        CancellationToken cancellationToken)
    {
        // --offline uses a fabricated workforce so the tool can be evaluated with no tenant at all.
        if (commandLine.Has("offline"))
        {
            var synthetic = SyntheticPersonaDirectory.Create(Options);
            Logger.LogInformation("Offline mode: using {Count} synthetic personas (no tenant contacted).",
                synthetic.Count);
            return synthetic;
        }

        var reader = await ResolveDirectoryReaderAsync(commandLine, cancellationToken).ConfigureAwait(false);

        if (reader is null)
        {
            throw new InvalidOperationException(
                "No enrolled user is available to read the directory. Run 'tenant-pulse bootstrap' first, " +
                "pass --as <upn>, or try 'tenant-pulse plan --offline' to preview with synthetic users.");
        }

        var directory = Services.GetRequiredService<GraphPersonaDirectory>();
        return await directory.LoadAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<IReadOnlyList<Storyline>> LoadStorylinesAsync(CancellationToken cancellationToken)
    {
        var path = FindStorylineCatalogue();
        var catalogue = await StorylineCatalogueLoader.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("Loaded {Count} storylines from {Path}.", catalogue.Count, path);
        return catalogue;
    }

    private static string FindStorylineCatalogue()
    {
        string[] candidates =
        [
            "config/storylines.json",
            "../config/storylines.json",
            "../../config/storylines.json",
            Path.Combine(AppContext.BaseDirectory, "config/storylines.json")
        ];

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}

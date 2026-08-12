using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace TenantPulse.Engine.Auth;

/// <summary>
/// Persists each user's MSAL token cache to disk, encrypted at rest by the platform
/// (DPAPI on Windows, keychain on macOS, keyring on Linux).
/// <para>
/// These files hold refresh tokens for every simulated user, so they are treated as secrets:
/// the cache directory is gitignored and should never be copied off the machine.
/// </para>
/// <para>
/// A headless Linux container has no keyring, so platform protection is unavailable there and the
/// cache falls back to an unencrypted file — see <see cref="AttachAsync"/>.
/// </para>
/// </summary>
internal static class TokenCacheStore
{
    private const string KeyringSchemaName = "com.github.sambetts.tenantpulse";

    private static bool _warnedAboutUnprotectedCache;

    /// <summary>
    /// Attaches a persistent cache to <paramref name="cache"/>.
    /// <para>
    /// Platform protection is tried first. If the platform has no usable secret store — the normal
    /// case in a container, where there is no D-Bus session or keyring — persistence would
    /// otherwise throw on every token operation, so the cache falls back to an unencrypted file.
    /// That file holds refresh tokens, so the directory must be treated as a secret: keep it on a
    /// private volume, never in an image layer or a repository.
    /// </para>
    /// </summary>
    public static async Task AttachAsync(ITokenCache cache, string cacheFilePath, ILogger? logger = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cacheFilePath))
                        ?? throw new InvalidOperationException($"Cannot resolve directory for '{cacheFilePath}'.");

        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(cacheFilePath);

        var helper = await TryCreateProtectedAsync(fileName, directory).ConfigureAwait(false);

        if (helper is null)
        {
            if (!_warnedAboutUnprotectedCache)
            {
                _warnedAboutUnprotectedCache = true;
                logger?.LogWarning(
                    "No usable platform secret store (DPAPI/keychain/keyring), which is expected in a " +
                    "container. Falling back to an UNENCRYPTED token cache in {Directory}. It holds " +
                    "refresh tokens for every simulated user — keep that path on a private volume.",
                    directory);
            }

            var unprotected = new StorageCreationPropertiesBuilder(fileName, directory)
                .WithUnprotectedFile()
                .Build();

            helper = await MsalCacheHelper.CreateAsync(unprotected).ConfigureAwait(false);
        }

        helper.RegisterCache(cache);
    }

    private static async Task<MsalCacheHelper?> TryCreateProtectedAsync(string fileName, string directory)
    {
        try
        {
            var properties = new StorageCreationPropertiesBuilder(fileName, directory)
                .WithMacKeyChain($"{KeyringSchemaName}.tokens", fileName)
                .WithLinuxKeyring(
                    schemaName: KeyringSchemaName,
                    collection: "default",
                    secretLabel: $"tenant-pulse token cache ({fileName})",
                    attribute1: new KeyValuePair<string, string>("Product", "tenant-pulse"),
                    attribute2: new KeyValuePair<string, string>("Cache", fileName))
                .Build();

            var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);

            // CreateAsync succeeds even where the store is unusable; this is the documented probe.
            helper.VerifyPersistence();

            return helper;
        }
        catch (MsalCachePersistenceException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            // libsecret is simply absent from most container base images.
            return null;
        }
    }
}

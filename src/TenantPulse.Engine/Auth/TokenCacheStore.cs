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
/// </summary>
internal static class TokenCacheStore
{
    private const string KeyringSchemaName = "com.github.sambetts.tenantpulse";

    public static async Task AttachAsync(ITokenCache cache, string cacheFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cacheFilePath))
                        ?? throw new InvalidOperationException($"Cannot resolve directory for '{cacheFilePath}'.");

        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(cacheFilePath);

        var builder = new StorageCreationPropertiesBuilder(fileName, directory)
            .WithMacKeyChain($"{KeyringSchemaName}.tokens", fileName)
            .WithLinuxKeyring(
                schemaName: KeyringSchemaName,
                collection: "default",
                secretLabel: $"tenant-pulse token cache ({fileName})",
                attribute1: new KeyValuePair<string, string>("Product", "tenant-pulse"),
                attribute2: new KeyValuePair<string, string>("Cache", fileName));

        var helper = await MsalCacheHelper.CreateAsync(builder.Build()).ConfigureAwait(false);
        helper.RegisterCache(cache);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;

namespace TenantPulse.Engine.Auth;

/// <summary>
/// Issues delegated Graph tokens for each simulated user.
/// <para>
/// Delegated tokens are mandatory, not a preference: app-only Graph cannot post Teams messages or
/// call Copilot at all, and app-only mail/file writes are not attributed to the user in the
/// Microsoft 365 usage reports — so an app-only simulator would leave the tenant looking as unused
/// as it started.
/// </para>
/// <para>
/// Two enrolment routes, both ending in the same place (a cached refresh token per user that is
/// then redeemed silently forever):
/// <list type="bullet">
///   <item><b>Device code</b> — the supported route. One interactive sign-in per user, once.</item>
///   <item><b>Username/password (ROPC)</b> — deprecated by Microsoft (MSAL marks it obsolete and
///   RFC 9700 forbids it) and blocked by MFA/security defaults, but demo tenants usually permit it
///   and it enrols 25 users unattended. Opt-in only.</item>
/// </list>
/// </para>
/// </summary>
public sealed class UserTokenBroker : IUserTokenProvider
{
    private readonly TenantPulseOptions _options;
    private readonly ILogger<UserTokenBroker> _logger;
    private readonly Dictionary<string, IPublicClientApplication> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserTokenBroker(TenantPulseOptions options, ILogger<UserTokenBroker> logger)
    {
        _options = options;
        _logger = logger;

        Directory.CreateDirectory(_options.Auth.CacheDirectory);
    }

    private string[] Scopes => [.. _options.Auth.Scopes];

    public async Task<string> GetAccessTokenAsync(string userPrincipalName, CancellationToken cancellationToken)
    {
        var app = await GetAppAsync(userPrincipalName).ConfigureAwait(false);

        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(Scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                return silent.AccessToken;
            }
            catch (MsalUiRequiredException ex)
            {
                _logger.LogWarning(
                    "Silent token acquisition failed for {Upn}: {Reason}. Re-enrolment required.",
                    userPrincipalName, ex.Message);
            }
        }

        // No usable cache. ROPC can re-enrol unattended; device code cannot.
        if (_options.Auth.Mode == AuthMode.UsernamePassword)
        {
            return await AcquireByPasswordAsync(app, userPrincipalName, cancellationToken).ConfigureAwait(false);
        }

        throw new UserNotEnrolledException(userPrincipalName, "no cached account and auth mode is DeviceCode");
    }

    public async Task<bool> IsEnrolledAsync(string userPrincipalName, CancellationToken cancellationToken)
    {
        try
        {
            _ = await GetAccessTokenAsync(userPrincipalName, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (UserNotEnrolledException)
        {
            return false;
        }
        catch (MsalException ex)
        {
            _logger.LogDebug("Enrolment check failed for {Upn}: {Message}", userPrincipalName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Enrols a user interactively via device code. <paramref name="prompt"/> receives the
    /// "go to https://microsoft.com/devicelogin and enter CODE" instruction to show the operator.
    /// </summary>
    public async Task<string> EnrolByDeviceCodeAsync(
        string userPrincipalName,
        Func<DeviceCodeResult, Task> prompt,
        CancellationToken cancellationToken)
    {
        var app = await GetAppAsync(userPrincipalName).ConfigureAwait(false);

        var result = await app.AcquireTokenWithDeviceCode(Scopes, deviceCode =>
            {
                return prompt(deviceCode);
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        WarnOnIdentityMismatch(userPrincipalName, result.Account?.Username);
        return result.AccessToken;
    }

    /// <summary>Enrols a user unattended with a password (ROPC). Opt-in; see class remarks.</summary>
    public async Task<string> EnrolByPasswordAsync(string userPrincipalName, CancellationToken cancellationToken)
    {
        var app = await GetAppAsync(userPrincipalName).ConfigureAwait(false);
        return await AcquireByPasswordAsync(app, userPrincipalName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AcquireByPasswordAsync(
        IPublicClientApplication app,
        string userPrincipalName,
        CancellationToken cancellationToken)
    {
        var password = ResolvePassword(userPrincipalName)
            ?? throw new UserNotEnrolledException(
                userPrincipalName,
                "no password configured (set Auth.SharedPassword, Auth.Passwords[upn], or TENANTPULSE_SHARED_PASSWORD)");

        try
        {
#pragma warning disable CS0618 // ROPC is obsolete by design; this path is opt-in and documented.
            var result = await app.AcquireTokenByUsernamePassword(Scopes, userPrincipalName, password)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore CS0618

            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            throw new UserNotEnrolledException(
                userPrincipalName,
                $"username/password sign-in failed ({ex.ErrorCode}). MFA, Conditional Access or security " +
                $"defaults will block ROPC — switch Auth.Mode to DeviceCode. {ex.Message}");
        }
    }

    private string? ResolvePassword(string upn)
    {
        if (_options.Auth.Passwords.TryGetValue(upn, out var specific) && !string.IsNullOrWhiteSpace(specific))
        {
            return specific;
        }

        if (!string.IsNullOrWhiteSpace(_options.Auth.SharedPassword))
        {
            return _options.Auth.SharedPassword;
        }

        var fromEnv = Environment.GetEnvironmentVariable("TENANTPULSE_SHARED_PASSWORD");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private void WarnOnIdentityMismatch(string expectedUpn, string? signedInAs)
    {
        if (!string.IsNullOrWhiteSpace(signedInAs) &&
            !string.Equals(signedInAs, expectedUpn, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Enrolled as {Actual} but expected {Expected}. Activity will be attributed to the wrong " +
                "user — clear that user's cache and retry.", signedInAs, expectedUpn);
        }
    }

    /// <summary>
    /// One MSAL app per user, each with its own on-disk cache file. Keeping caches separate means a
    /// corrupt or expired cache only affects one simulated user.
    /// </summary>
    private async Task<IPublicClientApplication> GetAppAsync(string userPrincipalName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_apps.TryGetValue(userPrincipalName, out var existing))
            {
                return existing;
            }

            if (string.IsNullOrWhiteSpace(_options.Tenant.ClientId))
            {
                throw new InvalidOperationException(
                    "Tenant.ClientId is not configured. Register a public-client Entra app with the " +
                    "delegated Graph scopes and set its application (client) id.");
            }

            var app = PublicClientApplicationBuilder
                .Create(_options.Tenant.ClientId)
                .WithAuthority(_options.Tenant.Authority)
                .WithDefaultRedirectUri()
                .Build();

            var cacheFile = CacheFileFor(userPrincipalName);
            await TokenCacheStore.AttachAsync(app.UserTokenCache, cacheFile).ConfigureAwait(false);

            _apps[userPrincipalName] = app;
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CacheFileFor(string upn)
    {
        var safe = string.Concat(upn.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_options.Auth.CacheDirectory, $"{safe}.msalcache");
    }
}

namespace TenantPulse.Core.Personas;

/// <summary>
/// Supplies a delegated (per-user) Graph access token for a persona.
/// <para>
/// Delegated tokens are non-negotiable: app-only Graph calls cannot post Teams messages or call
/// Copilot at all, and app-only mail/file writes are not attributed to the user in the Microsoft
/// 365 usage reports — so they would not make the tenant look used.
/// </para>
/// </summary>
public interface IUserTokenProvider
{
    /// <summary>
    /// Returns a bearer token for <paramref name="userPrincipalName"/>, refreshing silently from
    /// the cached refresh token. Throws <see cref="UserNotEnrolledException"/> when the user has
    /// never been enrolled or their refresh token has expired.
    /// </summary>
    Task<string> GetAccessTokenAsync(string userPrincipalName, CancellationToken cancellationToken);

    /// <summary>True when a usable cached token/refresh token exists for the user.</summary>
    Task<bool> IsEnrolledAsync(string userPrincipalName, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a persona has no usable cached credentials and cannot be signed in silently.
/// The remedy is to re-run <c>tenant-pulse bootstrap</c> for that user.
/// </summary>
public sealed class UserNotEnrolledException(string upn, string? detail = null)
    : Exception($"'{upn}' is not enrolled (or the cached token expired). Run: tenant-pulse bootstrap --user {upn}" +
                (detail is null ? "" : $" — {detail}"))
{
    public string UserPrincipalName { get; } = upn;
}

using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Time;

namespace TenantPulse.Core.Safety;

/// <summary>
/// Raised when tenant-pulse is asked to act against a tenant it has not been explicitly allowed to
/// touch. This is deliberately fatal and deliberately not catchable-and-ignorable in normal flow.
/// </summary>
public sealed class TenantNotAllowedException(string message) : Exception(message);

public sealed record RateDecision(bool Allowed, string? Reason)
{
    public static readonly RateDecision Ok = new(true, null);

    public static RateDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The last line of defence. Enforces:
/// <list type="bullet">
///   <item>the tenant allow-list (never act on a tenant that isn't explicitly listed),</item>
///   <item>the kill switch file,</item>
///   <item>per-user daily, per-tenant hourly and per-user spacing rate limits.</item>
/// </list>
/// Every executor call must pass through <see cref="TryBeginActivity"/>.
/// </summary>
public sealed class SafetyGovernor(TenantPulseOptions options, IClock clock)
{
    private readonly Dictionary<string, List<DateTimeOffset>> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _tenantWide = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Verifies the configured tenant is allow-listed. Call once at startup, before any token is
    /// requested. Throws <see cref="TenantNotAllowedException"/> when it is not.
    /// </summary>
    public void AssertTenantAllowed()
    {
        var tenantId = options.Tenant.TenantId?.Trim();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new TenantNotAllowedException(
                "Tenant.TenantId is not set. tenant-pulse refuses to run without an explicit target tenant.");
        }

        if (options.Tenant.AllowedTenantIds.Count == 0)
        {
            throw new TenantNotAllowedException(
                "Tenant.AllowedTenantIds is empty. Add the demo tenant's id to the allow-list to confirm " +
                "you intend to generate activity in it. This guard exists so tenant-pulse can never be " +
                "pointed at a production tenant by accident.");
        }

        var allowed = options.Tenant.AllowedTenantIds
            .Any(id => string.Equals(id?.Trim(), tenantId, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new TenantNotAllowedException(
                $"Tenant '{tenantId}' is not in Tenant.AllowedTenantIds. Refusing to generate activity.");
        }
    }

    /// <summary>
    /// True when a persona is eligible to act at all: not excluded, and in an allowed domain.
    /// </summary>
    public bool IsPersonaAllowed(Persona persona)
    {
        if (persona.Excluded)
        {
            return false;
        }

        if (options.Tenant.AllowedDomains.Count == 0)
        {
            return true;
        }

        return options.Tenant.AllowedDomains.Any(d =>
            persona.UserPrincipalName.EndsWith($"@{d.TrimStart('@')}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the kill switch file exists.</summary>
    public bool IsStopRequested() =>
        !string.IsNullOrWhiteSpace(options.Simulation.KillSwitchFile) &&
        File.Exists(options.Simulation.KillSwitchFile);

    /// <summary>
    /// Reserves a slot for one activity. Returns a denial (with reason) when a limit would be
    /// breached; the caller should skip the intent rather than wait.
    /// </summary>
    public RateDecision TryBeginActivity(ActivityIntent intent)
    {
        if (IsStopRequested())
        {
            return RateDecision.Deny($"Kill switch present ({options.Simulation.KillSwitchFile}).");
        }

        if (!IsPersonaAllowed(intent.Actor))
        {
            return RateDecision.Deny($"Persona {intent.Actor.UserPrincipalName} is not allowed to act.");
        }

        if (intent.Kind.RequiresCopilotLicence() && !intent.Actor.HasCopilotLicence)
        {
            return RateDecision.Deny($"{intent.Actor.UserPrincipalName} has no Microsoft 365 Copilot licence.");
        }

        var now = clock.UtcNow;
        var limits = options.Limits;

        lock (_gate)
        {
            Prune(now);

            if (_tenantWide.Count >= limits.MaxActivitiesPerTenantPerHour)
            {
                return RateDecision.Deny(
                    $"Tenant hourly cap reached ({limits.MaxActivitiesPerTenantPerHour}/h).");
            }

            if (!_perUser.TryGetValue(intent.Actor.Id, out var history))
            {
                history = [];
                _perUser[intent.Actor.Id] = history;
            }

            var today = history.Count(t => t >= now.AddDays(-1));
            if (today >= limits.MaxActivitiesPerUserPerDay)
            {
                return RateDecision.Deny(
                    $"{intent.Actor.UserPrincipalName} hit the daily cap ({limits.MaxActivitiesPerUserPerDay}).");
            }

            var last = history.Count > 0 ? history[^1] : (DateTimeOffset?)null;
            if (last is not null &&
                (now - last.Value).TotalSeconds < limits.MinSecondsBetweenUserActivities)
            {
                return RateDecision.Deny(
                    $"{intent.Actor.UserPrincipalName} acted {(now - last.Value).TotalSeconds:F0}s ago; " +
                    $"minimum spacing is {limits.MinSecondsBetweenUserActivities}s.");
            }

            history.Add(now);
            _tenantWide.Add(now);
            return RateDecision.Ok;
        }
    }

    /// <summary>Number of activities recorded in the last hour, tenant-wide.</summary>
    public int ActivitiesInLastHour
    {
        get
        {
            lock (_gate)
            {
                Prune(clock.UtcNow);
                return _tenantWide.Count;
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var hourAgo = now.AddHours(-1);
        _tenantWide.RemoveAll(t => t < hourAgo);

        var dayAgo = now.AddDays(-1);
        foreach (var history in _perUser.Values)
        {
            history.RemoveAll(t => t < dayAgo);
        }
    }
}

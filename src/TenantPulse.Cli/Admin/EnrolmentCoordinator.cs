using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TenantPulse.Engine.Auth;

namespace TenantPulse.Cli.Admin;

/// <summary>
/// Runs device-code enrolments on behalf of the admin web.
/// <para>
/// A device-code sign-in cannot be served by a single request: MSAL hands back a code, then blocks
/// until the operator has typed it in somewhere else, which takes as long as it takes. So the flow
/// is started detached, its code published as soon as MSAL produces one, and the browser polls for
/// the outcome. Nothing is written to disk here — MSAL's own per-user cache is the record.
/// </para>
/// <para>
/// Device code rather than a password box: it is the route Microsoft actually supports, it works
/// when MFA or Conditional Access is switched on (which is exactly when ROPC fails and the operator
/// comes looking for this), and it means the admin web never handles a credential.
/// </para>
/// </summary>
internal sealed class EnrolmentCoordinator(UserTokenBroker broker, ILogger logger)
{
    /// <summary>Device codes are short-lived; MSAL gives up long before this, but nothing may hang forever.</summary>
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How long a finished flow stays readable so the browser can collect the outcome.</summary>
    private static readonly TimeSpan KeepCompletedFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Enrolment> _flows = new(StringComparer.Ordinal);

    public Enrolment Start(string userPrincipalName)
    {
        Prune();

        // One live flow per user: starting a second would leave the operator holding two codes and
        // only one of them working.
        var running = _flows.Values.FirstOrDefault(f =>
            f.Status is EnrolmentStatus.Starting or EnrolmentStatus.WaitingForCode &&
            string.Equals(f.Upn, userPrincipalName, StringComparison.OrdinalIgnoreCase));

        if (running is not null)
        {
            return running;
        }

        var enrolment = new Enrolment(Guid.NewGuid().ToString("N"), userPrincipalName);
        _flows[enrolment.Id] = enrolment;

        _ = RunAsync(enrolment);
        return enrolment;
    }

    public Enrolment? Get(string id) => _flows.TryGetValue(id, out var flow) ? flow : null;

    private async Task RunAsync(Enrolment enrolment)
    {
        using var timeout = new CancellationTokenSource(FlowTimeout);

        try
        {
            await broker.EnrolByDeviceCodeAsync(
                enrolment.Upn,
                deviceCode =>
                {
                    enrolment.Publish(deviceCode.UserCode, deviceCode.VerificationUrl, deviceCode.ExpiresOn);
                    logger.LogInformation(
                        "Device-code enrolment started for {Upn}: enter {Code} at {Url}.",
                        enrolment.Upn, deviceCode.UserCode, deviceCode.VerificationUrl);
                    return Task.CompletedTask;
                },
                timeout.Token).ConfigureAwait(false);

            enrolment.Succeed();
            logger.LogInformation("Enrolled {Upn}.", enrolment.Upn);
        }
        catch (OperationCanceledException)
        {
            enrolment.Fail("Timed out waiting for the sign-in to be completed.");
        }
        catch (Exception ex)
        {
            enrolment.Fail(ex.GetBaseException().Message);
            logger.LogWarning(ex, "Device-code enrolment failed for {Upn}.", enrolment.Upn);
        }
    }

    private void Prune()
    {
        foreach (var (id, flow) in _flows)
        {
            if (flow.Finished is { } finished && DateTimeOffset.UtcNow - finished > KeepCompletedFor)
            {
                _flows.TryRemove(id, out _);
            }
        }
    }
}

internal enum EnrolmentStatus
{
    Starting,
    WaitingForCode,
    Enrolled,
    Failed
}

internal sealed class Enrolment(string id, string upn)
{
    private readonly Lock _sync = new();

    public string Id { get; } = id;

    public string Upn { get; } = upn;

    public EnrolmentStatus Status { get; private set; } = EnrolmentStatus.Starting;

    public string? UserCode { get; private set; }

    public string? VerificationUrl { get; private set; }

    public DateTimeOffset? ExpiresUtc { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset? Finished { get; private set; }

    public void Publish(string userCode, string verificationUrl, DateTimeOffset expiresOn)
    {
        lock (_sync)
        {
            UserCode = userCode;
            VerificationUrl = verificationUrl;
            ExpiresUtc = expiresOn;
            Status = EnrolmentStatus.WaitingForCode;
        }
    }

    public void Succeed()
    {
        lock (_sync)
        {
            Status = EnrolmentStatus.Enrolled;
            Finished = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (_sync)
        {
            Status = EnrolmentStatus.Failed;
            Error = error;
            Finished = DateTimeOffset.UtcNow;
        }
    }

    public object ToPayload()
    {
        lock (_sync)
        {
            return new
            {
                id = Id,
                upn = Upn,
                status = Status.ToString(),
                userCode = UserCode,
                verificationUrl = VerificationUrl,
                expiresUtc = ExpiresUtc,
                error = Error
            };
        }
    }
}

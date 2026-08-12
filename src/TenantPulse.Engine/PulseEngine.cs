using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Safety;
using TenantPulse.Core.Scheduling;
using TenantPulse.Core.Storylines;
using TenantPulse.Core.Time;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine;

/// <summary>
/// The heart of the simulator: plans each day, then plays the plan out in real time.
/// <para>
/// Activity is executed <b>when its scheduled moment arrives</b> rather than in a burst, because
/// the whole point is a tenant that looks continuously worked-in. Everything is funnelled through
/// the <see cref="SafetyGovernor"/> and recorded in the journal, and the journal also makes the
/// loop idempotent — restarting mid-day replays the same plan without duplicating what already ran.
/// </para>
/// </summary>
public sealed class PulseEngine(
    TenantPulseOptions options,
    SafetyGovernor governor,
    IActivityJournal journal,
    IEnumerable<IActivityExecutor> executors,
    IClock clock,
    ILogger<PulseEngine> logger)
{
    private readonly Dictionary<ActivityKind, IActivityExecutor> _executors =
        executors.ToDictionary(e => e.Kind);

    /// <summary>Plans a day without executing anything. Used by the <c>plan</c> command.</summary>
    public IReadOnlyList<ActivityIntent> PlanDay(
        DateOnly date,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<Storyline> catalogue)
    {
        var storylines = new StorylineScheduler(options, catalogue).ActiveOn(date, personas);
        var plan = new DayPlanner(options).PlanDay(date, personas, storylines);

        logger.LogInformation(
            "Planned {Count} activities for {Date} across {Personas} personas and {Storylines} storylines.",
            plan.Count, date, personas.Count(p => !p.Excluded), storylines.Count);

        return plan;
    }

    /// <summary>
    /// Runs continuously: plays out today's plan as the clock reaches each intent, then rolls over
    /// into the next day. Returns when cancelled or when the kill switch appears.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<Persona> personas,
        IReadOnlyList<Storyline> catalogue,
        CancellationToken cancellationToken)
    {
        governor.AssertTenantAllowed();

        await journal.InitialiseAsync(cancellationToken).ConfigureAwait(false);

        var context = new ExecContext { DryRun = options.Simulation.DryRun, Seed = options.Simulation.Seed };

        logger.LogInformation(
            "tenant-pulse running against {TenantId} ({Mode}). Kill switch: {KillSwitch}",
            options.Tenant.TenantId,
            context.DryRun ? "DRY RUN — nothing will be written" : "LIVE",
            options.Simulation.KillSwitchFile);

        DateOnly? plannedDate = null;
        var queue = new Queue<ActivityIntent>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (governor.IsStopRequested())
                {
                    logger.LogWarning("Kill switch present — stopping. Delete {File} to resume.",
                        options.Simulation.KillSwitchFile);
                    return;
                }

                var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

                if (plannedDate != today)
                {
                    var plan = PlanDay(today, personas, catalogue);
                    queue = new Queue<ActivityIntent>(plan.Where(i => i.ScheduledUtc >= clock.UtcNow.AddMinutes(-5)));
                    plannedDate = today;

                    var alreadyPast = plan.Count - queue.Count;
                    if (alreadyPast > 0)
                    {
                        logger.LogInformation(
                            "Skipping {Count} activities already in the past for today.", alreadyPast);
                    }
                }

                if (queue.Count == 0)
                {
                    // Nothing left today — idle until just after midnight UTC, then re-plan.
                    var nextMidnight = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
                    var wait = new DateTimeOffset(nextMidnight, TimeSpan.Zero) - clock.UtcNow;
                    await DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var next = queue.Peek();
                var untilDue = next.ScheduledUtc - clock.UtcNow;

                if (untilDue > TimeSpan.Zero)
                {
                    // Wake at most every minute so the kill switch is honoured promptly.
                    await DelayAsync(untilDue > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : untilDue,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                queue.Dequeue();
                await ExecuteAsync(next, context, cancellationToken).ConfigureAwait(false);

                // Debounced internally; only does anything when a durable copy is configured.
                // A file share hiccup must never end a run that is meant to last for days.
                await SnapshotQuietlyAsync(force: false).ConfigureAwait(false);
            }
        }
        finally
        {
            // The tenant must stay cleanable even if this process is about to disappear.
            await SnapshotQuietlyAsync(force: true).ConfigureAwait(false);
        }
    }

    private async Task SnapshotQuietlyAsync(bool force)
    {
        try
        {
            await journal.SnapshotAsync(CancellationToken.None, force).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the journal snapshot; purge history may be stale.");
        }
    }

    /// <summary>
    /// Executes a batch of intents immediately, ignoring their scheduled times. Used by the
    /// <c>once</c> command to prove the pipeline end to end without waiting for the clock.
    /// </summary>
    public async Task<IReadOnlyList<(ActivityIntent Intent, ActivityResult Result)>> RunBatchAsync(
        IReadOnlyList<ActivityIntent> intents,
        CancellationToken cancellationToken)
    {
        governor.AssertTenantAllowed();
        await journal.InitialiseAsync(cancellationToken).ConfigureAwait(false);

        var context = new ExecContext { DryRun = options.Simulation.DryRun, Seed = options.Simulation.Seed };
        var results = new List<(ActivityIntent, ActivityResult)>();

        foreach (var intent in intents)
        {
            if (cancellationToken.IsCancellationRequested || governor.IsStopRequested())
            {
                break;
            }

            var result = await ExecuteAsync(intent, context, cancellationToken).ConfigureAwait(false);
            results.Add((intent, result));
        }

        return results;
    }

    private async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        // Replaying a plan (after a restart) must never repeat work already done.
        if (await journal.HasExecutedAsync(intent.Id, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Already executed {IntentId}; skipping.", intent.Id);
            return ActivityResult.Skipped("Already executed.");
        }

        var decision = governor.TryBeginActivity(intent);
        if (!decision.Allowed)
        {
            var skipped = ActivityResult.Skipped(decision.Reason ?? "Rate limited.");
            logger.LogDebug("Skipped {Kind} for {Upn}: {Reason}",
                intent.Kind, intent.Actor.UserPrincipalName, decision.Reason);
            await journal.RecordAsync(intent, skipped, cancellationToken).ConfigureAwait(false);
            return skipped;
        }

        if (!_executors.TryGetValue(intent.Kind, out var executor))
        {
            var missing = ActivityResult.Skipped($"No executor registered for {intent.Kind}.");
            await journal.RecordAsync(intent, missing, cancellationToken).ConfigureAwait(false);
            return missing;
        }

        ActivityResult result;
        try
        {
            result = await executor.ExecuteAsync(intent, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UserNotEnrolledException ex)
        {
            result = ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            // One bad activity must never take the simulator down — it has to run for days.
            logger.LogError(ex, "{Kind} failed for {Upn}.", intent.Kind, intent.Actor.UserPrincipalName);
            result = ActivityResult.Failed(ex.Message);
        }

        LogResult(intent, result);
        await journal.RecordAsync(intent, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private void LogResult(ActivityIntent intent, ActivityResult result)
    {
        switch (result.Outcome)
        {
            case ActivityOutcome.Executed:
                logger.LogInformation("✓ {Kind} · {Upn} · {Topic}{Detail}",
                    intent.Kind, intent.Actor.UserPrincipalName, intent.Topic,
                    result.Detail is null ? "" : $" — {result.Detail}");
                break;

            case ActivityOutcome.Simulated:
                logger.LogInformation("· {Kind} · {Upn} · {Detail}",
                    intent.Kind, intent.Actor.UserPrincipalName, result.Detail ?? intent.Topic);
                break;

            case ActivityOutcome.Skipped:
                logger.LogDebug("– {Kind} · {Upn} · skipped: {Reason}",
                    intent.Kind, intent.Actor.UserPrincipalName, result.Detail);
                break;

            case ActivityOutcome.Failed:
                logger.LogWarning("✗ {Kind} · {Upn} · failed: {Error}",
                    intent.Kind, intent.Actor.UserPrincipalName, result.Error);
                break;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Shutting down.
        }
    }
}

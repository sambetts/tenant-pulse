using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Storylines;
using TenantPulse.Engine;
using TenantPulse.Engine.Auth;

namespace TenantPulse.Cli.Admin;

/// <summary>
/// A small HTTP surface for watching and steering a running simulator.
/// <para>
/// It is hosted <b>inside the run process on purpose</b>. The simulator is single-writer by design
/// — two of them would double-post into the tenant and race the journal — so an admin service that
/// could trigger activity had to either live here or talk to something that does. Living here also
/// means a manually triggered batch goes through the real engine, and therefore lands in the
/// journal, the console, Log Analytics and the workbook exactly like scheduled activity. Anything
/// launched through <c>az containerapp exec</c> never reaches Log Analytics at all.
/// </para>
/// <para>
/// There is deliberately no authentication code here. The container app is fronted by Container
/// Apps' built-in Entra authentication, so this process only ever sees requests that have already
/// been through it. Writing bespoke auth for a control plane that can write to a tenant would be a
/// worse answer than using the platform's.
/// </para>
/// </summary>
internal sealed class AdminServer(
    IServiceProvider services,
    TenantPulseOptions options,
    IReadOnlyList<Persona> personas,
    IReadOnlyList<Storyline> storylines,
    ILogger logger)
{
    private static readonly string IndexHtml = LoadIndexHtml();

    private readonly EnrolmentCoordinator _enrolments =
        new(services.GetRequiredService<UserTokenBroker>(), logger);

    public async Task<WebApplication> StartAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        MapEndpoints(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Admin web listening on port {Port}.", port);
        return app;
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

        app.MapGet("/api/health", (CancellationToken ct) => Health(ct));
        app.MapGet("/api/stats", (int? since, CancellationToken ct) => StatsAsync(since ?? 7, ct));
        app.MapGet("/api/activity", (int? since, int? limit, string? persona, string? kind, string? outcome,
            CancellationToken ct) => ActivityAsync(since ?? 7, limit ?? 50, persona, kind, outcome, ct));

        app.MapGet("/api/config", () => Results.Ok(CurrentConfig()));
        app.MapPost("/api/config", (ConfigRequest request, HttpContext http, CancellationToken ct) =>
            UpdateConfigAsync(request, http, ct));

        app.MapGet("/api/personas", (CancellationToken ct) => PersonasAsync(ct));

        app.MapGet("/api/kinds", () => Results.Ok(Enum.GetNames<ActivityKind>().Order(StringComparer.Ordinal)));

        app.MapPost("/api/run", (RunRequest request, CancellationToken ct) => RunNowAsync(request, ct));

        app.MapPost("/api/enrol", (EnrolRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Upn))
            {
                return Results.BadRequest(new { error = "A user is required." });
            }

            if (!personas.Any(p => p.UserPrincipalName.Equals(request.Upn, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = $"No persona matches '{request.Upn}'." });
            }

            return Results.Ok(_enrolments.Start(request.Upn).ToPayload());
        });

        app.MapGet("/api/enrol/{id}", (string id) =>
        {
            var enrolment = _enrolments.Get(id);
            return enrolment is null
                ? Results.NotFound(new { error = "That enrolment is no longer being tracked." })
                : Results.Ok(enrolment.ToPayload());
        });
    }

    /// <summary>
    /// The persona list carries enrolment state, because "nothing happened" is almost always an
    /// unenrolled user rather than a broken simulator.
    /// </summary>
    private async Task<IResult> PersonasAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var broker = services.GetRequiredService<UserTokenBroker>();
        var active = personas
            .Where(p => !p.Excluded)
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<object>(active.Count);

        foreach (var persona in active)
        {
            result.Add(new
            {
                upn = persona.UserPrincipalName,
                name = persona.DisplayName,
                department = persona.Department,
                copilot = persona.HasCopilotLicence,
                enrolled = await broker.HasCachedAccountAsync(persona.UserPrincipalName).ConfigureAwait(false)
            });
        }

        return Results.Ok(result);
    }

    private IResult Health(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var engine = services.GetRequiredService<PulseEngine>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = engine.PlanDay(today, personas.Where(p => !p.Excluded).ToList(), storylines);
        var next = plan.FirstOrDefault(i => i.ScheduledUtc >= DateTimeOffset.UtcNow);

        return Results.Ok(new
        {
            tenantId = options.Tenant.TenantId,
            dryRun = options.Simulation.DryRun,
            personas = personas.Count(p => !p.Excluded),
            storylines = storylines.Count,
            plannedToday = plan.Count,
            nextDueUtc = next?.ScheduledUtc,
            nextKind = next?.Kind.ToString(),
            nextPersona = next?.Actor.DisplayName,
            killSwitch = File.Exists(options.Simulation.KillSwitchFile)
        });
    }

    private async Task<IResult> StatsAsync(int since, CancellationToken cancellationToken)
    {
        var journal = services.GetRequiredService<IActivityJournal>();
        var days = Math.Clamp(since, 1, 90);
        var summary = await journal
            .SummariseAsync(DateTimeOffset.UtcNow.AddDays(-days), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            days,
            summary.Total,
            summary.Executed,
            summary.Simulated,
            summary.Skipped,
            summary.Failed,
            byKind = summary.ByKind
                .OrderByDescending(k => k.Value)
                .Select(k => new { kind = k.Key.ToString(), count = k.Value }),
            byActor = summary.ByActor.Select(a => new
            {
                upn = a.ActorUpn,
                name = personas.FirstOrDefault(p =>
                    p.UserPrincipalName.Equals(a.ActorUpn, StringComparison.OrdinalIgnoreCase))?.DisplayName
                    ?? a.ActorUpn,
                a.Total,
                a.Executed,
                a.Skipped,
                a.Failed
            })
        });
    }

    private async Task<IResult> ActivityAsync(
        int since,
        int limit,
        string? persona,
        string? kind,
        string? outcome,
        CancellationToken cancellationToken)
    {
        ActivityKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<ActivityKind>(kind, true, out var k))
        {
            parsedKind = k;
        }

        ActivityOutcome? parsedOutcome = null;
        if (!string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<ActivityOutcome>(outcome, true, out var o))
        {
            parsedOutcome = o;
        }

        var journal = services.GetRequiredService<IActivityJournal>();

        var entries = await journal.QueryAsync(
            new JournalQuery
            {
                Since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(since, 1, 90)),
                ActorUpn = string.IsNullOrWhiteSpace(persona) ? null : persona,
                Kind = parsedKind,
                Outcome = parsedOutcome,
                Limit = Math.Clamp(limit, 1, 500)
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(entries.Select(e => new
        {
            occurredUtc = e.OccurredUtc,
            kind = e.Kind.ToString(),
            outcome = e.Outcome.ToString(),
            upn = e.ActorUpn,
            name = personas.FirstOrDefault(p =>
                p.UserPrincipalName.Equals(e.ActorUpn, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? e.ActorUpn,
            e.Topic,
            e.Detail,
            e.Error,
            link = e.WebLink,
            storyline = e.StorylineId
        }));
    }

    private object CurrentConfig() => new
    {
        activityIntensity = options.Simulation.ActivityIntensity,
        maxActivitiesPerUserPerDay = options.Limits.MaxActivitiesPerUserPerDay,
        maxActivitiesPerTenantPerHour = options.Limits.MaxActivitiesPerTenantPerHour,

        // The per-persona budget is capped at the daily limit minus two, so a generous intensity
        // with a stock limit silently does nothing. Surfacing it stops that being a mystery.
        effectivePerPersonaCap = Math.Max(1, options.Limits.MaxActivitiesPerUserPerDay - 2)
    };

    private async Task<IResult> UpdateConfigAsync(
        ConfigRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request.ActivityIntensity is { } intensity && (intensity < 0.1 || intensity > 20.0))
        {
            return Results.BadRequest(new { error = "activityIntensity must be between 0.1 and 20." });
        }

        var settings = new RuntimeSettings
        {
            ActivityIntensity = request.ActivityIntensity,
            MaxActivitiesPerUserPerDay = request.MaxActivitiesPerUserPerDay,
            MaxActivitiesPerTenantPerHour = request.MaxActivitiesPerTenantPerHour,
            UpdatedBy = ResolveCaller(http),
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        settings.ApplyTo(options);

        // Persist what is now in force rather than just the delta, so the next restart reproduces
        // this exact state even if the deployment's defaults have moved on.
        var persisted = RuntimeSettings.CaptureFrom(options) with
        {
            UpdatedBy = settings.UpdatedBy,
            UpdatedUtc = settings.UpdatedUtc
        };

        var store = services.GetRequiredService<IRuntimeSettingsStore>();
        await store.SaveAsync(persisted, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Admin changed settings: intensity {Intensity}, {PerUser}/user/day, {PerHour}/tenant/hour (by {By}).",
            options.Simulation.ActivityIntensity,
            options.Limits.MaxActivitiesPerUserPerDay,
            options.Limits.MaxActivitiesPerTenantPerHour,
            settings.UpdatedBy ?? "unknown");

        return Results.Ok(CurrentConfig());
    }

    /// <summary>
    /// Container Apps' built-in authentication forwards the signed-in identity in these headers, so
    /// a settings change can be attributed without this process handling tokens itself.
    /// </summary>
    private static string? ResolveCaller(HttpContext http)
    {
        foreach (var header in (string[])["X-MS-CLIENT-PRINCIPAL-NAME", "X-MS-CLIENT-PRINCIPAL-ID"])
        {
            if (http.Request.Headers.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.ToString();
            }
        }

        return null;
    }

    private async Task<IResult> RunNowAsync(RunRequest request, CancellationToken cancellationToken)
    {
        var eligible = personas.Where(p => !p.Excluded).ToList();

        if (!string.IsNullOrWhiteSpace(request.Upn))
        {
            eligible = [.. eligible.Where(p =>
                p.UserPrincipalName.Equals(request.Upn, StringComparison.OrdinalIgnoreCase))];

            if (eligible.Count == 0)
            {
                return Results.BadRequest(new { error = $"No persona matches '{request.Upn}'." });
            }
        }

        ActivityKind? kind = null;
        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            if (!Enum.TryParse<ActivityKind>(request.Kind, true, out var parsed))
            {
                return Results.BadRequest(new { error = $"Unknown activity kind '{request.Kind}'." });
            }

            kind = parsed;
        }

        var count = Math.Clamp(request.Count ?? 3, 1, 25);
        var engine = services.GetRequiredService<PulseEngine>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Look across a few days so a filter that matches nothing today still finds work, which is
        // the difference between "generate a Copilot prompt for Megan" working and mystifying.
        var candidates = new List<ActivityIntent>();
        for (var offset = 0; offset < 5 && candidates.Count < count * 6; offset++)
        {
            candidates.AddRange(engine.PlanDay(today.AddDays(offset), eligible, storylines));
        }

        if (kind is not null)
        {
            candidates = [.. candidates.Where(i => i.Kind == kind)];
        }

        if (!string.IsNullOrWhiteSpace(request.Upn))
        {
            candidates = [.. candidates.Where(i =>
                i.Actor.UserPrincipalName.Equals(request.Upn, StringComparison.OrdinalIgnoreCase))];
        }

        var batch = candidates.Take(count).ToList();

        if (batch.Count == 0)
        {
            return Results.BadRequest(new
            {
                error = "Nothing matched. That persona may not do that activity kind — Copilot " +
                        "prompts are only planned for licensed users, for example."
            });
        }

        logger.LogInformation("Admin triggered {Count} activities immediately.", batch.Count);

        var results = await engine.RunBatchAsync(batch, cancellationToken).ConfigureAwait(false);

        return Results.Ok(results.Select(r => new
        {
            kind = r.Intent.Kind.ToString(),
            upn = r.Intent.Actor.UserPrincipalName,
            name = r.Intent.Actor.DisplayName,
            r.Intent.Topic,
            outcome = r.Result.Outcome.ToString(),
            detail = r.Result.Detail,
            error = r.Result.Error,
            link = r.Result.WebLink
        }));
    }

    private static string LoadIndexHtml()
    {
        // Embedded rather than a wwwroot folder so the published container is a single artefact and
        // the page cannot go missing from a Dockerfile COPY.
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.Ordinal));

        if (name is null)
        {
            return "<!doctype html><title>tenant-pulse</title><p>Admin page resource is missing.</p>";
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal sealed record ConfigRequest(
        double? ActivityIntensity,
        int? MaxActivitiesPerUserPerDay,
        int? MaxActivitiesPerTenantPerHour);

    internal sealed record RunRequest(string? Upn, string? Kind, int? Count);

    internal sealed record EnrolRequest(string? Upn);
}

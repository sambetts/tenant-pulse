using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Graph;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Activities;

public sealed class CreateEventExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    ILogger<CreateEventExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.CreateEvent;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        if (intent.Targets.Count is 0)
        {
            return ActivityResult.Skipped("No meeting attendees were provided.");
        }

        try
        {
            var slot = FindMeetingSlot(intent, context);
            if (slot is null)
            {
                return ActivityResult.Skipped("No near-future working-hours slot was available for all attendees.");
            }

            var content = await contentGenerator.GenerateAsync(
                new ContentRequest { Shape = ContentShape.MeetingInvite, Intent = intent },
                cancellationToken).ConfigureAwait(false);
            var subject = ExecutorHelpers.SubjectOrFallback(content.Subject, intent.Topic);

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would create Teams meeting '{subject}' at {slot.Value.StartLocal:yyyy-MM-dd HH:mm}.");
            }

            var upn = intent.Actor.UserPrincipalName;
            var created = await graph.PostAsync(
                upn,
                $"users/{upn}/events",
                new
                {
                    subject,
                    body = new { contentType = "HTML", content = ExecutorHelpers.ToHtmlParagraphs(content.Body) },
                    start = new { dateTime = FormatGraphLocal(slot.Value.StartLocal), timeZone = intent.Actor.TimeZoneId },
                    end = new { dateTime = FormatGraphLocal(slot.Value.EndLocal), timeZone = intent.Actor.TimeZoneId },
                    attendees = intent.Targets.Select(ToAttendee).ToArray(),
                    isOnlineMeeting = true,
                    onlineMeetingProvider = "teamsForBusiness"
                },
                cancellationToken).ConfigureAwait(false);

            var eventId = created?.GetStringOrNull("id");
            return ActivityResult.Executed(
                eventId,
                eventId is null ? null : $"users/{upn}/events/{eventId}",
                $"Created Teams meeting '{subject}' with {intent.Targets.Count} attendee(s).");
        }
        catch (UserNotEnrolledException ex)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create event for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    private static MeetingSlot? FindMeetingSlot(ActivityIntent intent, ExecContext context)
    {
        var rng = ExecutorHelpers.RandomFor(context.Seed, intent.Id);
        var duration = TimeSpan.FromMinutes(rng.Next(2) is 0 ? 30 : 60);
        var actorZone = intent.Actor.ResolveTimeZone();
        var startUtc = DateTimeOffset.UtcNow.AddHours(1);
        startUtc = RoundUp(startUtc, TimeSpan.FromMinutes(30));

        for (var offset = TimeSpan.Zero; offset < TimeSpan.FromDays(14); offset += TimeSpan.FromMinutes(30))
        {
            var candidateUtc = startUtc + offset;
            var endUtc = candidateUtc + duration;
            var candidateLocal = TimeZoneInfo.ConvertTime(candidateUtc, actorZone).DateTime;
            var endLocal = TimeZoneInfo.ConvertTime(endUtc, actorZone).DateTime;
            if (candidateUtc <= DateTimeOffset.UtcNow ||
                !intent.Actor.WorkingHours.IsWithinWorkingHours(candidateLocal) ||
                !intent.Actor.WorkingHours.IsWithinWorkingHours(endLocal.AddMinutes(-1)))
            {
                continue;
            }

            if (intent.Targets.All(target => IsWithinWorkingHours(target, candidateUtc, endUtc)))
            {
                return new MeetingSlot(candidateLocal, endLocal);
            }
        }

        return null;
    }

    private static bool IsWithinWorkingHours(Persona persona, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var zone = persona.ResolveTimeZone();
        var startLocal = TimeZoneInfo.ConvertTime(startUtc, zone).DateTime;
        var endLocal = TimeZoneInfo.ConvertTime(endUtc, zone).DateTime;
        return persona.WorkingHours.IsWithinWorkingHours(startLocal) &&
            persona.WorkingHours.IsWithinWorkingHours(endLocal.AddMinutes(-1));
    }

    private static DateTimeOffset RoundUp(DateTimeOffset value, TimeSpan increment)
    {
        var ticks = ((value.Ticks + increment.Ticks - 1) / increment.Ticks) * increment.Ticks;
        return new DateTimeOffset(ticks, value.Offset);
    }

    private static string FormatGraphLocal(DateTime local) => local.ToString("yyyy-MM-ddTHH:mm:ss");

    private static object ToAttendee(Persona persona) => new
    {
        emailAddress = new { address = persona.UserPrincipalName, name = persona.DisplayName },
        type = "required"
    };
}

internal readonly record struct MeetingSlot(DateTime StartLocal, DateTime EndLocal);

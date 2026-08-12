using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Engine;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Shows the day's plan without touching the tenant. The fastest way to sanity-check realism:
/// you can see who does what, when, and which storyline it belongs to.
/// </summary>
internal sealed class PlanCommand(
    IServiceProvider services,
    TenantPulseOptions options,
    ILogger logger) : CommandBase(services, options, logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var personas = await LoadPersonasAsync(commandLine, cancellationToken).ConfigureAwait(false);
        var storylines = await LoadStorylinesAsync(cancellationToken).ConfigureAwait(false);
        var engine = Services.GetRequiredService<PulseEngine>();

        var startDate = commandLine.DateValue("date", DateOnly.FromDateTime(DateTime.UtcNow));
        var days = Math.Max(1, commandLine.IntValue("days", 1));

        for (var offset = 0; offset < days; offset++)
        {
            var date = startDate.AddDays(offset);
            var plan = engine.PlanDay(date, personas, storylines);

            Console.WriteLine();
            Console.WriteLine($"── {date:dddd d MMMM yyyy} ── {plan.Count} activities ──────────────");

            if (plan.Count == 0)
            {
                Console.WriteLine("   (nothing planned — weekend, or all workloads disabled)");
                continue;
            }

            foreach (var intent in plan)
            {
                var local = TimeZoneInfo.ConvertTime(intent.ScheduledUtc, intent.Actor.ResolveTimeZone());
                var targets = intent.Targets.Count > 0
                    ? " → " + string.Join(", ", intent.Targets.Select(t => t.FirstName))
                    : string.Empty;

                var storyline = intent.StorylineId is null ? "" : $"  [{intent.StorylineId}]";

                Console.WriteLine(
                    $"   {local:HH:mm} {intent.Actor.TimeZoneId,-20} {intent.Kind,-16} " +
                    $"{intent.Actor.DisplayName,-22}{targets}");
                Console.WriteLine($"          {intent.Topic}{storyline}");
            }

            Summarise(plan);

            if (commandLine.Has("sample-content") && offset == 0)
            {
                await ShowSampleContentAsync(plan, commandLine.IntValue("sample-content", 4), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        Console.WriteLine();
        Console.WriteLine("   Nothing was written — 'plan' is always read-only.");
        Console.WriteLine();
        return 0;
    }

    private static void Summarise(IReadOnlyList<ActivityIntent> plan)
    {
        var byWorkload = plan
            .GroupBy(i => i.Workload)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");

        Console.WriteLine();
        Console.WriteLine($"   Summary: {string.Join(" · ", byWorkload)}");
    }

    /// <summary>
    /// Generates the actual wording for a few planned activities. This is the honest way to judge
    /// whether the tenant will read as real work or as obvious filler, before anything is sent.
    /// </summary>
    private async Task ShowSampleContentAsync(
        IReadOnlyList<ActivityIntent> plan,
        int count,
        CancellationToken cancellationToken)
    {
        var generator = Services.GetRequiredService<IContentGenerator>();

        var samples = plan
            .Where(i => i.Kind is not (ActivityKind.ReadMail or ActivityKind.Reaction))
            .GroupBy(i => i.Kind)
            .Select(g => g.First())
            .Take(Math.Max(1, count))
            .ToList();

        Console.WriteLine();
        Console.WriteLine("   Sample generated content");
        Console.WriteLine("   " + new string('─', 56));

        foreach (var intent in samples)
        {
            var shape = ShapeFor(intent.Kind);

            var content = await generator.GenerateAsync(
                new ContentRequest
                {
                    Shape = shape,
                    Intent = intent,
                    ThreadSubject = intent.Topic,
                    InReplyTo = shape is ContentShape.EmailReply or ContentShape.TeamsReply
                        ? "Thanks — can you take a look and come back to me today?"
                        : null
                },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"   [{intent.Kind}] {intent.Actor.DisplayName} ({intent.Actor.JobTitle})" +
                              (content.FromTemplate ? "  ·template·" : ""));

            if (!string.IsNullOrWhiteSpace(content.Subject))
            {
                Console.WriteLine($"   Subject: {content.Subject}");
            }

            foreach (var line in content.Body.Split('\n').Take(12))
            {
                Console.WriteLine($"     {line.TrimEnd()}");
            }
        }

        Console.WriteLine();
    }

    private static ContentShape ShapeFor(ActivityKind kind) => kind switch
    {
        ActivityKind.SendMail => ContentShape.EmailNew,
        ActivityKind.ReplyMail => ContentShape.EmailReply,
        ActivityKind.ChatMessage => ContentShape.TeamsChat,
        ActivityKind.ChannelPost => ContentShape.TeamsChannelPost,
        ActivityKind.ChannelReply => ContentShape.TeamsReply,
        ActivityKind.CreateDocument or ActivityKind.EditDocument => ContentShape.DocumentBody,
        ActivityKind.CreateEvent => ContentShape.MeetingInvite,
        ActivityKind.CopilotPrompt => ContentShape.CopilotPrompt,
        ActivityKind.AgentPrompt => ContentShape.AgentPrompt,
        _ => ContentShape.TeamsChat
    };
}

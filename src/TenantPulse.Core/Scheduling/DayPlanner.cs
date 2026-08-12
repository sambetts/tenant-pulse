using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Storylines;

namespace TenantPulse.Core.Scheduling;

/// <summary>
/// Builds a day's worth of <see cref="ActivityIntent"/>s.
/// <para>
/// Two sources are merged:
/// <list type="number">
///   <item><b>Storyline beats</b> — scripted, coherent work that spans days and keeps the same
///   cast on the same topics. This is what makes the tenant read as a real business.</item>
///   <item><b>Ambient activity</b> — the background hum (a quick chat, reading mail, a Copilot
///   prompt) sized by each persona's traits.</item>
/// </list>
/// Everything is placed inside the persona's own working hours in their own time zone, with jitter,
/// so activity arrives the way people actually work rather than on the hour every hour.
/// </para>
/// </summary>
public sealed class DayPlanner(TenantPulseOptions options)
{
    /// <summary>
    /// Produces the ordered plan for <paramref name="date"/> (interpreted per-persona in local time).
    /// </summary>
    public IReadOnlyList<ActivityIntent> PlanDay(
        DateOnly date,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<RunningStoryline> storylines)
    {
        var eligible = personas.Where(p => !p.Excluded).ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var intents = new List<ActivityIntent>();
        intents.AddRange(PlanStorylineBeats(date, storylines));
        intents.AddRange(PlanAmbient(date, eligible, storylines));

        var spaced = EnforcePerPersonaSpacing(intents);

        return [.. spaced
            .Where(i => IsWorkloadEnabled(i.Workload))
            .OrderBy(i => i.ScheduledUtc)];
    }

    /// <summary>
    /// Nudges apart any two activities by the same persona that landed too close together.
    /// <para>
    /// Without this, independently-placed ambient activities routinely collide on the same minute —
    /// which both looks wrong and would be thrown away later by the safety governor's minimum
    /// spacing rule, wasting the planned activity.
    /// </para>
    /// </summary>
    private IReadOnlyList<ActivityIntent> EnforcePerPersonaSpacing(IReadOnlyList<ActivityIntent> intents)
    {
        var minimumGap = TimeSpan.FromSeconds(Math.Max(60, options.Limits.MinSecondsBetweenUserActivities));
        var adjusted = new List<ActivityIntent>(intents.Count);

        foreach (var group in intents.GroupBy(i => i.Actor.Id))
        {
            DateTimeOffset? previous = null;

            foreach (var intent in group.OrderBy(i => i.ScheduledUtc))
            {
                var scheduled = intent.ScheduledUtc;

                if (previous is not null && scheduled - previous.Value < minimumGap)
                {
                    scheduled = previous.Value + minimumGap;
                }

                // Don't push activity past the end of the actor's day just to satisfy spacing.
                var local = TimeZoneInfo.ConvertTime(scheduled, intent.Actor.ResolveTimeZone());
                if (TimeOnly.FromDateTime(local.DateTime) > intent.Actor.WorkingHours.End &&
                    intent.Hint("ambient") == "true")
                {
                    continue;
                }

                previous = scheduled;
                adjusted.Add(scheduled == intent.ScheduledUtc ? intent : intent with { ScheduledUtc = scheduled });
            }
        }

        return adjusted;
    }

    private IEnumerable<ActivityIntent> PlanStorylineBeats(DateOnly date, IReadOnlyList<RunningStoryline> storylines)
    {
        foreach (var running in storylines.Where(s => s.IsActiveOn(date)))
        {
            var dayOffset = date.DayNumber - running.StartDate.DayNumber;

            foreach (var beat in running.Storyline.Beats.Where(b => b.DayOffset == dayOffset))
            {
                var actor = running.Actor(beat.ActorRole);
                if (actor is null)
                {
                    continue;
                }

                // Beats land on a working day for their actor; if the actor isn't working, the beat
                // slips rather than firing at an implausible time.
                if (!actor.WorkingHours.IsWorkingDay(date))
                {
                    continue;
                }

                var targets = beat.TargetRoles
                    .Select(running.Actor)
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .Where(p => p.Id != actor.Id)
                    .ToList();

                var rng = DeterministicRandom.For(
                    options.Simulation.Seed, running.InstanceId, beat.Id, date.ToString("O"));

                var scheduled = PlaceInWorkingDay(actor, date, rng, beat.PreferredHour);
                if (scheduled is null)
                {
                    continue;
                }

                var hints = new Dictionary<string, string>(beat.Hints, StringComparer.OrdinalIgnoreCase)
                {
                    ["storylineTitle"] = running.Storyline.Title,
                    ["storylineSummary"] = running.Storyline.Summary
                };

                yield return new ActivityIntent
                {
                    Id = $"{running.InstanceId}:{beat.Id}:{date:yyyyMMdd}",
                    ScheduledUtc = scheduled.Value,
                    Kind = beat.Kind,
                    Actor = actor,
                    Targets = targets,
                    StorylineId = running.Storyline.Id,
                    BeatId = beat.Id,
                    Topic = running.ResolveTopic(beat.Topic),
                    Hints = hints
                };
            }
        }
    }

    private IEnumerable<ActivityIntent> PlanAmbient(
        DateOnly date,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<RunningStoryline> storylines)
    {
        foreach (var persona in personas)
        {
            var rng = DeterministicRandom.For(options.Simulation.Seed, "ambient", persona.Id, date.ToString("O"));
            var isWorkingDay = persona.WorkingHours.IsWorkingDay(date);

            // Off-days get at most a token amount of activity, and only for people who do that.
            if (!isWorkingDay)
            {
                if (!options.Simulation.IncludeAfterHours || !rng.Chance(persona.Traits.AfterHoursPropensity * 0.5))
                {
                    continue;
                }
            }

            var budget = AmbientBudget(persona, rng, isWorkingDay);

            for (var i = 0; i < budget; i++)
            {
                var kind = PickAmbientKind(persona, rng);
                if (kind is null)
                {
                    continue;
                }

                var scheduled = PlaceInWorkingDay(persona, date, rng, preferredHour: null, allowAfterHours: !isWorkingDay);
                if (scheduled is null)
                {
                    continue;
                }

                var targets = PickTargets(persona, personas, kind.Value, rng);

                yield return new ActivityIntent
                {
                    Id = $"ambient:{persona.Id}:{date:yyyyMMdd}:{i}",
                    ScheduledUtc = scheduled.Value,
                    Kind = kind.Value,
                    Actor = persona,
                    Targets = targets,
                    Topic = AmbientTopic(persona, kind.Value, storylines, rng),
                    Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ambient"] = "true"
                    }
                };
            }
        }
    }

    private int AmbientBudget(Persona persona, Random rng, bool isWorkingDay)
    {
        if (!isWorkingDay)
        {
            return rng.Next(1, 3);
        }

        var appetite =
            (persona.Traits.Chattiness + persona.Traits.MailVolume + persona.Traits.FileVolume) / 3.0;

        // 2..9 activities for a normal working day, shaped by traits and capped by policy.
        var baseline = 2 + (int)Math.Round(appetite * 7);
        var jittered = Math.Max(1, baseline + rng.Next(-1, 2));
        return Math.Min(jittered, Math.Max(1, options.Limits.MaxActivitiesPerUserPerDay - 2));
    }

    private ActivityKind? PickAmbientKind(Persona persona, Random rng)
    {
        var candidates = new List<(ActivityKind Kind, double Weight)>();

        if (options.Workloads.Teams)
        {
            candidates.Add((ActivityKind.ChatMessage, 3 * persona.Traits.Chattiness + 0.4));
            candidates.Add((ActivityKind.ChannelPost, 1.4 * persona.Traits.Chattiness + 0.2));
            candidates.Add((ActivityKind.Reaction, 1.6 * persona.Traits.Chattiness + 0.3));
        }

        if (options.Workloads.Mail)
        {
            candidates.Add((ActivityKind.SendMail, 2.2 * persona.Traits.MailVolume + 0.3));
            candidates.Add((ActivityKind.ReplyMail, 2.0 * persona.Traits.MailVolume + 0.3));
            candidates.Add((ActivityKind.ReadMail, 1.8));
        }

        if (options.Workloads.Files)
        {
            candidates.Add((ActivityKind.CreateDocument, 1.0 * persona.Traits.FileVolume + 0.15));
            candidates.Add((ActivityKind.EditDocument, 1.6 * persona.Traits.FileVolume + 0.25));
        }

        if (options.Workloads.Calendar)
        {
            candidates.Add((ActivityKind.CreateEvent, 0.9 * persona.Traits.MeetingLoad));
        }

        if (options.Workloads.Copilot && options.Copilot.Enabled && persona.HasCopilotLicence)
        {
            candidates.Add((ActivityKind.CopilotPrompt, 2.4 * persona.Traits.CopilotAffinity + 0.2));
        }

        if (options.Workloads.Agents && options.Copilot.Agents.Count > 0)
        {
            candidates.Add((ActivityKind.AgentPrompt, 0.8 * persona.Traits.CopilotAffinity));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var picked = rng.WeightedPick(candidates, c => c.Weight);
        return picked.Weight <= 0 ? null : picked.Kind;
    }

    private static List<Persona> PickTargets(
        Persona actor,
        IReadOnlyList<Persona> everyone,
        ActivityKind kind,
        Random rng)
    {
        if (kind is ActivityKind.CopilotPrompt or ActivityKind.AgentPrompt
            or ActivityKind.ReadMail or ActivityKind.CreateDocument or ActivityKind.EditDocument
            or ActivityKind.ChannelPost or ActivityKind.ChannelReply or ActivityKind.Reaction)
        {
            return [];
        }

        var pool = everyone.Where(p => p.Id != actor.Id).ToList();
        if (pool.Count == 0)
        {
            return [];
        }

        // Prefer colleagues in the same department — people mostly talk to their own team.
        var sameTeam = pool
            .Where(p => !string.IsNullOrWhiteSpace(p.Department) &&
                        string.Equals(p.Department, actor.Department, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var preferred = sameTeam.Count > 0 && rng.Chance(0.65) ? sameTeam : pool;

        var count = kind == ActivityKind.SendMail && rng.Chance(0.25)
            ? Math.Min(3, preferred.Count)
            : 1;

        return rng.Shuffled(preferred).Take(count).ToList();
    }

    /// <summary>
    /// Ambient topics are the background hum. They mostly orbit whatever storylines the persona is
    /// involved in — but as a specific angle on that work ("chasing the outstanding numbers"), never
    /// the storyline title repeated verbatim, which is what makes filler look like filler.
    /// </summary>
    private static string AmbientTopic(
        Persona persona,
        ActivityKind kind,
        IReadOnlyList<RunningStoryline> storylines,
        Random rng)
    {
        var involved = storylines
            .Where(s => s.Cast.Values.Any(p => p.Id == persona.Id))
            .Select(s => s.Storyline.Title)
            .ToList();

        var nearby = involved.Count > 0
            ? involved
            : [.. storylines.Select(s => s.Storyline.Title)];

        // Someone not cast in anything talks about their own department's work instead.
        if (nearby.Count == 0 || !rng.Chance(involved.Count > 0 ? 0.75 : 0.4))
        {
            return GenericTopic(persona, kind, rng);
        }

        var storyline = nearby[rng.Next(nearby.Count)];
        var angle = AngleFor(kind, rng);

        return $"{storyline} — {angle}";
    }

    private static string AngleFor(ActivityKind kind, Random rng)
    {
        string[] angles = kind switch
        {
            ActivityKind.CopilotPrompt =>
            [
                "catching up on what changed", "summarising the latest documents",
                "what are my open actions", "pulling together a status update",
                "what did I miss while I was out"
            ],
            ActivityKind.CreateDocument =>
            [
                "first draft of the summary", "working notes", "checklist for the next stage",
                "options paper", "handover notes"
            ],
            ActivityKind.EditDocument =>
            [
                "picking up review comments", "updating the numbers", "tidying before it goes out",
                "adding the latest position"
            ],
            ActivityKind.CreateEvent =>
            [
                "quick sync", "checkpoint", "review session", "catch-up before the deadline"
            ],
            ActivityKind.ChannelPost or ActivityKind.ChannelReply =>
            [
                "where we got to this week", "one thing I could use help with",
                "flagging a risk", "sharing the latest draft", "small update"
            ],
            ActivityKind.ReplyMail =>
            [
                "answering the open question", "coming back on the timings",
                "confirming the numbers", "picking up your point"
            ],
            ActivityKind.SendMail =>
            [
                "next steps and owners", "a couple of questions", "timings for next week",
                "quick summary for you", "asking for a decision"
            ],
            _ =>
            [
                "quick question", "chasing the outstanding bits", "checking timings",
                "thanks — that's done", "one blocker to flag"
            ]
        };

        return angles[rng.Next(angles.Length)];
    }

    private static string GenericTopic(Persona persona, ActivityKind kind, Random rng)
    {
        var dept = string.IsNullOrWhiteSpace(persona.Department) ? "the team" : persona.Department;

        string[] pool = kind switch
        {
            ActivityKind.CreateEvent =>
            [
                $"{dept} weekly sync", $"{dept} planning session", "1:1 catch-up", "sprint review"
            ],
            ActivityKind.CopilotPrompt =>
            [
                "summarise my unread mail", "what did I miss this week", "draft an update for my manager",
                $"summarise the latest {dept} documents", "what are the action items from my meetings"
            ],
            ActivityKind.CreateDocument or ActivityKind.EditDocument =>
            [
                $"{dept} status notes", "meeting notes", $"{dept} monthly summary", "process checklist"
            ],
            _ =>
            [
                $"{dept} priorities this week", "quick question", "status update", "handover notes",
                "next week's plan"
            ]
        };

        return pool[rng.Next(pool.Length)];
    }

    /// <summary>
    /// Places an activity at a plausible instant inside the persona's local working day and returns
    /// it as UTC. Returns null when no slot is available (non-working day and after-hours disabled).
    /// </summary>
    private DateTimeOffset? PlaceInWorkingDay(
        Persona persona,
        DateOnly date,
        Random rng,
        int? preferredHour,
        bool allowAfterHours = false)
    {
        var tz = persona.ResolveTimeZone();
        var hours = persona.WorkingHours;

        TimeOnly localTime;

        if (allowAfterHours || !hours.IsWorkingDay(date))
        {
            if (!options.Simulation.IncludeAfterHours)
            {
                return null;
            }

            // Evening/weekend dabbling: a bit before work, or in the evening.
            localTime = rng.Chance(0.5)
                ? new TimeOnly(rng.Next(7, 9), rng.Next(0, 60))
                : new TimeOnly(rng.Next(18, 22), rng.Next(0, 60));
        }
        else if (preferredHour is int hour)
        {
            var clamped = Math.Clamp(hour, hours.Start.Hour, Math.Max(hours.Start.Hour, hours.End.Hour - 1));
            localTime = new TimeOnly(clamped, rng.Next(0, 60));
        }
        else
        {
            localTime = RandomWorkingMoment(hours, rng);
        }

        var localDateTime = date.ToDateTime(localTime);
        return ToUtc(localDateTime, tz);
    }

    private static TimeOnly RandomWorkingMoment(WorkingHours hours, Random rng)
    {
        var startMinutes = hours.Start.Hour * 60 + hours.Start.Minute;
        var endMinutes = hours.End.Hour * 60 + hours.End.Minute;
        if (endMinutes <= startMinutes)
        {
            endMinutes = startMinutes + 60;
        }

        var lunchStart = hours.LunchStart.Hour * 60 + hours.LunchStart.Minute;
        var lunchEnd = lunchStart + hours.LunchMinutes;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            // Two humps: people are busiest mid-morning and mid-afternoon.
            var t = rng.Chance(0.55)
                ? Gaussian(rng, startMinutes + (endMinutes - startMinutes) * 0.28, 55)
                : Gaussian(rng, startMinutes + (endMinutes - startMinutes) * 0.72, 65);

            var minute = (int)Math.Round(t);
            if (minute < startMinutes || minute > endMinutes)
            {
                continue;
            }

            if (minute >= lunchStart && minute < lunchEnd)
            {
                continue;
            }

            return new TimeOnly(minute / 60 % 24, minute % 60);
        }

        var fallback = rng.Next(startMinutes, endMinutes);
        return new TimeOnly(fallback / 60 % 24, fallback % 60);
    }

    private static double Gaussian(Random rng, double mean, double stdDev)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * normal;
    }

    /// <summary>
    /// Converts a persona-local wall-clock time to UTC, stepping over DST gaps and resolving
    /// ambiguous (repeated) times to the first occurrence.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime localDateTime, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = tz.IsAmbiguousTime(unspecified)
            ? tz.GetAmbiguousTimeOffsets(unspecified).Max()
            : tz.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private bool IsWorkloadEnabled(Workload workload) => workload switch
    {
        Workload.Mail => options.Workloads.Mail,
        Workload.Teams => options.Workloads.Teams,
        Workload.Files => options.Workloads.Files,
        Workload.Calendar => options.Workloads.Calendar,
        Workload.Copilot => options.Workloads.Copilot && options.Copilot.Enabled,
        Workload.Agents => options.Workloads.Agents,
        _ => false
    };
}

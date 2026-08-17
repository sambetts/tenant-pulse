using AwesomeAssertions;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Scheduling;
using TenantPulse.Core.Storylines;

namespace TenantPulse.Tests.Scheduling;

public class DayPlannerTests
{
    private static TenantPulseOptions Options(Action<TenantPulseOptions>? configure = null)
    {
        var options = new TenantPulseOptions
        {
            Simulation = { Seed = 12345, ConcurrentStorylines = 2, IncludeAfterHours = false }
        };

        configure?.Invoke(options);
        return options;
    }

    private static readonly DateOnly Wednesday = new(2026, 8, 12);
    private static readonly DateOnly Saturday = new(2026, 8, 15);

    [Fact]
    public void PlanDay_produces_activity_for_a_normal_working_day()
    {
        var plan = new DayPlanner(Options()).PlanDay(Wednesday, TestData.Workforce(), []);

        plan.Should().NotBeEmpty("a workforce with no storylines should still generate ambient activity");
    }

    [Fact]
    public void PlanDay_is_deterministic_for_the_same_seed()
    {
        var personas = TestData.Workforce();

        var first = new DayPlanner(Options()).PlanDay(Wednesday, personas, []);
        var second = new DayPlanner(Options()).PlanDay(Wednesday, personas, []);

        second.Select(i => i.Id).Should().Equal(first.Select(i => i.Id));
        second.Select(i => i.ScheduledUtc).Should().Equal(first.Select(i => i.ScheduledUtc));
    }

    [Fact]
    public void PlanDay_differs_between_seeds()
    {
        var personas = TestData.Workforce();

        var first = new DayPlanner(Options(o => o.Simulation.Seed = 1)).PlanDay(Wednesday, personas, []);
        var second = new DayPlanner(Options(o => o.Simulation.Seed = 2)).PlanDay(Wednesday, personas, []);

        first.Select(i => i.ScheduledUtc).Should().NotEqual(second.Select(i => i.ScheduledUtc));
    }

    [Fact]
    public void PlanDay_returns_activities_in_chronological_order()
    {
        var plan = new DayPlanner(Options()).PlanDay(Wednesday, TestData.Workforce(), []);

        plan.Select(i => i.ScheduledUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Activities_land_inside_each_persona_working_hours()
    {
        var plan = new DayPlanner(Options()).PlanDay(Wednesday, TestData.Workforce(), []);

        foreach (var intent in plan)
        {
            var local = TimeZoneInfo.ConvertTime(intent.ScheduledUtc, intent.Actor.ResolveTimeZone());

            intent.Actor.WorkingHours.IsWithinWorkingHours(local.DateTime)
                .Should().BeTrue(
                    "{0} was scheduled at {1:HH:mm} local, outside {2}'s {3}–{4} working window",
                    intent.Kind, local, intent.Actor.DisplayName,
                    intent.Actor.WorkingHours.Start, intent.Actor.WorkingHours.End);
        }
    }

    [Fact]
    public void Activities_avoid_the_lunch_hour()
    {
        var plan = new DayPlanner(Options()).PlanDay(Wednesday, TestData.Workforce(12), []);

        foreach (var intent in plan)
        {
            var local = TimeZoneInfo.ConvertTime(intent.ScheduledUtc, intent.Actor.ResolveTimeZone());
            var time = TimeOnly.FromDateTime(local.DateTime);
            var hours = intent.Actor.WorkingHours;
            var lunchEnd = hours.LunchStart.AddMinutes(hours.LunchMinutes);

            (time >= hours.LunchStart && time < lunchEnd)
                .Should().BeFalse("nobody should be working at {0:HH:mm}, they're at lunch", local);
        }
    }

    [Fact]
    public void Weekends_are_quiet_when_after_hours_is_disabled()
    {
        var plan = new DayPlanner(Options()).PlanDay(Saturday, TestData.Workforce(), []);

        plan.Should().BeEmpty("nobody works Saturday when IncludeAfterHours is off");
    }

    [Fact]
    public void Disabled_workloads_produce_no_activity_of_that_kind()
    {
        var options = Options(o =>
        {
            o.Workloads.Mail = false;
            o.Workloads.Copilot = false;
        });

        var plan = new DayPlanner(options).PlanDay(Wednesday, TestData.Workforce(12), []);

        plan.Should().NotContain(i => i.Workload == Workload.Mail);
        plan.Should().NotContain(i => i.Workload == Workload.Copilot);
    }

    [Fact]
    public void Copilot_activity_is_never_planned_for_unlicensed_users()
    {
        var personas = TestData.Workforce(10)
            .Select((p, i) => p with { HasCopilotLicence = i % 2 == 0 })
            .ToList();

        var plan = new DayPlanner(Options()).PlanDay(Wednesday, personas, []);

        plan.Where(i => i.Kind == ActivityKind.CopilotPrompt)
            .Should().OnlyContain(i => i.Actor.HasCopilotLicence);
    }

    [Fact]
    public void Excluded_personas_never_act()
    {
        var personas = TestData.Workforce(6)
            .Select((p, i) => i == 0 ? p with { Excluded = true } : p)
            .ToList();

        var plan = new DayPlanner(Options()).PlanDay(Wednesday, personas, []);

        plan.Should().NotContain(i => i.Actor.Id == personas[0].Id);
    }

    [Fact]
    public void Nobody_exceeds_the_configured_daily_activity_cap()
    {
        var options = Options(o => o.Limits.MaxActivitiesPerUserPerDay = 6);
        var plan = new DayPlanner(options).PlanDay(Wednesday, TestData.Workforce(10), []);

        foreach (var group in plan.GroupBy(i => i.Actor.Id))
        {
            group.Count().Should().BeLessThanOrEqualTo(6,
                "{0} was planned {1} activities but the cap is 6", group.Key, group.Count());
        }
    }

    [Fact]
    public void Intensity_scales_ambient_volume_without_flattening_persona_differences()
    {
        var personas = TestData.Workforce(10);
        var headroom = Options(o =>
        {
            o.Limits.MaxActivitiesPerUserPerDay = 200;
            o.Simulation.ActivityIntensity = 3.0;
        });

        var normal = new DayPlanner(Options(o => o.Limits.MaxActivitiesPerUserPerDay = 200))
            .PlanDay(Wednesday, personas, []);
        var busier = new DayPlanner(headroom).PlanDay(Wednesday, personas, []);

        busier.Count.Should().BeGreaterThan(normal.Count * 2,
            "intensity 3.0 should produce substantially more activity");

        // Flattening everyone to the same volume is what makes simulated traffic read as simulated,
        // so the spread between the busiest and quietest persona must survive the dial.
        var perPersona = busier.GroupBy(i => i.Actor.Id).Select(g => g.Count()).ToList();
        perPersona.Max().Should().BeGreaterThan(perPersona.Min());
    }

    [Fact]
    public void Intensity_never_overrides_the_safety_cap()
    {
        var options = Options(o =>
        {
            o.Limits.MaxActivitiesPerUserPerDay = 6;
            o.Simulation.ActivityIntensity = 10.0;
        });

        var plan = new DayPlanner(options).PlanDay(Wednesday, TestData.Workforce(10), []);

        foreach (var group in plan.GroupBy(i => i.Actor.Id))
        {
            group.Count().Should().BeLessThanOrEqualTo(6,
                "{0} was planned {1} activities but the cap is 6", group.Key, group.Count());
        }
    }

    [Fact]
    public void An_actor_never_targets_themselves()
    {
        var plan = new DayPlanner(Options()).PlanDay(Wednesday, TestData.Workforce(10), []);

        plan.Should().NotContain(i => i.Targets.Any(t => t.Id == i.Actor.Id));
    }

    [Fact]
    public void Storyline_beats_appear_on_the_right_day_with_the_right_cast()
    {
        var personas = TestData.Workforce(10);
        var storyline = TestStoryline();

        var running = new CastingDirector(personas)
            .Cast(storyline, Wednesday, "instance-1", seed: 42);

        running.Should().NotBeNull();

        var plan = new DayPlanner(Options()).PlanDay(Wednesday, personas, [running!]);

        var beat = plan.SingleOrDefault(i => i.BeatId == "kickoff");
        beat.Should().NotBeNull("the day-0 beat should be scheduled on the storyline's start date");
        beat!.Actor.Id.Should().Be(running!.Cast["lead"].Id);
        beat.StorylineId.Should().Be(storyline.Id);
        beat.Topic.Should().Be("Test project kickoff");
    }

    [Fact]
    public void Storyline_beats_do_not_appear_before_the_storyline_starts()
    {
        var personas = TestData.Workforce(10);
        var running = new CastingDirector(personas)
            .Cast(TestStoryline(), Wednesday.AddDays(3), "instance-2", seed: 42);

        var plan = new DayPlanner(Options()).PlanDay(Wednesday, personas, [running!]);

        plan.Should().NotContain(i => i.BeatId == "kickoff");
    }

    [Fact]
    public void Beat_preferred_hour_is_respected()
    {
        var personas = TestData.Workforce(10);
        var running = new CastingDirector(personas).Cast(TestStoryline(), Wednesday, "i3", 42);

        var plan = new DayPlanner(Options()).PlanDay(Wednesday, personas, [running!]);
        var beat = plan.Single(i => i.BeatId == "kickoff");

        var local = TimeZoneInfo.ConvertTime(beat.ScheduledUtc, beat.Actor.ResolveTimeZone());
        local.Hour.Should().Be(10);
    }

    private static Storyline TestStoryline() => new()
    {
        Id = "test-project",
        Title = "Test project",
        Summary = "A test storyline.",
        Roles =
        [
            new StorylineRole { Name = "lead" },
            new StorylineRole { Name = "helper" }
        ],
        Beats =
        [
            new StorylineBeat
            {
                Id = "kickoff",
                DayOffset = 0,
                Kind = ActivityKind.SendMail,
                ActorRole = "lead",
                TargetRoles = ["helper"],
                Topic = "Test project kickoff",
                PreferredHour = 10
            },
            new StorylineBeat
            {
                Id = "followup",
                DayOffset = 2,
                Kind = ActivityKind.ChatMessage,
                ActorRole = "helper",
                TargetRoles = ["lead"],
                Topic = "Following up"
            }
        ]
    };
}

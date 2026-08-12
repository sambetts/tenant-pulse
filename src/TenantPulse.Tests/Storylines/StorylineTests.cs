using AwesomeAssertions;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Storylines;

namespace TenantPulse.Tests.Storylines;

public class StorylineTests
{
    private static readonly DateOnly Monday = new(2026, 8, 10);

    private static Storyline Sample(string id = "bid", double weight = 1) => new()
    {
        Id = id,
        Title = $"{id} storyline",
        Summary = "Summary.",
        Weight = weight,
        Roles =
        [
            new StorylineRole { Name = "lead", PreferredArchetypes = [new PersonaArchetypeName("Sales")] },
            new StorylineRole { Name = "analyst", PreferredArchetypes = [new PersonaArchetypeName("Finance")] }
        ],
        Beats =
        [
            new StorylineBeat
            {
                Id = "b0", DayOffset = 0, Kind = Core.Activities.ActivityKind.SendMail,
                ActorRole = "lead", TargetRoles = ["analyst"], Topic = "Kickoff with {analyst}"
            },
            new StorylineBeat
            {
                Id = "b3", DayOffset = 3, Kind = Core.Activities.ActivityKind.ChatMessage,
                ActorRole = "analyst", TargetRoles = ["lead"], Topic = "Update"
            }
        ]
    };

    [Fact]
    public void Duration_is_derived_from_the_last_beat()
    {
        Sample().DurationDays.Should().Be(4);
    }

    [Fact]
    public void Casting_fills_every_role_with_distinct_people()
    {
        var running = new CastingDirector(TestData.Workforce(10)).Cast(Sample(), Monday, "i1", 7);

        running.Should().NotBeNull();
        running!.Cast.Should().HaveCount(2);
        running.Cast["lead"].Id.Should().NotBe(running.Cast["analyst"].Id);
    }

    [Fact]
    public void Casting_prefers_matching_archetypes()
    {
        var personas = TestData.Workforce(12);

        var running = new CastingDirector(personas).Cast(Sample(), Monday, "i1", 7);

        running!.Cast["lead"].Archetype.Should().Be(PersonaArchetype.Sales);
        running.Cast["analyst"].Archetype.Should().Be(PersonaArchetype.Finance);
    }

    [Fact]
    public void Casting_is_stable_for_the_same_instance_and_seed()
    {
        var personas = TestData.Workforce(10);

        var first = new CastingDirector(personas).Cast(Sample(), Monday, "same-instance", 7);
        var second = new CastingDirector(personas).Cast(Sample(), Monday, "same-instance", 7);

        second!.Cast["lead"].Id.Should().Be(first!.Cast["lead"].Id);
        second.Cast["analyst"].Id.Should().Be(first.Cast["analyst"].Id);
    }

    [Fact]
    public void Casting_fails_gracefully_when_there_are_too_few_people()
    {
        var running = new CastingDirector([TestData.Persona("solo", "Solo Person")])
            .Cast(Sample(), Monday, "i1", 7);

        running.Should().BeNull("a two-role storyline cannot be cast from one person");
    }

    [Fact]
    public void Casting_excludes_excluded_personas()
    {
        var personas = TestData.Workforce(4).Select(p => p with { Excluded = true }).ToList();

        new CastingDirector(personas).Cast(Sample(), Monday, "i1", 7).Should().BeNull();
    }

    [Fact]
    public void Topic_placeholders_are_replaced_with_cast_first_names()
    {
        var running = new CastingDirector(TestData.Workforce(10)).Cast(Sample(), Monday, "i1", 7);

        var resolved = running!.ResolveTopic("Kickoff with {analyst}");

        resolved.Should().Be($"Kickoff with {running.Cast["analyst"].FirstName}");
        resolved.Should().NotContain("{");
    }

    [Fact]
    public void IsActiveOn_covers_the_whole_span_and_nothing_outside_it()
    {
        var running = new CastingDirector(TestData.Workforce(10)).Cast(Sample(), Monday, "i1", 7)!;

        running.IsActiveOn(Monday.AddDays(-1)).Should().BeFalse();
        running.IsActiveOn(Monday).Should().BeTrue();
        running.IsActiveOn(Monday.AddDays(3)).Should().BeTrue();
        running.IsActiveOn(Monday.AddDays(4)).Should().BeFalse();
    }

    [Fact]
    public void Scheduler_keeps_the_configured_number_of_storylines_running()
    {
        var options = new TenantPulseOptions { Simulation = { Seed = 99, ConcurrentStorylines = 3 } };
        var catalogue = new[] { Sample("a"), Sample("b"), Sample("c") };

        var running = new StorylineScheduler(options, catalogue)
            .ActiveOn(Monday, TestData.Workforce(20));

        running.Should().HaveCount(3);
        running.Should().OnlyContain(r => r.IsActiveOn(Monday));
    }

    [Fact]
    public void Scheduler_is_deterministic_for_the_same_day_and_seed()
    {
        var options = new TenantPulseOptions { Simulation = { Seed = 99, ConcurrentStorylines = 2 } };
        var catalogue = new[] { Sample("a"), Sample("b"), Sample("c") };
        var personas = TestData.Workforce(20);

        var first = new StorylineScheduler(options, catalogue).ActiveOn(Monday, personas);
        var second = new StorylineScheduler(options, catalogue).ActiveOn(Monday, personas);

        second.Select(r => r.InstanceId).Should().Equal(first.Select(r => r.InstanceId));
    }

    [Fact]
    public void Scheduler_moves_on_to_new_storylines_over_time()
    {
        var options = new TenantPulseOptions { Simulation = { Seed = 99, ConcurrentStorylines = 2 } };
        var catalogue = new[] { Sample("a"), Sample("b"), Sample("c") };
        var personas = TestData.Workforce(20);
        var scheduler = new StorylineScheduler(options, catalogue);

        var today = scheduler.ActiveOn(Monday, personas).Select(r => r.InstanceId).ToList();
        var muchLater = scheduler.ActiveOn(Monday.AddDays(60), personas).Select(r => r.InstanceId).ToList();

        muchLater.Should().NotEqual(today, "storylines should finish and be replaced over time");
    }

    [Fact]
    public void Scheduler_copes_with_an_empty_catalogue()
    {
        var options = new TenantPulseOptions();

        new StorylineScheduler(options, []).ActiveOn(Monday, TestData.Workforce()).Should().BeEmpty();
    }
}

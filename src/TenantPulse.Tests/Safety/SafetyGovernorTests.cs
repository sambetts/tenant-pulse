using AwesomeAssertions;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Safety;
using TenantPulse.Core.Time;

namespace TenantPulse.Tests.Safety;

public class SafetyGovernorTests
{
    private const string DemoTenant = "11111111-1111-1111-1111-111111111111";
    private const string ProductionTenant = "99999999-9999-9999-9999-999999999999";

    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private static TenantPulseOptions Options(Action<TenantPulseOptions>? configure = null)
    {
        var options = new TenantPulseOptions
        {
            Tenant =
            {
                TenantId = DemoTenant,
                AllowedTenantIds = [DemoTenant]
            },
            Simulation = { KillSwitchFile = Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}") }
        };

        configure?.Invoke(options);
        return options;
    }

    private static (SafetyGovernor Governor, FixedClock Clock) Create(
        Action<TenantPulseOptions>? configure = null)
    {
        var clock = new FixedClock(Now);
        return (new SafetyGovernor(Options(configure), clock), clock);
    }

    private static ActivityIntent Intent(Persona actor, ActivityKind kind = ActivityKind.SendMail) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ScheduledUtc = Now,
        Kind = kind,
        Actor = actor,
        Topic = "test"
    };

    [Fact]
    public void Allows_a_tenant_that_is_on_the_allow_list()
    {
        var (governor, _) = Create();

        var act = governor.AssertTenantAllowed;

        act.Should().NotThrow();
    }

    [Fact]
    public void Refuses_a_tenant_that_is_not_on_the_allow_list()
    {
        var (governor, _) = Create(o => o.Tenant.TenantId = ProductionTenant);

        var act = governor.AssertTenantAllowed;

        act.Should().Throw<TenantNotAllowedException>()
            .WithMessage("*not in Tenant.AllowedTenantIds*");
    }

    [Fact]
    public void Refuses_when_the_allow_list_is_empty()
    {
        var (governor, _) = Create(o => o.Tenant.AllowedTenantIds = []);

        var act = governor.AssertTenantAllowed;

        act.Should().Throw<TenantNotAllowedException>()
            .WithMessage("*allow-list*");
    }

    [Fact]
    public void Refuses_when_no_tenant_is_configured()
    {
        var (governor, _) = Create(o => o.Tenant.TenantId = "");

        var act = governor.AssertTenantAllowed;

        act.Should().Throw<TenantNotAllowedException>();
    }

    [Fact]
    public void Allow_list_matching_ignores_case_and_whitespace()
    {
        var (governor, _) = Create(o =>
        {
            o.Tenant.TenantId = DemoTenant.ToUpperInvariant();
            o.Tenant.AllowedTenantIds = [$"  {DemoTenant}  "];
        });

        var act = governor.AssertTenantAllowed;

        act.Should().NotThrow();
    }

    [Fact]
    public void Enforces_the_minimum_gap_between_one_persona_activities()
    {
        var (governor, clock) = Create(o => o.Limits.MinSecondsBetweenUserActivities = 90);
        var actor = TestData.Persona("u1", "Ada Lovelace");

        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(30));
        var tooSoon = governor.TryBeginActivity(Intent(actor));

        tooSoon.Allowed.Should().BeFalse();
        tooSoon.Reason.Should().Contain("spacing");

        clock.Advance(TimeSpan.FromSeconds(70));
        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Enforces_the_per_user_daily_cap()
    {
        var (governor, clock) = Create(o =>
        {
            o.Limits.MaxActivitiesPerUserPerDay = 3;
            o.Limits.MinSecondsBetweenUserActivities = 0;
        });

        var actor = TestData.Persona("u1", "Ada Lovelace");

        for (var i = 0; i < 3; i++)
        {
            governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var denied = governor.TryBeginActivity(Intent(actor));

        denied.Allowed.Should().BeFalse();
        denied.Reason.Should().Contain("daily cap");
    }

    [Fact]
    public void Daily_cap_frees_up_after_a_day()
    {
        var (governor, clock) = Create(o =>
        {
            o.Limits.MaxActivitiesPerUserPerDay = 1;
            o.Limits.MinSecondsBetweenUserActivities = 0;
        });

        var actor = TestData.Persona("u1", "Ada Lovelace");

        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();
        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));

        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Enforces_the_tenant_wide_hourly_cap_across_different_users()
    {
        var (governor, clock) = Create(o =>
        {
            o.Limits.MaxActivitiesPerTenantPerHour = 3;
            o.Limits.MinSecondsBetweenUserActivities = 0;
        });

        for (var i = 0; i < 3; i++)
        {
            var actor = TestData.Persona($"u{i}", $"Person{i} Example");
            governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var denied = governor.TryBeginActivity(Intent(TestData.Persona("u9", "Nine Example")));

        denied.Allowed.Should().BeFalse();
        denied.Reason.Should().Contain("hourly cap");
    }

    [Fact]
    public void Hourly_cap_rolls_off_after_an_hour()
    {
        var (governor, clock) = Create(o =>
        {
            o.Limits.MaxActivitiesPerTenantPerHour = 1;
            o.Limits.MinSecondsBetweenUserActivities = 0;
        });

        governor.TryBeginActivity(Intent(TestData.Persona("u1", "One Example"))).Allowed.Should().BeTrue();
        governor.TryBeginActivity(Intent(TestData.Persona("u2", "Two Example"))).Allowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));

        governor.TryBeginActivity(Intent(TestData.Persona("u2", "Two Example"))).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Copilot_activity_is_denied_without_a_licence()
    {
        var (governor, _) = Create();
        var unlicensed = TestData.Persona("u1", "Ada Lovelace", copilot: false);

        var decision = governor.TryBeginActivity(Intent(unlicensed, ActivityKind.CopilotPrompt));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Copilot licence");
    }

    [Fact]
    public void Excluded_personas_are_denied()
    {
        var (governor, _) = Create();
        var excluded = TestData.Persona("u1", "Admin Account") with { Excluded = true };

        governor.TryBeginActivity(Intent(excluded)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Personas_outside_the_allowed_domains_are_denied()
    {
        var (governor, _) = Create(o => o.Tenant.AllowedDomains = ["allowed.onmicrosoft.com"]);
        var outsider = TestData.Persona("u1", "Ada Lovelace");

        governor.IsPersonaAllowed(outsider).Should().BeFalse();
        governor.TryBeginActivity(Intent(outsider)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Kill_switch_stops_everything()
    {
        var killSwitch = Path.Combine(Path.GetTempPath(), $"tp-stop-{Guid.NewGuid():N}");
        var (governor, _) = Create(o => o.Simulation.KillSwitchFile = killSwitch);
        var actor = TestData.Persona("u1", "Ada Lovelace");

        governor.IsStopRequested().Should().BeFalse();
        governor.TryBeginActivity(Intent(actor)).Allowed.Should().BeTrue();

        try
        {
            File.WriteAllText(killSwitch, "stop");

            governor.IsStopRequested().Should().BeTrue();

            var denied = governor.TryBeginActivity(Intent(actor));
            denied.Allowed.Should().BeFalse();
            denied.Reason.Should().Contain("Kill switch");
        }
        finally
        {
            File.Delete(killSwitch);
        }
    }
}

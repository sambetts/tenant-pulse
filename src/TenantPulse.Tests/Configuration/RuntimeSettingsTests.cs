using System.Text.Json;
using AwesomeAssertions;
using TenantPulse.Core.Configuration;

namespace TenantPulse.Tests.Configuration;

/// <summary>
/// Settings changed in the admin web have to outlive the container they were changed in — Azure
/// replaces it on every deployment — so the overlay and its round trip are load-bearing.
/// </summary>
public class RuntimeSettingsTests
{
    [Fact]
    public void Only_the_values_that_were_set_are_overlaid()
    {
        var options = new TenantPulseOptions
        {
            Simulation = { ActivityIntensity = 1.0 },
            Limits = { MaxActivitiesPerUserPerDay = 14, MaxActivitiesPerTenantPerHour = 60 }
        };

        new RuntimeSettings { ActivityIntensity = 3.0 }.ApplyTo(options);

        options.Simulation.ActivityIntensity.Should().Be(3.0);

        // An operator who only ever touched the volume dial must not find the rest of their
        // configuration frozen at whatever it happened to be that day.
        options.Limits.MaxActivitiesPerUserPerDay.Should().Be(14);
        options.Limits.MaxActivitiesPerTenantPerHour.Should().Be(60);
    }

    [Fact]
    public void Absurd_values_are_clamped_rather_than_trusted()
    {
        var options = new TenantPulseOptions();

        new RuntimeSettings
        {
            ActivityIntensity = 500,
            MaxActivitiesPerUserPerDay = 100_000,
            MaxActivitiesPerTenantPerHour = -5
        }.ApplyTo(options);

        options.Simulation.ActivityIntensity.Should().Be(20.0);
        options.Limits.MaxActivitiesPerUserPerDay.Should().Be(500);
        options.Limits.MaxActivitiesPerTenantPerHour.Should().Be(1);
    }

    [Fact]
    public void Settings_survive_a_json_round_trip()
    {
        var original = new RuntimeSettings
        {
            ActivityIntensity = 2.5,
            MaxActivitiesPerUserPerDay = 40,
            MaxActivitiesPerTenantPerHour = 250,
            UpdatedBy = "admin@contoso.onmicrosoft.com",
            UpdatedUtc = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<RuntimeSettings>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        restored.Should().Be(original);

        // IsEmpty is computed; persisting it would put a value in the store that reading it back
        // can never honour.
        json.Should().NotContain("isEmpty");
    }

    [Fact]
    public void Capture_reflects_what_is_actually_in_force()
    {
        var options = new TenantPulseOptions
        {
            Simulation = { ActivityIntensity = 4.0 },
            Limits = { MaxActivitiesPerUserPerDay = 40, MaxActivitiesPerTenantPerHour = 250 }
        };

        var captured = RuntimeSettings.CaptureFrom(options);

        captured.ActivityIntensity.Should().Be(4.0);
        captured.MaxActivitiesPerUserPerDay.Should().Be(40);
        captured.MaxActivitiesPerTenantPerHour.Should().Be(250);
        captured.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void An_empty_overlay_changes_nothing()
    {
        var options = new TenantPulseOptions { Simulation = { ActivityIntensity = 1.5 } };
        var settings = new RuntimeSettings();

        settings.IsEmpty.Should().BeTrue();
        settings.ApplyTo(options);

        options.Simulation.ActivityIntensity.Should().Be(1.5);
    }
}

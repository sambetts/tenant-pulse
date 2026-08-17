using System.Text.Json;
using AwesomeAssertions;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Telemetry;

namespace TenantPulse.Tests.Telemetry;

/// <summary>
/// The activity event is how a hosted run is reported on: the durable journal sits behind a private
/// endpoint, so stdout collected into Log Analytics is the only view available from a browser.
/// That makes the wire format load-bearing — a saved query and a portal workbook parse it — and
/// above all it must stay on one line, because the log collector splits on newlines and half an
/// event is worse than none.
/// </summary>
public class ActivityEventLogTests
{
    private static ActivityIntent Intent(
        string topic = "Quarterly close",
        string? storyline = "quarterly-close",
        IReadOnlyList<Persona>? targets = null) => new()
        {
            Id = "intent-1",
            ScheduledUtc = new DateTimeOffset(2026, 8, 17, 9, 30, 0, TimeSpan.Zero),
            Kind = ActivityKind.SendMail,
            Actor = TestData.Persona("user-01", "Megan Bowen", department: "Finance"),
            Targets = targets ?? [],
            StorylineId = storyline,
            Topic = topic
        };

    private static JsonElement Parse(string payload) => JsonDocument.Parse(payload).RootElement;

    [Fact]
    public void An_executed_activity_carries_everything_a_report_needs()
    {
        var result = ActivityResult.Executed(
            resourceId: "AAMk123",
            purgePath: "/users/x/messages/AAMk123",
            detail: "Sent to alex",
            webLink: "https://outlook.office.com/mail/id/AAMk123");

        var json = Parse(ActivityEventLog.Serialise(Intent(), result));

        json.GetProperty("v").GetInt32().Should().Be(ActivityEventLog.SchemaVersion);
        json.GetProperty("kind").GetString().Should().Be("SendMail");
        json.GetProperty("outcome").GetString().Should().Be("Executed");
        json.GetProperty("upn").GetString().Should().Be("megan.bowen@contoso.onmicrosoft.com");
        json.GetProperty("actor").GetString().Should().Be("Megan Bowen");
        json.GetProperty("dept").GetString().Should().Be("Finance");
        json.GetProperty("topic").GetString().Should().Be("Quarterly close");
        json.GetProperty("storyline").GetString().Should().Be("quarterly-close");
        json.GetProperty("link").GetString().Should().Be("https://outlook.office.com/mail/id/AAMk123");
        json.GetProperty("id").GetString().Should().Be("intent-1");
    }

    [Fact]
    public void A_failure_carries_its_error()
    {
        var json = Parse(ActivityEventLog.Serialise(Intent(), ActivityResult.Failed("403 Forbidden")));

        json.GetProperty("outcome").GetString().Should().Be("Failed");
        json.GetProperty("error").GetString().Should().Be("403 Forbidden");
    }

    [Fact]
    public void Targets_are_listed_for_activities_that_have_them()
    {
        var target = TestData.Persona("user-02", "Alex Wilber");

        var json = Parse(ActivityEventLog.Serialise(Intent(targets: [target]), ActivityResult.Executed()));

        json.GetProperty("targets").GetString().Should().Be("alex.wilber@contoso.onmicrosoft.com");
    }

    [Fact]
    public void Absent_values_are_omitted_rather_than_written_as_null()
    {
        // Every key is repeated on every activity for the life of the deployment, and Log Analytics
        // charges by ingested byte.
        var json = Parse(ActivityEventLog.Serialise(Intent(storyline: null), ActivityResult.Executed()));

        json.TryGetProperty("storyline", out _).Should().BeFalse();
        json.TryGetProperty("error", out _).Should().BeFalse();
        json.TryGetProperty("link", out _).Should().BeFalse();
        json.TryGetProperty("targets", out _).Should().BeFalse();
    }

    [Fact]
    public void A_multi_line_detail_is_still_emitted_on_one_line()
    {
        // The log collector emits one row per line. A payload broken across rows is unparseable,
        // and exception messages and generated content routinely contain newlines.
        var result = ActivityResult.Failed("first line\nsecond line\r\nthird");

        var payload = ActivityEventLog.Serialise(Intent(topic: "multi\nline topic"), result);

        payload.Should().NotContain("\n").And.NotContain("\r");
        Parse(payload).GetProperty("error").GetString().Should().Be("first line\nsecond line\r\nthird");
    }

    [Fact]
    public void The_payload_is_pure_ascii_so_it_survives_console_encoding()
    {
        // Generated content is full of em dashes and emoji, and the Windows az log viewer dies on
        // any non-ASCII byte it prints.
        var payload = ActivityEventLog.Serialise(
            Intent(topic: "Office move — hybrid working 🚴"),
            ActivityResult.Executed(detail: "café"));

        payload.Should().MatchRegex("^[\\x20-\\x7E]*$");
        Parse(payload).GetProperty("topic").GetString().Should().Be("Office move — hybrid working 🚴");
    }
}

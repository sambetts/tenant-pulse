using System.Net.Sockets;
using AwesomeAssertions;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;
using TenantPulse.Engine.Journaling;

namespace TenantPulse.Tests.Journaling;

/// <summary>
/// Exercises the Azure Table journal against the storage emulator.
/// <para>
/// The rest of the suite deliberately needs no network, so these skip themselves when Azurite is not
/// listening rather than failing. Start it with <c>azurite-table --silent --location &lt;dir&gt;</c>
/// to run them for real.
/// </para>
/// </summary>
public class AzureTableJournalTests
{
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    /// <summary>Probed once per run: seven two-second connect timeouts is a slow way to skip.</summary>
    private static readonly Lazy<bool> Emulator = new(() =>
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 10002).Wait(TimeSpan.FromSeconds(2));
        }
        catch (SocketException)
        {
            return false;
        }
        catch (AggregateException)
        {
            return false;
        }
    });

    /// <summary>A table per test, so one test's rows can never explain another's assertion.</summary>
    private static TenantPulseOptions Options() => new()
    {
        Simulation =
        {
            JournalTable =
            {
                ConnectionString = EmulatorConnectionString,
                TableName = $"tptest{Guid.NewGuid():N}"
            }
        }
    };

    private static bool EmulatorIsListening() => Emulator.Value;

    private static async Task<AzureTableActivityJournal> JournalAsync(
        TenantPulseOptions options,
        CancellationToken cancellationToken)
    {
        var journal = new AzureTableActivityJournal(options);
        await journal.InitialiseAsync(cancellationToken);
        return journal;
    }

    private static ActivityIntent Intent(
        string id,
        string upn = "cora@demo.onmicrosoft.com",
        ActivityKind kind = ActivityKind.SendMail,
        DateTimeOffset? scheduled = null) => new()
        {
            Id = id,
            Kind = kind,
            Actor = new Core.Personas.Persona
            {
                Id = "user-1",
                UserPrincipalName = upn,
                DisplayName = "Cora Thomas"
            },
            Topic = "Quarterly close",
            ScheduledUtc = scheduled ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Records_an_activity_with_its_browser_link()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        await journal.RecordAsync(
            Intent("intent-1"),
            ActivityResult.Executed(
                "msg-1",
                "users/cora/messages/msg-1",
                "Sent 'Quarterly close' to 1 recipient(s).",
                "https://outlook.office.com/mail/deeplink/read/msg-1"),
            token);

        var entries = await journal.QueryAsync(new JournalQuery { Limit = 10 }, token);

        var entry = entries.Should().ContainSingle().Subject;
        entry.IntentId.Should().Be("intent-1");
        entry.WebLink.Should().Be("https://outlook.office.com/mail/deeplink/read/msg-1");
        entry.PurgePath.Should().Be("users/cora/messages/msg-1");
        entry.Outcome.Should().Be(ActivityOutcome.Executed);
    }

    [Fact]
    public async Task Replaying_an_intent_updates_it_rather_than_duplicating_it()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        // Same scheduled time both times: that is what keeps the row addressable.
        var scheduled = DateTimeOffset.UtcNow;

        await journal.RecordAsync(
            Intent("intent-1", scheduled: scheduled),
            ActivityResult.Failed("Graph said no"),
            token);

        await journal.RecordAsync(
            Intent("intent-1", scheduled: scheduled),
            ActivityResult.Executed("msg-1", "users/cora/messages/msg-1", "Sent."),
            token);

        var entries = await journal.QueryAsync(new JournalQuery { Limit = 10 }, token);

        entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(ActivityOutcome.Executed);
    }

    [Fact]
    public async Task Summary_splits_each_persona_by_outcome()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        await journal.RecordAsync(Intent("a1", "cora@demo.test"), ActivityResult.Executed("1"), token);
        await journal.RecordAsync(Intent("a2", "cora@demo.test"), ActivityResult.Skipped("nothing to do"), token);
        await journal.RecordAsync(Intent("a3", "cora@demo.test"), ActivityResult.Failed("boom"), token);
        await journal.RecordAsync(Intent("b1", "omar@demo.test"), ActivityResult.Executed("2"), token);

        var summary = await journal.SummariseAsync(DateTimeOffset.UtcNow.AddDays(-1), token);

        summary.Total.Should().Be(4);
        summary.Executed.Should().Be(2);
        summary.Failed.Should().Be(1);

        var cora = summary.ByActor.Single(a => a.ActorUpn == "cora@demo.test");
        cora.Total.Should().Be(3);
        cora.Executed.Should().Be(1);
        cora.Skipped.Should().Be(1);
        cora.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Query_filters_by_persona_kind_and_outcome()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        await journal.RecordAsync(
            Intent("a1", "cora@demo.test", ActivityKind.SendMail), ActivityResult.Executed("1"), token);
        await journal.RecordAsync(
            Intent("a2", "cora@demo.test", ActivityKind.ChatMessage), ActivityResult.Executed("2"), token);
        await journal.RecordAsync(
            Intent("b1", "omar@demo.test", ActivityKind.SendMail), ActivityResult.Failed("boom"), token);

        var byPersona = await journal.QueryAsync(new JournalQuery { ActorUpn = "cora@demo.test" }, token);
        byPersona.Should().HaveCount(2);

        var byKind = await journal.QueryAsync(new JournalQuery { Kind = ActivityKind.SendMail }, token);
        byKind.Should().HaveCount(2);

        var failures = await journal.QueryAsync(new JournalQuery { Outcome = ActivityOutcome.Failed }, token);
        failures.Should().ContainSingle().Which.ActorUpn.Should().Be("omar@demo.test");
    }

    [Fact]
    public async Task Purged_entries_stop_being_offered_for_purge()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        await journal.RecordAsync(
            Intent("intent-1"),
            ActivityResult.Executed("msg-1", "users/cora/messages/msg-1", "Sent."),
            token);

        // Executed but with nothing deletable behind it, so it must never be offered.
        await journal.RecordAsync(
            Intent("intent-2"),
            ActivityResult.Executed(detail: "Marked 3 inbox message(s) as read."),
            token);

        var since = DateTimeOffset.UtcNow.AddDays(-1);

        var purgeable = await journal.PurgeableAsync(since, token);
        purgeable.Should().ContainSingle().Which.IntentId.Should().Be("intent-1");

        await journal.MarkPurgedAsync(purgeable[0], token);

        (await journal.PurgeableAsync(since, token)).Should().BeEmpty();
    }

    [Fact]
    public async Task Executed_intents_are_recognised_on_replay()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        var executed = Intent("intent-1");
        var skipped = Intent("intent-2");

        await journal.RecordAsync(executed, ActivityResult.Executed("msg-1"), token);
        await journal.RecordAsync(skipped, ActivityResult.Skipped("nothing to reply to"), token);

        (await journal.HasExecutedAsync(executed, token)).Should().BeTrue();
        (await journal.HasExecutedAsync(skipped, token)).Should().BeFalse();
        (await journal.HasExecutedAsync(Intent("never-seen"), token)).Should().BeFalse();
    }

    [Fact]
    public async Task Daily_count_ignores_skipped_and_failed_activity()
    {
        if (!EmulatorIsListening())
        {
            Assert.Skip("Azurite is not running on 127.0.0.1:10002.");
        }

        var token = TestContext.Current.CancellationToken;
        var journal = await JournalAsync(Options(), token);

        var day = DateTimeOffset.UtcNow;

        await journal.RecordAsync(Intent("a1", scheduled: day), ActivityResult.Executed("1"), token);
        await journal.RecordAsync(Intent("a2", scheduled: day), ActivityResult.Simulated("would send"), token);
        await journal.RecordAsync(Intent("a3", scheduled: day), ActivityResult.Skipped("no target"), token);
        await journal.RecordAsync(Intent("a4", scheduled: day), ActivityResult.Failed("boom"), token);

        var count = await journal.CountForActorOnDayAsync(
            "cora@demo.onmicrosoft.com",
            DateOnly.FromDateTime(day.UtcDateTime),
            token);

        count.Should().Be(2);
    }
}

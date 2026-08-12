using AwesomeAssertions;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Journaling;

namespace TenantPulse.Tests.Journaling;

/// <summary>
/// The journal is what makes the simulator reversible, so it has to survive the process dying.
/// In a container the live database sits on disposable local disk — SQLite cannot run on the SMB
/// share that provides durability — so it is snapshotted across instead.
/// </summary>
public class JournalSnapshotTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tp-journal-{Guid.NewGuid():N}");

    public JournalSnapshotTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private TenantPulseOptions Options(string working, string? snapshot) => new()
    {
        Simulation =
        {
            JournalPath = Path.Combine(_directory, working),
            JournalSnapshotPath = snapshot is null ? null : Path.Combine(_directory, snapshot),
            JournalSnapshotIntervalSeconds = 1
        }
    };

    private static ActivityIntent Intent(string id) => new()
    {
        Id = id,
        Kind = ActivityKind.SendMail,
        Actor = new Persona
        {
            Id = "user-1",
            UserPrincipalName = "cora@demo.onmicrosoft.com",
            DisplayName = "Cora Thomas"
        },
        Topic = "Quarterly close",
        ScheduledUtc = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task Journal_survives_losing_the_working_copy()
    {
        var token = TestContext.Current.CancellationToken;

        var first = new SqliteActivityJournal(Options("live.db", "durable.db"));
        await first.InitialiseAsync(token);
        await first.RecordAsync(
            Intent("intent-1"),
            ActivityResult.Executed("msg-1", "users/cora/messages/msg-1", "Sent."),
            token);
        await first.SnapshotAsync(token, force: true);

        // The container restarts: local disk is gone, only the share survives.
        File.Delete(Path.Combine(_directory, "live.db"));

        var second = new SqliteActivityJournal(Options("live.db", "durable.db"));
        await second.InitialiseAsync(token);

        var purgeable = await second.PurgeableAsync(DateTimeOffset.MinValue, token);

        purgeable.Should().ContainSingle()
            .Which.PurgePath.Should().Be("users/cora/messages/msg-1");
    }

    [Fact]
    public async Task Snapshot_is_a_no_op_when_no_durable_path_is_configured()
    {
        var token = TestContext.Current.CancellationToken;

        var journal = new SqliteActivityJournal(Options("live.db", snapshot: null));
        await journal.InitialiseAsync(token);
        await journal.SnapshotAsync(token, force: true);

        Directory.GetFiles(_directory, "*.db").Should().ContainSingle();
    }

    [Fact]
    public async Task Snapshot_leaves_no_staging_file_behind()
    {
        var token = TestContext.Current.CancellationToken;

        var journal = new SqliteActivityJournal(Options("live.db", "durable.db"));
        await journal.InitialiseAsync(token);
        await journal.RecordAsync(Intent("intent-1"), ActivityResult.Skipped("nothing to do"), token);
        await journal.SnapshotAsync(token, force: true);

        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
        File.Exists(Path.Combine(_directory, "durable.db")).Should().BeTrue();
    }
}

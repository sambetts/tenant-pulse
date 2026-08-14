using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Journaling;

namespace TenantPulse.Cli.Commands;

/// <summary>
/// Summarises what the simulator has actually done, and links to it.
/// <para>
/// This is the only honest answer to "did anything happen?" — the console log scrolls away and only
/// exists wherever the process ran, whereas the journal is the durable record. Pointed at an Azure
/// Table journal it reports on a run hosted in Azure from wherever it is invoked.
/// </para>
/// </summary>
internal sealed class ReportCommand(IServiceProvider services, TenantPulseOptions options, ILogger logger)
{
    public async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var journal = services.GetRequiredService<IActivityJournal>();
        await journal.InitialiseAsync(cancellationToken).ConfigureAwait(false);

        var days = Math.Max(1, commandLine.IntValue("since", 7));
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        if (!TryReadFilters(commandLine, out var persona, out var kind, out var outcome))
        {
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"tenant-pulse activity — last {days} day(s)");
        Console.WriteLine($"  source  {DescribeSource()}");
        Console.WriteLine(new string('─', 78));

        var summary = await journal.SummariseAsync(since, cancellationToken).ConfigureAwait(false);

        WriteTotals(summary);
        WriteByActivity(summary);
        WriteByPersona(summary, commandLine.Has("by-persona"));

        await WriteFailuresAsync(journal, since, cancellationToken).ConfigureAwait(false);

        await WriteEntriesAsync(journal, commandLine, since, persona, kind, outcome, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine();

        if (summary.Total == 0)
        {
            logger.LogInformation("Nothing recorded yet. Run 'tenant-pulse once --live' to get started.");
        }

        return 0;
    }

    /// <summary>Where the numbers came from, because "nothing recorded" usually means "wrong journal".</summary>
    private string DescribeSource()
    {
        var table = options.Simulation.JournalTable;

        if (!table.IsConfigured)
        {
            return $"sqlite {Path.GetFullPath(options.Simulation.JournalPath)}";
        }

        var location = string.IsNullOrWhiteSpace(table.Endpoint) ? "connection string" : table.Endpoint;
        return $"azure table {table.TableName} @ {location}";
    }

    private static bool TryReadFilters(
        CommandLine commandLine,
        out string? persona,
        out ActivityKind? kind,
        out ActivityOutcome? outcome)
    {
        persona = commandLine.Value("persona");
        kind = null;
        outcome = null;

        var kindText = commandLine.Value("kind");
        if (!string.IsNullOrWhiteSpace(kindText))
        {
            if (!Enum.TryParse<ActivityKind>(kindText, ignoreCase: true, out var parsedKind))
            {
                Console.Error.WriteLine($"Unknown activity kind '{kindText}'. Expected one of: " +
                                        string.Join(", ", Enum.GetNames<ActivityKind>()));
                return false;
            }

            kind = parsedKind;
        }

        var outcomeText = commandLine.Value("outcome");
        if (!string.IsNullOrWhiteSpace(outcomeText))
        {
            if (!Enum.TryParse<ActivityOutcome>(outcomeText, ignoreCase: true, out var parsedOutcome))
            {
                Console.Error.WriteLine($"Unknown outcome '{outcomeText}'. Expected one of: " +
                                        string.Join(", ", Enum.GetNames<ActivityOutcome>()));
                return false;
            }

            outcome = parsedOutcome;
        }

        return true;
    }

    private static void WriteTotals(JournalSummary summary)
    {
        Console.WriteLine($"  Total       {summary.Total}");
        Console.WriteLine($"  Executed    {summary.Executed}");
        Console.WriteLine($"  Simulated   {summary.Simulated}  (dry run)");
        Console.WriteLine($"  Skipped     {summary.Skipped}");
        Console.WriteLine($"  Failed      {summary.Failed}");
    }

    private static void WriteByActivity(JournalSummary summary)
    {
        if (summary.ByKind.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  By activity");
        foreach (var (kind, count) in summary.ByKind.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"    {kind,-18} {count}");
        }
    }

    private static void WriteByPersona(JournalSummary summary, bool all)
    {
        if (summary.ByActor.Count == 0)
        {
            return;
        }

        var shown = all ? summary.ByActor : summary.ByActor.Take(10).ToList();

        Console.WriteLine();
        Console.WriteLine(all ? "  By persona" : "  By persona (top 10 — --by-persona for all)");
        Console.WriteLine($"    {"persona",-42} {"total",5} {"exec",5} {"skip",5} {"fail",5}");

        foreach (var actor in shown)
        {
            Console.WriteLine($"    {Truncate(actor.ActorUpn, 42),-42} {actor.Total,5} " +
                              $"{actor.Executed,5} {actor.Skipped,5} {actor.Failed,5}");
        }
    }

    /// <summary>Failures are always shown: a silent failure is the whole reason to read a report.</summary>
    private static async Task WriteFailuresAsync(
        IActivityJournal journal,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var failures = await journal.QueryAsync(
            new JournalQuery { Since = since, Outcome = ActivityOutcome.Failed, Limit = 20 },
            cancellationToken).ConfigureAwait(false);

        if (failures.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Failures ({failures.Count})");
        foreach (var entry in failures)
        {
            Console.WriteLine($"    {entry.OccurredUtc:yyyy-MM-dd HH:mm} {entry.Kind,-16} " +
                              $"{Truncate(entry.ActorUpn, 32),-32} {Truncate(entry.Error, 60)}");
        }
    }

    private static async Task WriteEntriesAsync(
        IActivityJournal journal,
        CommandLine commandLine,
        DateTimeOffset since,
        string? persona,
        ActivityKind? kind,
        ActivityOutcome? outcome,
        CancellationToken cancellationToken)
    {
        var filtered = persona is not null || kind is not null || outcome is not null;

        // A filter implies you want to see the matching activity, so it does not also need --recent.
        var count = commandLine.IntValue("recent", filtered ? 25 : 0);
        if (count <= 0)
        {
            return;
        }

        var entries = await journal.QueryAsync(
            new JournalQuery
            {
                Since = since,
                ActorUpn = persona,
                Kind = kind,
                Outcome = outcome,
                Limit = count
            },
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  Activity ({entries.Count})");

        if (entries.Count == 0)
        {
            Console.WriteLine("    nothing matched those filters");
            return;
        }

        foreach (var entry in entries)
        {
            Console.WriteLine();
            Console.WriteLine($"    {entry.OccurredUtc:yyyy-MM-dd HH:mm}  {entry.Outcome,-9} {entry.Kind}");
            Console.WriteLine($"      {entry.ActorUpn}  ·  {entry.Topic}");

            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                Console.WriteLine($"      {entry.Detail}");
            }

            if (!string.IsNullOrWhiteSpace(entry.Error))
            {
                Console.WriteLine($"      error: {entry.Error}");
            }

            // Printed bare on its own line so a terminal will linkify it and a copy/paste picks up
            // the whole URL rather than something truncated to fit a column.
            if (!string.IsNullOrWhiteSpace(entry.WebLink))
            {
                Console.WriteLine($"      {entry.WebLink}");
            }
        }
    }

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : string.Concat(value.AsSpan(0, length - 1), "…");
    }
}

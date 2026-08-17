using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;

namespace TenantPulse.Engine.Telemetry;

/// <summary>
/// Emits one machine-readable line per activity to stdout, so a hosted run can be reported on
/// without reaching into the tenant's private network.
/// <para>
/// The Azure Table journal remains the system of record — it holds the purge paths and it is
/// durable. But in a governed subscription the storage account keeps <c>publicNetworkAccess</c>
/// disabled, so the journal is only readable from inside the VNet. Container stdout, by contrast,
/// is collected into Log Analytics automatically, and Log Analytics is queryable from a browser
/// anywhere with nothing but RBAC. So the reporting path pushes out rather than being reached in
/// for.
/// </para>
/// <para>
/// The payload is deliberately one line of JSON behind a fixed <see cref="Marker"/>: multi-line
/// output would be split across rows by the log collector, and parsing prose is how console logs
/// become unqueryable. Non-ASCII is escaped by the default encoder, which also keeps the line
/// readable through <c>az containerapp logs</c> on Windows.
/// </para>
/// </summary>
public sealed class ActivityEventLog(TenantPulseOptions options, ILogger<ActivityEventLog> logger)
{
    /// <summary>
    /// Fixed prefix that identifies an activity event in the console stream. KQL filters on this,
    /// so changing it breaks every saved query and the workbook.
    /// </summary>
    public const string Marker = "tenant-pulse-activity";

    /// <summary>
    /// Payload schema version. Bump it when the shape changes so a query can tell old rows from
    /// new ones rather than silently reading a field that has moved.
    /// </summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public void Record(ActivityIntent intent, ActivityResult result)
    {
        if (!options.Simulation.EmitActivityEvents)
        {
            return;
        }

        // The value is substituted literally, so the JSON braces are not read as placeholders.
        logger.LogInformation("{Marker} {Event}", Marker, Serialise(intent, result));
    }

    /// <summary>Serialises one activity to the single-line JSON payload. Public for tests.</summary>
    public static string Serialise(ActivityIntent intent, ActivityResult result) =>
        JsonSerializer.Serialize(
            new ActivityEvent
            {
                Version = SchemaVersion,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                ScheduledUtc = intent.ScheduledUtc.ToString("O"),
                Kind = intent.Kind.ToString(),
                Workload = intent.Workload.ToString(),
                Outcome = result.Outcome.ToString(),
                Upn = intent.Actor.UserPrincipalName,
                Actor = intent.Actor.DisplayName,
                Department = Nullify(intent.Actor.Department),
                Targets = intent.Targets.Count == 0
                    ? null
                    : string.Join(";", intent.Targets.Select(t => t.UserPrincipalName)),
                Topic = intent.Topic,
                Storyline = Nullify(intent.StorylineId),
                Detail = Nullify(result.Detail),
                Error = Nullify(result.Error),
                Link = Nullify(result.WebLink),
                IntentId = intent.Id
            },
            SerializerOptions);

    private static string? Nullify(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Wire format. Property names are short because every one of them is repeated on every
    /// activity for the lifetime of the deployment, and Log Analytics charges by ingested byte.
    /// </summary>
    private sealed record ActivityEvent
    {
        [JsonPropertyName("v")] public required int Version { get; init; }

        [JsonPropertyName("ts")] public required string TimestampUtc { get; init; }

        [JsonPropertyName("due")] public required string ScheduledUtc { get; init; }

        [JsonPropertyName("kind")] public required string Kind { get; init; }

        [JsonPropertyName("workload")] public required string Workload { get; init; }

        [JsonPropertyName("outcome")] public required string Outcome { get; init; }

        [JsonPropertyName("upn")] public required string Upn { get; init; }

        [JsonPropertyName("actor")] public required string Actor { get; init; }

        [JsonPropertyName("dept")] public string? Department { get; init; }

        [JsonPropertyName("targets")] public string? Targets { get; init; }

        [JsonPropertyName("topic")] public required string Topic { get; init; }

        [JsonPropertyName("storyline")] public string? Storyline { get; init; }

        [JsonPropertyName("detail")] public string? Detail { get; init; }

        [JsonPropertyName("error")] public string? Error { get; init; }

        [JsonPropertyName("link")] public string? Link { get; init; }

        [JsonPropertyName("id")] public required string IntentId { get; init; }
    }
}

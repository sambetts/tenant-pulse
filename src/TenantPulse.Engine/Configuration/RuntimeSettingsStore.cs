using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;

namespace TenantPulse.Engine.Configuration;

/// <summary>
/// Persists operator-changed settings beside the journal.
/// <para>
/// Where they go follows the journal: an Azure Table when one is configured, otherwise a JSON file
/// next to the SQLite journal. That way a hosted run survives the container being replaced — which
/// Azure does on every deployment — and a local run does not need a storage account to exist.
/// </para>
/// <para>
/// A settings store must never be able to stop the simulator. Every failure here is logged and
/// swallowed: running with the configured defaults is a far better outcome than refusing to start
/// because a preference could not be read.
/// </para>
/// </summary>
public sealed class RuntimeSettingsStore : IRuntimeSettingsStore
{
    private const string PartitionKey = "settings";
    private const string RowKey = "current";
    private const string PayloadColumn = "Payload";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TableClient? _table;
    private readonly string? _filePath;
    private readonly ILogger<RuntimeSettingsStore> _logger;

    public RuntimeSettingsStore(TenantPulseOptions options, ILogger<RuntimeSettingsStore> logger)
    {
        _logger = logger;

        var journal = options.Simulation.JournalTable;
        var tableName = (string.IsNullOrWhiteSpace(journal.TableName) ? "TenantPulseJournal" : journal.TableName)
                        + "Settings";

        if (!string.IsNullOrWhiteSpace(journal.ConnectionString))
        {
            _table = new TableClient(journal.ConnectionString, tableName);
        }
        else if (!string.IsNullOrWhiteSpace(journal.Endpoint))
        {
            _table = new TableClient(new Uri(journal.Endpoint), tableName, new DefaultAzureCredential());
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.Simulation.JournalPath));
            _filePath = Path.Combine(directory ?? ".", "runtime-settings.json");
        }
    }

    public async Task<RuntimeSettings?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_table is not null)
            {
                await _table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

                var response = await _table
                    .GetEntityIfExistsAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!response.HasValue || response.Value is null)
                {
                    return null;
                }

                var payload = response.Value.GetString(PayloadColumn);
                return string.IsNullOrWhiteSpace(payload)
                    ? null
                    : JsonSerializer.Deserialize<RuntimeSettings>(payload, SerializerOptions);
            }

            if (_filePath is null || !File.Exists(_filePath))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RuntimeSettings>(text, SerializerOptions);
        }
        catch (Exception ex) when (ex is RequestFailedException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read runtime settings; using the configured values.");
            return null;
        }
    }

    public async Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(settings, SerializerOptions);

        if (_table is not null)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

            var entity = new TableEntity(PartitionKey, RowKey) { [PayloadColumn] = payload };
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_filePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_filePath, payload, cancellationToken).ConfigureAwait(false);
    }
}

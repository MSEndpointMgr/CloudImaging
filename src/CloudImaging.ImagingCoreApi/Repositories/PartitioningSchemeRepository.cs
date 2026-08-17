using System.Text.Json;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Persists and retrieves the global <see cref="PartitioningScheme"/> record from Azure Table
/// Storage. Only one row exists (PartitionKey="scheme", RowKey="default"). A sensible default
/// (standard UEFI-bootable layout) is returned when no row exists yet.
/// </summary>
public sealed partial class PartitioningSchemeRepository
{
    private const string TableName = "PartitioningScheme";
    private const string PartitionKey = "scheme";
    private const string RowKey = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TableClient _table;
    private readonly ILogger<PartitioningSchemeRepository> _logger;

    public PartitioningSchemeRepository(
        TableServiceClient tableService,
        ILogger<PartitioningSchemeRepository> logger)
    {
        _table = tableService.GetTableClient(TableName);
        _logger = logger;
    }

    /// <summary>Ensures the backing table exists. Called once on startup.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Returns the current partitioning scheme, or the standard UEFI-bootable default if none
    /// has been persisted yet.
    /// </summary>
    public async Task<PartitioningScheme> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: ct);
            return MapFromEntity(response.Value);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            LogNoSchemeFound(_logger);
            return PartitioningScheme.Default;
        }
        catch (JsonException ex)
        {
            // Guards against a row persisted before PartitionType gained a JsonStringEnumConverter
            // (partitionType stored as a raw number rather than its name) — self-heal to the
            // default layout instead of surfacing a 500 to the admin UI.
            LogSchemeUnreadable(_logger, ex);
            return PartitioningScheme.Default;
        }
    }

    /// <summary>Upserts the partitioning scheme.</summary>
    public async Task UpsertAsync(PartitioningScheme scheme, CancellationToken ct = default)
    {
        var entity = new TableEntity(PartitionKey, RowKey)
        {
            ["PartitionsJson"] = JsonSerializer.Serialize(scheme.Partitions, JsonOptions),
            ["LastModifiedAt"] = DateTimeOffset.UtcNow,
        };

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        LogSchemeUpserted(_logger);
    }

    // ------------------------------------------------------------------ helpers

    private static PartitioningScheme MapFromEntity(TableEntity e)
    {
        var json = e.GetString("PartitionsJson");
        var partitions = json is not null
            ? JsonSerializer.Deserialize<List<PartitionDefinition>>(json, JsonOptions)
            : null;

        return new PartitioningScheme
        {
            Partitions = partitions ?? PartitioningScheme.Default.Partitions,
            LastModifiedAt = e.GetDateTimeOffset("LastModifiedAt") ?? default,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No PartitioningScheme row found — returning default UEFI layout.")]
    private static partial void LogNoSchemeFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored PartitioningScheme row could not be deserialized — returning default UEFI layout.")]
    private static partial void LogSchemeUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PartitioningScheme upserted.")]
    private static partial void LogSchemeUpserted(ILogger logger);
}

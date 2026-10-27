using System.Globalization;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Per-item outcomes of imaging steps. PartitionKey: sessionId | RowKey: "{StepName}-{index:D4}".
/// A step's final report replaces its rows; rows are deleted when the session is purged.
/// </summary>
public sealed class ImagingStepItemRepository
{
    private const string TableName = "ImagingStepItems";
    private const int MaxActionsPerTransaction = 100;
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the step items table.</param>
    public ImagingStepItemRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Replaces a step's items with <paramref name="items"/>.</summary>
    public async Task ReplaceAsync(Guid sessionId, ImagingStepName stepName, IReadOnlyList<ImagingStepItem> items, CancellationToken ct = default)
    {
        var partition = sessionId.ToString();
        var existing = await ReadEntitiesAsync(partition, stepName, ct);
        foreach (var chunk in existing.Chunk(MaxActionsPerTransaction))
        {
            await _table.SubmitTransactionAsync(chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e, Azure.ETag.All)), ct);
        }

        var inserts = items.Select(item => new TableTransactionAction(TableTransactionActionType.UpsertReplace, ToEntity(partition, stepName, item))).ToList();
        foreach (var chunk in inserts.Chunk(MaxActionsPerTransaction))
        {
            await _table.SubmitTransactionAsync(chunk, ct);
        }
    }

    /// <summary>Lists a step's items in reported order.</summary>
    public async Task<List<ImagingStepItem>> ListAsync(Guid sessionId, ImagingStepName stepName, CancellationToken ct = default)
    {
        var entities = await ReadEntitiesAsync(sessionId.ToString(), stepName, ct);
        return [.. entities.Select(FromEntity).OrderBy(item => item.Index)];
    }

    /// <summary>Deletes every item of a session.</summary>
    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var partition = sessionId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition}");
        var rows = new List<TableEntity>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            rows.Add(entity);
        }
        foreach (var chunk in rows.Chunk(MaxActionsPerTransaction))
        {
            await _table.SubmitTransactionAsync(chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e, Azure.ETag.All)), ct);
        }
    }

    private async Task<List<TableEntity>> ReadEntitiesAsync(string partition, ImagingStepName stepName, CancellationToken ct)
    {
        var prefix = $"{stepName}-";
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition}");
        var rows = new List<TableEntity>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            if (entity.RowKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                rows.Add(entity);
            }
        }
        return rows;
    }

    private static TableEntity ToEntity(string partition, ImagingStepName stepName, ImagingStepItem item) =>
        new(partition, string.Create(CultureInfo.InvariantCulture, $"{stepName}-{item.Index:D4}"))
        {
            ["Index"] = item.Index,
            ["Kind"] = item.Kind.ToString(),
            ["Name"] = item.Name,
            ["Target"] = item.Target,
            ["Outcome"] = item.Outcome.ToString(),
            ["Reason"] = item.Reason,
            ["ExitCode"] = item.ExitCode,
            ["DurationMs"] = item.DurationMs,
            ["LastOutputLine"] = item.LastOutputLine,
            ["DriverAction"] = item.DriverAction?.ToString(),
            ["ComponentVersion"] = item.ComponentVersion,
            ["DriverOrigin"] = item.DriverOrigin?.ToString(),
            ["FromUsbCache"] = item.FromUsbCache,
        };

    private static ImagingStepItem FromEntity(TableEntity e) => new()
    {
        Index = e.GetInt32("Index") ?? 0,
        Kind = Enum.TryParse<StepItemKind>(e.GetString("Kind"), out var kind) ? kind : StepItemKind.RegistryValue,
        Name = e.GetString("Name") ?? string.Empty,
        Target = e.GetString("Target"),
        Outcome = Enum.TryParse<StepItemOutcome>(e.GetString("Outcome"), out var outcome) ? outcome : StepItemOutcome.Failed,
        Reason = e.GetString("Reason"),
        ExitCode = e.GetInt32("ExitCode"),
        DurationMs = e.GetInt64("DurationMs"),
        LastOutputLine = e.GetString("LastOutputLine"),
        DriverAction = Enum.TryParse<DriverAction>(e.GetString("DriverAction"), out var action) ? action : null,
        ComponentVersion = e.GetString("ComponentVersion"),
        DriverOrigin = Enum.TryParse<DriverComponentOrigin>(e.GetString("DriverOrigin"), out var origin) ? origin : null,
        FromUsbCache = e.GetBoolean("FromUsbCache"),
    };
}

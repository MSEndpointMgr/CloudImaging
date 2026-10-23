using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for ImagingStep entities.
/// PartitionKey: sessionId | RowKey: step name.
/// </summary>
public sealed class ImagingStepRepository
{
    private const string TableName = "ImagingSteps";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the imaging steps table.</param>
    public ImagingStepRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Creates or replaces a session's step record.</summary>
    public async Task UpsertAsync(Guid sessionId, ImagingStep step, CancellationToken ct = default)
    {
        var entity = new TableEntity(sessionId.ToString(), step.StepName.ToString())
        {
            ["Status"] = step.Status.ToString(),
            ["StartedAt"] = step.StartedAt,
            ["CompletedAt"] = step.CompletedAt,
            ["ErrorDetail"] = step.ErrorDetail,
            ["StepProgress"] = step.StepProgressPercent,
            ["WarningCount"] = step.WarningCount,
            ["ItemCountsJson"] = step.ItemCounts.Count == 0 ? null : JsonSerializer.Serialize(step.ItemCounts, JsonOptions),
            ["ItemsTruncated"] = step.ItemsTruncated,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>Lists all step records for a session.</summary>
    public async Task<IReadOnlyList<ImagingStep>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var partitionKey = sessionId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partitionKey}");
        var steps = new List<ImagingStep>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            steps.Add(new ImagingStep
            {
                StepName = Enum.Parse<ImagingStepName>(entity.RowKey),
                Status = Enum.Parse<ImagingStepStatus>(entity.GetString("Status") ?? nameof(ImagingStepStatus.Pending)),
                StartedAt = entity.GetDateTimeOffset("StartedAt"),
                CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
                ErrorDetail = entity.GetString("ErrorDetail"),
                StepProgressPercent = entity.GetInt32("StepProgress"),
                WarningCount = entity.GetInt32("WarningCount"),
                ItemCounts = ReadItemCounts(entity.GetString("ItemCountsJson")),
                ItemsTruncated = entity.GetBoolean("ItemsTruncated") ?? false,
            });
        }
        return steps;
    }

    private static List<ImagingStepItemCount> ReadItemCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ImagingStepItemCount>>(json, JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
    }
}

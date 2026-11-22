using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>A step record and the version of its row when it was read.</summary>
/// <param name="Step">The step.</param>
/// <param name="ETag">Version of the stored row.</param>
public sealed record StoredImagingStep(ImagingStep Step, ETag ETag);

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
    public async Task UpsertAsync(Guid sessionId, ImagingStep step, CancellationToken ct = default) =>
        await _table.UpsertEntityAsync(ToEntity(sessionId, step), TableUpdateMode.Replace, ct);

    /// <summary>
    /// Writes a session's step record only while its row is still at <paramref name="etag"/>, or
    /// does not exist yet when <paramref name="etag"/> is null. False when another report for the
    /// step was stored first.
    /// </summary>
    public async Task<bool> TryWriteAsync(Guid sessionId, ImagingStep step, ETag? etag, CancellationToken ct = default)
    {
        var entity = ToEntity(sessionId, step);
        try
        {
            if (etag is { } version)
            {
                await _table.UpdateEntityAsync(entity, version, TableUpdateMode.Replace, ct);
            }
            else
            {
                await _table.AddEntityAsync(entity, ct);
            }
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
        {
            return false;
        }
    }

    /// <summary>Lists all step records for a session.</summary>
    public async Task<IReadOnlyList<ImagingStep>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default) =>
        [.. (await GetStoredBySessionAsync(sessionId, ct)).Select(stored => stored.Step)];

    /// <summary>Lists all step records for a session with the version of each row, for <see cref="TryWriteAsync"/>.</summary>
    public async Task<IReadOnlyList<StoredImagingStep>> GetStoredBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var partitionKey = sessionId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partitionKey}");
        var steps = new List<StoredImagingStep>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            steps.Add(new StoredImagingStep(new ImagingStep
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
                StatusNote = entity.GetString("StatusNote"),
            }, entity.ETag));
        }
        return steps;
    }

    private static TableEntity ToEntity(Guid sessionId, ImagingStep step) => new(sessionId.ToString(), step.StepName.ToString())
    {
        ["Status"] = step.Status.ToString(),
        ["StartedAt"] = step.StartedAt,
        ["CompletedAt"] = step.CompletedAt,
        ["ErrorDetail"] = step.ErrorDetail,
        ["StepProgress"] = step.StepProgressPercent,
        ["WarningCount"] = step.WarningCount,
        ["ItemCountsJson"] = step.ItemCounts.Count == 0 ? null : JsonSerializer.Serialize(step.ItemCounts, JsonOptions),
        ["ItemsTruncated"] = step.ItemsTruncated,
        ["StatusNote"] = step.StatusNote,
    };

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

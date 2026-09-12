using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for ImagingStep entities.
/// PartitionKey: sessionId | RowKey: step name.
/// </summary>
public sealed class ImagingStepRepository
{
    private const string TableName = "ImagingSteps";
    private readonly TableClient _table;

    /// <summary>Initializes a new instance of <see cref="ImagingStepRepository"/>.</summary>
    public ImagingStepRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Ensures the backing table exists.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Upserts an imaging step for a given session.</summary>
    public async Task UpsertAsync(Guid sessionId, ImagingStep step, CancellationToken ct = default)
    {
        var entity = new TableEntity(sessionId.ToString(), step.StepName.ToString())
        {
            ["Status"] = step.Status.ToString(),
            ["StartedAt"] = step.StartedAt,
            ["CompletedAt"] = step.CompletedAt,
            ["ErrorDetail"] = step.ErrorDetail,
            ["StepProgress"] = step.StepProgressPercent,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>Returns all imaging steps recorded for a session.</summary>
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
            });
        }
        return steps;
    }
}

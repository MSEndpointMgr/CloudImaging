using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for Autopilot registration requests.
/// PartitionKey: "request" | RowKey: requestId. One partition keeps ETag-guarded updates simple;
/// request volume is bounded by how many devices an organization registers.
/// </summary>
public sealed class AutopilotRegistrationRepository
{
    private const string TableName = "AutopilotRegistrations";
    private const string Partition = "request";
    private readonly TableClient _table;

    public AutopilotRegistrationRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task AddAsync(AutopilotRegistrationRequest request, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(request), ct);

    public async Task<(AutopilotRegistrationRequest Request, ETag ETag)?> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, requestId.ToString(), cancellationToken: ct);
            return (FromEntity(response.Value), response.Value.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>
    /// Replaces a request only if it has not changed since <paramref name="etag"/> was read.
    /// Returns the new ETag, or null on a concurrent modification so two approvers cannot both import a device.
    /// </summary>
    public async Task<ETag?> TryReplaceAsync(AutopilotRegistrationRequest request, ETag etag, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.UpdateEntityAsync(ToEntity(request), etag, TableUpdateMode.Replace, ct);
            return response.Headers.ETag ?? ETag.All;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { return null; }
    }

    /// <summary>Deletes a request only if it has not changed since <paramref name="etag"/> was read. Returns false otherwise.</summary>
    public async Task<bool> TryDeleteAsync(Guid requestId, ETag etag, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, requestId.ToString(), etag, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { return false; }
    }

    public IAsyncEnumerable<(AutopilotRegistrationRequest Request, ETag ETag)> ListAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(e => (FromEntity(e), e.ETag));
    }

    public IAsyncEnumerable<(AutopilotRegistrationRequest Request, ETag ETag)> ListByStateAsync(AutopilotRegistrationState state, CancellationToken ct = default)
    {
        var stateName = state.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and State eq {stateName}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(e => (FromEntity(e), e.ETag));
    }

    public IAsyncEnumerable<(AutopilotRegistrationRequest Request, ETag ETag)> ListBySerialAsync(string serialNumber, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and SerialNumber eq {serialNumber}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(e => (FromEntity(e), e.ETag));
    }

    internal static TableEntity ToEntity(AutopilotRegistrationRequest r) => new(Partition, r.RequestId.ToString())
    {
        ["ReferenceCode"] = r.ReferenceCode,
        ["SerialNumber"] = r.SerialNumber,
        ["Manufacturer"] = r.Manufacturer,
        ["Model"] = r.Model,
        ["Architecture"] = MachineArchitecturePlatform.Slug(r.Architecture),
        ["LocationId"] = r.LocationId?.ToString(),
        ["LocationName"] = r.LocationName,
        ["HardwareHash"] = r.HardwareHash,
        ["StatusTokenHash"] = r.StatusTokenHash,
        ["State"] = r.State.ToString(),
        ["SubmittedAt"] = r.SubmittedAt,
        ["UpdatedAt"] = r.UpdatedAt,
        ["ExpiresAt"] = r.ExpiresAt,
        ["ClientVersion"] = r.ClientVersion,
        ["TpmVersion"] = r.TpmVersion,
        ["PreProvisioningReady"] = r.PreProvisioningReady,
        ["GroupTag"] = r.GroupTag,
        ["GroupTagDefinitionId"] = r.GroupTagDefinitionId?.ToString(),
        ["DecidedByUpn"] = r.DecidedByUpn,
        ["DecidedByObjectId"] = r.DecidedByObjectId,
        ["DecidedAt"] = r.DecidedAt,
        ["RejectionReason"] = r.RejectionReason,
        ["ImportedIdentityId"] = r.ImportedIdentityId,
        ["ImportStartedAt"] = r.ImportStartedAt,
        ["ImportCompletedAt"] = r.ImportCompletedAt,
        ["ImportAttempts"] = r.ImportAttempts,
        ["ImportErrorCode"] = r.ImportErrorCode,
        ["ImportErrorName"] = r.ImportErrorName,
    };

    internal static AutopilotRegistrationRequest FromEntity(TableEntity e) => new()
    {
        RequestId = Guid.Parse(e.RowKey),
        ReferenceCode = e.GetString("ReferenceCode") ?? string.Empty,
        SerialNumber = e.GetString("SerialNumber") ?? string.Empty,
        Manufacturer = e.GetString("Manufacturer") ?? string.Empty,
        Model = e.GetString("Model") ?? string.Empty,
        Architecture = MachineArchitecturePlatform.ParseSlugOrDefault(e.GetString("Architecture")),
        LocationId = Guid.TryParse(e.GetString("LocationId"), out var locationId) ? locationId : null,
        LocationName = e.GetString("LocationName"),
        HardwareHash = e.GetString("HardwareHash"),
        StatusTokenHash = e.GetString("StatusTokenHash"),
        State = Enum.TryParse<AutopilotRegistrationState>(e.GetString("State"), out var state) ? state : AutopilotRegistrationState.PendingApproval,
        SubmittedAt = e.GetDateTimeOffset("SubmittedAt") ?? DateTimeOffset.MinValue,
        UpdatedAt = e.GetDateTimeOffset("UpdatedAt") ?? DateTimeOffset.MinValue,
        ExpiresAt = e.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MaxValue,
        ClientVersion = e.GetString("ClientVersion"),
        TpmVersion = e.GetString("TpmVersion"),
        PreProvisioningReady = e.GetBoolean("PreProvisioningReady"),
        GroupTag = e.GetString("GroupTag"),
        GroupTagDefinitionId = Guid.TryParse(e.GetString("GroupTagDefinitionId"), out var tagId) ? tagId : null,
        DecidedByUpn = e.GetString("DecidedByUpn"),
        DecidedByObjectId = e.GetString("DecidedByObjectId"),
        DecidedAt = e.GetDateTimeOffset("DecidedAt"),
        RejectionReason = e.GetString("RejectionReason"),
        ImportedIdentityId = e.GetString("ImportedIdentityId"),
        ImportStartedAt = e.GetDateTimeOffset("ImportStartedAt"),
        ImportCompletedAt = e.GetDateTimeOffset("ImportCompletedAt"),
        ImportAttempts = e.GetInt32("ImportAttempts") ?? 0,
        ImportErrorCode = e.GetString("ImportErrorCode"),
        ImportErrorName = e.GetString("ImportErrorName"),
    };
}

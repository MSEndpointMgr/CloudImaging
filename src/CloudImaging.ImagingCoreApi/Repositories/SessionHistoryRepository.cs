using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for <see cref="SessionHistoryRecord"/> entities (Reports feature).
/// PartitionKey: fixed "history" (single partition — mirrors the fixed-partition convention
/// used by <see cref="PortalConfigurationRepository"/> and <see cref="DeviceSessionRepository"/>'s
/// "active"/"terminal" partitions, rather than time-bucketing).
/// RowKey: sessionId (GUID string).
///
/// Unlike <see cref="DeviceSessionRepository"/>, records here are intentionally long-lived —
/// they exist specifically so reporting isn't limited by the live session table's purge
/// window. Retention is still enforced, but on a separate, administrator-configurable
/// schedule (<see cref="PortalConfiguration.SessionHistoryRetentionDays"/>), via
/// <see cref="QueryDueForPurgeAsync"/>/<see cref="DeletePurgedAsync"/>.
/// </summary>
public sealed class SessionHistoryRepository
{
    private const string TableName = "SessionHistory";
    private const string HistoryPartition = "history";

    private readonly TableClient _table;

    public SessionHistoryRepository(TableServiceClient tableServiceClient)
    {
        _table = tableServiceClient.GetTableClient(TableName);
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Persists a session's terminal outcome for reporting. <paramref name="retentionDays"/>
    /// is applied once, at write time, to compute the record's PurgeAt — later changes to the
    /// configured retention only affect records written afterwards (not retroactive).
    /// </summary>
    public async Task CreateAsync(SessionHistoryRecord record, int retentionDays, CancellationToken ct = default)
    {
        var entity = ToEntity(record, retentionDays);
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>
    /// Lists history records with <see cref="SessionHistoryRecord.TerminalAt"/> within
    /// [<paramref name="from"/>, <paramref name="to"/>], for reporting.
    /// </summary>
    public IAsyncEnumerable<SessionHistoryRecord> QueryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {HistoryPartition} and TerminalAt ge {from} and TerminalAt le {to}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    /// <summary>Lists history records whose PurgeAt has already elapsed as of <paramref name="asOf"/>.</summary>
    public IAsyncEnumerable<(Guid SessionId, DateTimeOffset? TerminalAt)> QueryDueForPurgeAsync(
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {HistoryPartition} and PurgeAt le {asOf}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct)
            .Select(e => (Guid.Parse(e.RowKey), e.GetDateTimeOffset("TerminalAt")));
    }

    /// <summary>Permanently deletes a history record once its retention window has elapsed.</summary>
    public async Task DeletePurgedAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(HistoryPartition, sessionId.ToString(), cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted (e.g. a concurrent purge run) — nothing left to do.
        }
    }

    // ── Entity mapping ────────────────────────────────────────────────────────

    private static TableEntity ToEntity(SessionHistoryRecord r, int retentionDays) =>
        new(HistoryPartition, r.SessionId.ToString())
        {
            ["FinalState"] = r.FinalState.ToString(),
            ["DeviceSerialNumber"] = r.DeviceSerialNumber,
            ["DeviceManufacturer"] = r.DeviceManufacturer,
            ["DeviceModel"] = r.DeviceModel,
            ["LocationId"] = r.LocationId?.ToString(),
            ["LocationName"] = r.LocationName,
            ["PreFlightAuthorizationResult"] = r.PreFlightAuthorizationResult.ToString(),
            ["AssignedOsImageId"] = r.AssignedOsImageId?.ToString(),
            ["FailedStepName"] = r.FailedStepName?.ToString(),
            ["ErrorDetail"] = r.ErrorDetail,
            ["CreatedAt"] = r.CreatedAt,
            ["TerminalAt"] = r.TerminalAt,
            ["PurgeAt"] = r.TerminalAt + TimeSpan.FromDays(retentionDays),
        };

    private static SessionHistoryRecord FromEntity(TableEntity e) => new()
    {
        SessionId = Guid.Parse(e.RowKey),
        FinalState = Enum.Parse<SessionState>(e.GetString("FinalState") ?? nameof(SessionState.SessionFailed)),
        DeviceSerialNumber = e.GetString("DeviceSerialNumber") ?? string.Empty,
        DeviceManufacturer = e.GetString("DeviceManufacturer") ?? string.Empty,
        DeviceModel = e.GetString("DeviceModel") ?? string.Empty,
        LocationId = e.GetString("LocationId") is string locationId ? Guid.Parse(locationId) : null,
        LocationName = e.GetString("LocationName"),
        PreFlightAuthorizationResult = Enum.Parse<PreFlightAuthorizationResult>(
            e.GetString("PreFlightAuthorizationResult") ?? nameof(PreFlightAuthorizationResult.Skipped)),
        AssignedOsImageId = e.GetString("AssignedOsImageId") is string sid ? Guid.Parse(sid) : null,
        FailedStepName = e.GetString("FailedStepName") is string fsn ? Enum.Parse<ImagingStepName>(fsn) : null,
        ErrorDetail = e.GetString("ErrorDetail"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
        TerminalAt = e.GetDateTimeOffset("TerminalAt") ?? DateTimeOffset.UtcNow,
    };
}

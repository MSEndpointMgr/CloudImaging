using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for DeviceSession entities (FR-021, FR-024).
/// PartitionKey: "active" for pre-terminal sessions, "terminal" for completed/failed/unauthorized.
/// RowKey: sessionId (GUID string).
/// </summary>
public sealed class DeviceSessionRepository
{
    private const string TableName = "DeviceSessions";
    private const string ActivePartition = "active";
    private const string TerminalPartition = "terminal";

    private readonly TableClient _table;

    public DeviceSessionRepository(TableServiceClient tableServiceClient)
    {
        _table = tableServiceClient.GetTableClient(TableName);
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Persist a newly created session.</summary>
    public async Task CreateAsync(DeviceSession session, CancellationToken ct = default)
    {
        var entity = ToEntity(session, ActivePartition);
        await _table.AddEntityAsync(entity, ct);
    }

    /// <summary>Retrieve a session by ID, checking both partition buckets.</summary>
    public async Task<DeviceSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        var key = sessionId.ToString();
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(ActivePartition, key, cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            try
            {
                var response = await _table.GetEntityAsync<TableEntity>(TerminalPartition, key, cancellationToken: ct);
                return FromEntity(response.Value);
            }
            catch (RequestFailedException ex2) when (ex2.Status == 404)
            {
                return null;
            }
        }
    }

    /// <summary>Update an existing session. Moves entity to terminal partition if state is terminal.</summary>
    public async Task UpdateAsync(DeviceSession session, CancellationToken ct = default)
    {
        var key = session.SessionId.ToString();
        var isTerminal = session.State is SessionState.SessionCompleted
            or SessionState.SessionFailed
            or SessionState.SessionNotAuthorized;

        var targetPartition = isTerminal ? TerminalPartition : ActivePartition;
        var entity = ToEntity(session, targetPartition);

        // If moving to terminal, delete from active first then insert into terminal
        if (isTerminal)
        {
            try
            {
                await _table.DeleteEntityAsync(ActivePartition, key, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* already moved */ }
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        else
        {
            await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Replace, ct);
        }
    }

    /// <summary>
    /// Find a session with a matching passcode hash among currently active (non-terminal) sessions.
    /// Used for coupling passcode validation (FR-021, FR-032).
    /// </summary>
    public async Task<DeviceSession?> FindByPasscodeHashAsync(string passcodeHash, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {ActivePartition} and PasscodeHash eq {passcodeHash} and PasscodeConsumed eq false");

        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            return FromEntity(entity);
        }
        return null;
    }

    /// <summary>List all active sessions for portal dashboard (FR-031).</summary>
    public IAsyncEnumerable<DeviceSession> QueryActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {ActivePartition}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct)
            .Select(FromEntity);
    }

    /// <summary>
    /// List all sessions across both partitions (active and terminal) for the portal devices
    /// view, so completed/failed sessions remain visible until they are purged (FR-031).
    /// </summary>
    public IAsyncEnumerable<DeviceSession> QueryAllAsync(CancellationToken ct = default) =>
        _table.QueryAsync<TableEntity>(cancellationToken: ct).Select(FromEntity);

    // ── Entity mapping ────────────────────────────────────────────────────────

    private static TableEntity ToEntity(DeviceSession s, string partition) => new(partition, s.SessionId.ToString())
    {
        ["State"] = s.State.ToString(),
        ["DeviceSerialNumber"] = s.DeviceSerialNumber,
        ["DeviceManufacturer"] = s.DeviceManufacturer,
        ["DeviceModel"] = s.DeviceModel,
        ["PreFlightAuthorizationResult"] = s.PreFlightAuthorizationResult.ToString(),
        ["PasscodeHash"] = s.Passcode,
        ["PasscodeExpiresAt"] = s.PasscodeExpiresAt,
        ["PasscodeConsumed"] = s.PasscodeConsumed,
        ["DeviceSessionToken"] = s.DeviceSessionToken,
        ["DeviceSessionTokenExpiresAt"] = s.DeviceSessionTokenExpiresAt,
        ["AssignedOsImageId"] = s.AssignedOsImageId?.ToString(),
        ["SasTokenUrl"] = s.SasTokenUrl,
        ["SasTokenUrlExpiresAt"] = s.SasTokenUrlExpiresAt,
        ["PartitioningSchemeSnapshotJson"] = s.PartitioningSchemeSnapshotJson,
        ["OverallProgressPercent"] = s.OverallProgressPercent,
        ["CurrentStep"] = s.CurrentStep,
        ["CreatedAt"] = s.CreatedAt,
        ["LastHeartbeatAt"] = s.LastHeartbeatAt,
        ["TerminalAt"] = s.TerminalAt,
        ["PurgeAt"] = s.PurgeAt,
    };

    private static DeviceSession FromEntity(TableEntity e) => new()
    {
        SessionId = Guid.Parse(e.RowKey),
        State = Enum.Parse<SessionState>(e.GetString("State") ?? nameof(SessionState.SessionInit)),
        DeviceSerialNumber = e.GetString("DeviceSerialNumber") ?? string.Empty,
        DeviceManufacturer = e.GetString("DeviceManufacturer") ?? string.Empty,
        DeviceModel = e.GetString("DeviceModel") ?? string.Empty,
        PreFlightAuthorizationResult = Enum.Parse<PreFlightAuthorizationResult>(e.GetString("PreFlightAuthorizationResult") ?? nameof(PreFlightAuthorizationResult.Skipped)),
        Passcode = e.GetString("PasscodeHash"),
        PasscodeExpiresAt = e.GetDateTimeOffset("PasscodeExpiresAt"),
        PasscodeConsumed = e.GetBoolean("PasscodeConsumed") ?? false,
        DeviceSessionToken = e.GetString("DeviceSessionToken"),
        DeviceSessionTokenExpiresAt = e.GetDateTimeOffset("DeviceSessionTokenExpiresAt"),
        AssignedOsImageId = e.GetString("AssignedOsImageId") is string sid ? Guid.Parse(sid) : null,
        SasTokenUrl = e.GetString("SasTokenUrl"),
        SasTokenUrlExpiresAt = e.GetDateTimeOffset("SasTokenUrlExpiresAt"),
        PartitioningSchemeSnapshotJson = e.GetString("PartitioningSchemeSnapshotJson"),
        OverallProgressPercent = e.GetInt32("OverallProgressPercent") ?? 0,
        CurrentStep = e.GetString("CurrentStep"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
        LastHeartbeatAt = e.GetDateTimeOffset("LastHeartbeatAt"),
        TerminalAt = e.GetDateTimeOffset("TerminalAt"),
        PurgeAt = e.GetDateTimeOffset("PurgeAt"),
    };
}

using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Processes bulk image assignment with conflict-safe per-session handling (T075, FR-035).
/// Sessions already in a non-assignable state are silently skipped (not failed).
/// Sessions not found are reported as skipped.
/// </summary>
public sealed partial class BulkAssignmentService
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly OsImageRepository _imageRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BulkAssignmentService> _logger;

    public BulkAssignmentService(
        DeviceSessionRepository sessionRepo,
        OsImageRepository imageRepo,
        PortalConfigurationRepository configRepo,
        BlobServiceClient blobClient,
        ILogger<BulkAssignmentService> logger)
    {
        _sessionRepo = sessionRepo;
        _imageRepo = imageRepo;
        _configRepo = configRepo;
        _blobClient = blobClient;
        _logger = logger;
    }

    public sealed record BulkAssignResult(
        int Assigned,
        int Skipped,
        IReadOnlyList<Guid> AssignedIds,
        IReadOnlyList<Guid> SkippedIds);

    /// <summary>
    /// Assigns <paramref name="osImageId"/> to each session in <paramref name="sessionIds"/>
    /// that is currently in <see cref="SessionState.SessionAssigned"/> state.
    /// Returns a summary of assigned vs skipped sessions.
    /// </summary>
    public async Task<BulkAssignResult> AssignAsync(
        IEnumerable<Guid> sessionIds,
        Guid osImageId,
        CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(osImageId, ct);
        if (image is null)
        {
            throw new InvalidOperationException($"OS image {osImageId} not found in active catalog.");
        }

        var config = await _configRepo.GetAsync(ct);
        var sasExpiry = TimeSpan.FromMinutes(
            config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 240);

        // All sessions in this batch are assigned the same OS image, so one SAS token URL can be
        // shared across every session — avoids one user-delegation-key round trip per device.
        // Without this, sessions bulk-assigned from the Coupled Devices table never receive a
        // SasTokenUrl at all, and the Client's ImagingWorkflowViewModel polls for one until it
        // times out (5 minutes) because only the single-session /assign endpoint used to set it.
        var sasUrl = await GenerateSasUrlAsync(image.StoragePath, sasExpiry, ct);

        var assigned = new List<Guid>();
        var skipped = new List<Guid>();

        foreach (var sessionId in sessionIds)
        {
            var session = await _sessionRepo.GetByIdAsync(sessionId, ct);

            if (session is null || session.State != SessionState.SessionAssigned || session.AssignedOsImageId.HasValue)
            {
                skipped.Add(sessionId);
                // CA1873: pre-compute the string to avoid expensive evaluation when logging is disabled
                string stateForLog = session?.State.ToString() ?? "NotFound";
                LogSkipped(_logger, sessionId, stateForLog);
                continue;
            }

            var updated = new DeviceSession
            {
                SessionId = session.SessionId,
                State = SessionState.SessionStarted,
                DeviceSerialNumber = session.DeviceSerialNumber,
                DeviceManufacturer = session.DeviceManufacturer,
                DeviceModel = session.DeviceModel,
                HardwareMetadata = session.HardwareMetadata,
                PreFlightAuthorizationResult = session.PreFlightAuthorizationResult,
                Passcode = session.Passcode,
                PasscodeExpiresAt = session.PasscodeExpiresAt,
                PasscodeConsumed = session.PasscodeConsumed,
                DeviceSessionToken = session.DeviceSessionToken,
                DeviceSessionTokenExpiresAt = session.DeviceSessionTokenExpiresAt,
                AssignedOsImageId = osImageId,
                SasTokenUrl = sasUrl,
                SasTokenUrlExpiresAt = DateTimeOffset.UtcNow + sasExpiry,
                OverallProgressPercent = 0,
                CreatedAt = session.CreatedAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            };

            await _sessionRepo.UpdateAsync(updated, ct);
            assigned.Add(sessionId);
        }

        LogBulkAssignCompleted(_logger, assigned.Count, skipped.Count);
        return new BulkAssignResult(assigned.Count, skipped.Count, assigned, skipped);
    }

    private async Task<string> GenerateSasUrlAsync(string storagePath, TimeSpan expiry, CancellationToken cancellationToken)
    {
        try
        {
            // storagePath format: {container}/{blobName}
            var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                return storagePath;
            }

            var container = storagePath[..slash];
            var blobName = storagePath[(slash + 1)..];

            return await BlobSasUrlGenerator.GenerateAsync(
                _blobClient, container, blobName, BlobSasPermissions.Read, expiry, cancellationToken);
        }
        catch
        {
            return storagePath; // Fallback — caller should handle auth separately
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} skipped in bulk assign (state={State}).")]
    private static partial void LogSkipped(ILogger logger, Guid sessionId, string state);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Bulk assign complete: {Assigned} assigned, {Skipped} skipped.")]
    private static partial void LogBulkAssignCompleted(ILogger logger, int assigned, int skipped);
}

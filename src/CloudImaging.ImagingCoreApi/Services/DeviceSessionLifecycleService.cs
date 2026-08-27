using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Maintains session health by expiring inactive sessions and purging terminal records (T112, FR-021).
///
/// Lifecycle rules:
///   • Inactivity timeout: sessions with no heartbeat within <see cref="InactivityTimeout"/> are
///     expired. Sessions still in <see cref="SessionState.SessionInit"/>/<see cref="SessionState.SessionAllowed"/>
///     (never coupled by an operator) transition to <see cref="SessionState.SessionExpired"/> — a
///     benign timeout, not a failure. Sessions already coupled (<see cref="SessionState.SessionAssigned"/>/
///     <see cref="SessionState.SessionStarted"/>/<see cref="SessionState.SessionInProgress"/>) transition
///     to <see cref="SessionState.SessionFailed"/>, since an operator was already involved and the
///     interruption has real diagnostic value.
///   • Terminal purge: sessions in terminal state (Completed / Failed / Expired / NotAuthorized) that have
///     been terminal for longer than <see cref="TerminalPurgeTtl"/> are deleted from Table Storage.
///
/// Designed to be called by a Timer-triggered Azure Function on a regular schedule.
/// </summary>
public sealed partial class DeviceSessionLifecycleService
{
    /// <summary>Sessions with no heartbeat for this long are expired.</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Terminal sessions older than this are purged from storage.</summary>
    public static readonly TimeSpan TerminalPurgeTtl = TimeSpan.FromHours(24);

    private static readonly SessionState[] ActiveNonTerminalStates =
    [
        SessionState.SessionInit,
        SessionState.SessionAllowed,
        SessionState.SessionAssigned,
        SessionState.SessionStarted,
        SessionState.SessionInProgress,
    ];

    // Never-coupled states: an inactivity timeout here means the operator simply never entered
    // the passcode in time — not a real failure, so it must resolve to SessionExpired rather
    // than SessionFailed (see class remarks).
    private static readonly SessionState[] NeverCoupledStates =
    [
        SessionState.SessionInit,
        SessionState.SessionAllowed,
    ];

    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ILogger<DeviceSessionLifecycleService> _logger;

    public DeviceSessionLifecycleService(
        DeviceSessionRepository sessionRepo,
        ILogger<DeviceSessionLifecycleService> logger)
    {
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    /// <summary>
    /// Expires sessions that have not sent a heartbeat within <see cref="InactivityTimeout"/>.
    /// Returns the number of sessions expired.
    /// </summary>
    public async Task<int> ExpireInactiveSessionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - InactivityTimeout;
        int expired = 0;

        await foreach (var session in _sessionRepo.QueryActiveAsync(ct))
        {
            if (!ActiveNonTerminalStates.Contains(session.State))
            {
                continue;
            }

            var lastHeartbeat = session.LastHeartbeatAt ?? session.CreatedAt;
            if (lastHeartbeat < cutoff)
            {
                var terminalState = NeverCoupledStates.Contains(session.State)
                    ? SessionState.SessionExpired
                    : SessionState.SessionFailed;
                var transitioned = BuildTransition(session, terminalState);
                await _sessionRepo.UpdateAsync(transitioned, ct);
                LogSessionExpired(_logger, session.SessionId, lastHeartbeat, terminalState);
                expired++;
            }
        }

        LogExpiryRun(_logger, expired);
        return expired;
    }

    /// <summary>
    /// Purges terminal sessions that have been in a terminal state for longer than
    /// <see cref="TerminalPurgeTtl"/>. Returns the number of sessions purged.
    /// </summary>
    public async Task<int> PurgeTerminalSessionsAsync(CancellationToken ct = default)
    {
        int purged = 0;

        await foreach (var session in _sessionRepo.QueryTerminalDueForPurgeAsync(DateTimeOffset.UtcNow, ct))
        {
            await _sessionRepo.DeletePurgedAsync(session.SessionId, ct);
            LogSessionPurged(_logger, session.SessionId, session.TerminalAt);
            purged++;
        }

        LogPurgeRun(_logger, purged);
        return purged;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DeviceSession BuildTransition(DeviceSession s, SessionState newState) =>
        new()
        {
            SessionId = s.SessionId,
            State = newState,
            DeviceSerialNumber = s.DeviceSerialNumber,
            DeviceManufacturer = s.DeviceManufacturer,
            DeviceModel = s.DeviceModel,
            HardwareMetadata = s.HardwareMetadata,
            PreFlightAuthorizationResult = s.PreFlightAuthorizationResult,
            Passcode = s.Passcode,
            PasscodeExpiresAt = s.PasscodeExpiresAt,
            PasscodeConsumed = s.PasscodeConsumed,
            DeviceSessionToken = s.DeviceSessionToken,
            DeviceSessionTokenExpiresAt = s.DeviceSessionTokenExpiresAt,
            AssignedOsImageId = s.AssignedOsImageId,
            SasTokenUrl = s.SasTokenUrl,
            SasTokenUrlExpiresAt = s.SasTokenUrlExpiresAt,
            OverallProgressPercent = s.OverallProgressPercent,
            CurrentStep = s.CurrentStep,
            CreatedAt = s.CreatedAt,
            LastHeartbeatAt = s.LastHeartbeatAt,
            TerminalAt = DateTimeOffset.UtcNow,
            PurgeAt = DateTimeOffset.UtcNow + TerminalPurgeTtl,
        };

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} expired due to inactivity (lastHeartbeat={LastHeartbeat}), transitioned to {TerminalState}.")]
    private static partial void LogSessionExpired(
        ILogger logger, Guid sessionId, DateTimeOffset lastHeartbeat, SessionState terminalState);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inactivity expiry run complete: {Count} sessions expired.")]
    private static partial void LogExpiryRun(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Terminal session {SessionId} purged (was terminal since {TerminalAt}).")]
    private static partial void LogSessionPurged(ILogger logger, Guid sessionId, DateTimeOffset? terminalAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal purge run complete: {Count} sessions purged.")]
    private static partial void LogPurgeRun(ILogger logger, int count);
}

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
///   • Sessions actively imaging (<see cref="SessionState.SessionStarted"/>/<see cref="SessionState.SessionInProgress"/>)
///     get the longer <see cref="ActiveImagingHeartbeatTimeout"/> instead — a full disk apply can
///     legitimately outlast the pre-imaging window, and a device mid-apply must survive a transient
///     network outage rather than being declared failed while it is still working.
///   • Terminal purge: sessions in terminal state (Completed / Failed / Expired / NotAuthorized) that have
///     been terminal for longer than <see cref="TerminalPurgeTtl"/> are deleted from Table Storage.
///
/// Designed to be called by a Timer-triggered Azure Function on a regular schedule.
/// </summary>
public sealed partial class DeviceSessionLifecycleService
{
    /// <summary>Pre-imaging sessions with no heartbeat for this long are expired.</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sessions actively imaging tolerate this much heartbeat silence before being failed. The
    /// client checks in every 30s while a step is running (SessionHeartbeatCoordinator), so this
    /// is an outage/crash backstop, not the expected cadence.
    /// </summary>
    public static readonly TimeSpan ActiveImagingHeartbeatTimeout = TimeSpan.FromHours(4);

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

    // States where the device is actually applying an image and gets the longer timeout.
    private static readonly SessionState[] ActiveImagingStates =
    [
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
    private readonly SessionHistoryRepository _historyRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<DeviceSessionLifecycleService> _logger;

    public DeviceSessionLifecycleService(
        DeviceSessionRepository sessionRepo,
        SessionHistoryRepository historyRepo,
        PortalConfigurationRepository configRepo,
        ILogger<DeviceSessionLifecycleService> logger)
    {
        _sessionRepo = sessionRepo;
        _historyRepo = historyRepo;
        _configRepo = configRepo;
        _logger = logger;
    }

    /// <summary>
    /// Expires sessions that have not sent a heartbeat within the timeout applicable to their
    /// state (<see cref="InactivityTimeout"/>, or <see cref="ActiveImagingHeartbeatTimeout"/> for
    /// sessions actively imaging). Returns the number of sessions expired.
    /// </summary>
    public async Task<int> ExpireInactiveSessionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int expired = 0;

        await foreach (var session in _sessionRepo.QueryActiveAsync(ct))
        {
            if (!ActiveNonTerminalStates.Contains(session.State))
            {
                continue;
            }

            var cutoff = now - TimeoutFor(session.State);
            var lastHeartbeat = session.LastHeartbeatAt ?? session.CreatedAt;
            if (lastHeartbeat < cutoff)
            {
                var terminalState = NeverCoupledStates.Contains(session.State)
                    ? SessionState.SessionExpired
                    : SessionState.SessionFailed;
                var transitioned = BuildTransition(session, terminalState);
                await _sessionRepo.UpdateAsync(transitioned, ct);
                await WriteHistoryAsync(transitioned, ct);
                LogSessionExpired(_logger, session.SessionId, lastHeartbeat, terminalState);
                expired++;
            }
        }

        LogExpiryRun(_logger, expired);
        return expired;
    }

    /// <summary>How long a session in <paramref name="state"/> may go without a heartbeat.</summary>
    public static TimeSpan TimeoutFor(SessionState state) =>
        ActiveImagingStates.Contains(state) ? ActiveImagingHeartbeatTimeout : InactivityTimeout;

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

    /// <summary>
    /// Purges SessionHistory (Reports audit) records whose retention window has elapsed.
    /// Independent of <see cref="PurgeTerminalSessionsAsync"/> — history retention is
    /// administrator-configurable and typically much longer than the live session TTL.
    /// Returns the number of history records purged.
    /// </summary>
    public async Task<int> PurgeSessionHistoryAsync(CancellationToken ct = default)
    {
        int purged = 0;

        await foreach (var (sessionId, _) in _historyRepo.QueryDueForPurgeAsync(DateTimeOffset.UtcNow, ct))
        {
            await _historyRepo.DeletePurgedAsync(sessionId, ct);
            purged++;
        }

        LogHistoryPurgeRun(_logger, purged);
        return purged;
    }

    /// <summary>
    /// Records a SessionHistory (Reports audit) entry for an inactivity-driven terminal
    /// transition. FailedStepName is intentionally left unset here — unlike
    /// <c>ReportProgressFunction</c>, this path doesn't have direct access to the per-step
    /// records, only the session's free-text <see cref="DeviceSession.CurrentStep"/> snapshot.
    /// ErrorDetail instead captures a synthesized, still-diagnostically-useful reason.
    /// </summary>
    private async Task WriteHistoryAsync(DeviceSession s, CancellationToken ct)
    {
        var config = await _configRepo.GetAsync(ct);
        var history = new SessionHistoryRecord
        {
            SessionId = s.SessionId,
            FinalState = s.State,
            DeviceSerialNumber = s.DeviceSerialNumber,
            DeviceManufacturer = s.DeviceManufacturer,
            DeviceModel = s.DeviceModel,
            PreFlightAuthorizationResult = s.PreFlightAuthorizationResult,
            AssignedOsImageId = s.AssignedOsImageId,
            ErrorDetail = s.State == SessionState.SessionFailed
                ? $"Session inactivity timeout while in progress (last step: {s.CurrentStep ?? "unknown"})."
                : null,
            CreatedAt = s.CreatedAt,
            TerminalAt = s.TerminalAt ?? DateTimeOffset.UtcNow,
        };
        await _historyRepo.CreateAsync(history, config.SessionHistoryRetentionDays, ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "SessionHistory purge run complete: {Count} history records purged.")]
    private static partial void LogHistoryPurgeRun(ILogger logger, int count);
}

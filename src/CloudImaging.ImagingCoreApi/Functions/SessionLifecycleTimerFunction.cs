using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Timer-triggered function that maintains session health on a regular schedule (T112, FR-021).
/// Runs every 5 minutes: expires inactive sessions, purges terminal records.
/// </summary>
public sealed partial class SessionLifecycleTimerFunction
{
    private readonly DeviceSessionLifecycleService _lifecycle;
    private readonly SetupProfileService _setupProfiles;
    private readonly ILogger<SessionLifecycleTimerFunction> _logger;

    /// <param name="lifecycle">Device session lifecycle service.</param>
    /// <param name="setupProfiles">Setup profiles, whose unpinned old versions are swept.</param>
    /// <param name="logger">Logger for this function.</param>
    public SessionLifecycleTimerFunction(
        DeviceSessionLifecycleService lifecycle,
        SetupProfileService setupProfiles,
        ILogger<SessionLifecycleTimerFunction> logger)
    {
        _lifecycle = lifecycle;
        _setupProfiles = setupProfiles;
        _logger = logger;
    }

    /// <summary>Timer (every 5 minutes). Expires inactive sessions, purges terminal session records and sweeps setup profile versions no deployment needs.</summary>
    [Function("SessionLifecycleTimer")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var expired = await _lifecycle.ExpireInactiveSessionsAsync(context.CancellationToken);
        var purged = await _lifecycle.PurgeTerminalSessionsAsync(context.CancellationToken);
        var historyPurged = await _lifecycle.PurgeSessionHistoryAsync(context.CancellationToken);
        var overridesPurged = await _lifecycle.PurgeExpiredOverridesAsync(context.CancellationToken);
        var versionsSwept = await _setupProfiles.SweepAsync(context.CancellationToken);
        LogLifecycleTick(_logger, expired, purged, historyPurged, overridesPurged, versionsSwept);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session lifecycle timer: {Expired} expired, {Purged} purged, {HistoryPurged} history records purged, {OverridesPurged} expired overrides removed, {VersionsSwept} setup profile versions swept.")]
    private static partial void LogLifecycleTick(ILogger logger, int expired, int purged, int historyPurged, int overridesPurged, int versionsSwept);
}

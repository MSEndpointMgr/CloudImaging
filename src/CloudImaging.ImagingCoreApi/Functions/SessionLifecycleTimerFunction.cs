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
    private readonly ILogger<SessionLifecycleTimerFunction> _logger;

    public SessionLifecycleTimerFunction(
        DeviceSessionLifecycleService lifecycle,
        ILogger<SessionLifecycleTimerFunction> logger)
    {
        _lifecycle = lifecycle;
        _logger = logger;
    }

    [Function("SessionLifecycleTimer")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var expired = await _lifecycle.ExpireInactiveSessionsAsync(context.CancellationToken);
        var purged = await _lifecycle.PurgeTerminalSessionsAsync(context.CancellationToken);
        LogLifecycleTick(_logger, expired, purged);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session lifecycle timer: {Expired} expired, {Purged} purged.")]
    private static partial void LogLifecycleTick(ILogger logger, int expired, int purged);
}

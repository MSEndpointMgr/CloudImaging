using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Keeps the backend aware that an actively-imaging session is still alive (FR-021).
///
/// Liveness must NOT be a side effect of progress reporting. Progress reports only flow while a
/// step happens to be producing measurable progress, and they are deliberately throttled to stay
/// under the Device Gateway's per-session rate limit — so steps that legitimately go quiet (a DISM
/// commit phase, reagentc, a stalled download) would otherwise look indistinguishable from a dead
/// device and get swept into SessionFailed by the inactivity timeout. This is a dedicated
/// side-channel: a plain GET /status on a fixed cadence, independent of what any step is doing.
/// </summary>
public sealed partial class SessionHeartbeatCoordinator : IDisposable
{
    /// <summary>How often the client checks in while the imaging pipeline is running.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly ILogger<SessionHeartbeatCoordinator> _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SessionHeartbeatCoordinator(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        ILogger<SessionHeartbeatCoordinator> logger)
    {
        _gatewayClient = gatewayClient;
        _sessionId     = sessionId;
        _logger        = logger;
    }

    /// <summary>Starts the background heartbeat loop.</summary>
    public void Start() => _ = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                await _gatewayClient.GetSessionStatusAsync(_sessionId, ct);
                LogHeartbeatSent(_logger, _sessionId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A missed heartbeat is recoverable (the backend tolerates several), and imaging
                // must never be interrupted by a liveness call.
                LogHeartbeatFailed(_logger, ex, _sessionId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat sent for session {SessionId}.")]
    private static partial void LogHeartbeatSent(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat failed for session {SessionId}.")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception ex, Guid sessionId);
}

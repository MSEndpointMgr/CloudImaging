using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Reports imaging step progress to the Device Gateway API (T054, FR-007).
/// Wraps the POST /api/v1/sessions/{sessionId}/progress endpoint.
/// </summary>
public sealed partial class ImagingProgressReporter
{
    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly ILogger<ImagingProgressReporter> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Device Gateway API enforces 10 calls / 30s per session (RateLimitingMiddleware), shared
    // across EVERY authenticated endpoint the client calls for that session (progress, status
    // poll, SAS refresh, ...) — not a separate budget per endpoint. A percent-progress callback
    // fires on every ~80 KB download chunk / every DISM console line, which for a multi-GB WIM is
    // easily hundreds of calls a second if sent unthrottled — that flooded the gateway with 429s
    // (visible as a wall of "Failed to report progress" warnings) and, since ReportProgressFunction
    // is also what updates the session's LastHeartbeatAt, made it look like heartbeat reporting
    // itself was broken. Only ever throttle percent-progress ("InProgress" + a percent value)
    // updates — step start/completion/failure reports are rare, one-off events and must always go
    // through immediately.
    //
    // Budget for the shared 10-calls/30s session bucket: 3 progress reports (this 10s interval)
    // + 1 SessionHeartbeatCoordinator check-in + at most ~3 step-transition reports in any single
    // 30s window ≈ 7, leaving deliberate headroom. Raising the cadence here eats into the
    // heartbeat's budget, so don't lower this interval without re-doing that arithmetic.
    //
    // IMPORTANT: do NOT special-case percent 0/100 as an always-send "boundary". A prior version
    // of this gate did exactly that (to guarantee the very first/last update always got through),
    // but integer-truncated percent legitimately stays at 0 for a long stretch at the start of a
    // large download (or briefly at 100 right at the end), so that "boundary" bypass fired on
    // every single tick during those stretches and defeated the whole throttle — the exact 429
    // flood this class exists to prevent. isNewStep alone already guarantees the first update for
    // a step gets through; nothing else may bypass the interval check.
    private static readonly TimeSpan MinProgressReportInterval = TimeSpan.FromSeconds(10);

    private readonly object _throttleGate = new();
    private ImagingStepName? _lastProgressStep;
    private DateTimeOffset _lastProgressReportUtc = DateTimeOffset.MinValue;

    public ImagingProgressReporter(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        ILogger<ImagingProgressReporter> logger,
        TimeProvider? timeProvider = null)
    {
        _gatewayClient = gatewayClient;
        _sessionId     = sessionId;
        _logger        = logger;
        _timeProvider  = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Reports a step status update to the Device Gateway API.</summary>
    public async Task ReportAsync(
        ImagingStepName stepName,
        ImagingStepStatus status,
        int? stepProgressPercent = null,
        string? errorDetail = null,
        CancellationToken ct = default)
    {
        if (status == ImagingStepStatus.InProgress
            && stepProgressPercent.HasValue
            && !ShouldSendProgressUpdate(stepName))
        {
            return;
        }

        try
        {
            await _gatewayClient.ReportProgressAsync(_sessionId, new
            {
                stepName            = stepName.ToString(),
                status              = status.ToString(),
                stepProgressPercent,
                errorDetail,
            }, ct);

            LogReported(_logger, stepName, status, stepProgressPercent ?? -1);
        }
        catch (Exception ex)
        {
            LogReportFailed(_logger, ex, stepName);
            // Do NOT rethrow — progress reporting is best-effort; imaging continues
        }
    }

    /// <summary>
    /// Decides whether a percent-progress update is worth spending one of the session's rate-
    /// limited calls on. Always lets through the first update for a new step; otherwise only
    /// sends once the minimum interval has passed since the last update actually sent (not merely
    /// the last one attempted). Deliberately does NOT special-case any particular percent value
    /// (see the remarks above <see cref="MinProgressReportInterval"/>). Locked because
    /// fire-and-forget callers (<c>_ = reporter.ReportAsync(...)</c>) don't await previous calls
    /// before issuing the next one.
    /// </summary>
    private bool ShouldSendProgressUpdate(ImagingStepName stepName)
    {
        lock (_throttleGate)
        {
            var now = _timeProvider.GetUtcNow();
            var isNewStep       = _lastProgressStep != stepName;
            var intervalPassed  = now - _lastProgressReportUtc >= MinProgressReportInterval;

            if (!isNewStep && !intervalPassed)
            {
                return false;
            }

            _lastProgressStep      = stepName;
            _lastProgressReportUtc = now;
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Progress reported: step={StepName} status={Status} subProgress={SubProgress}%.")]
    private static partial void LogReported(
        ILogger logger, ImagingStepName stepName, ImagingStepStatus status, int subProgress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report progress for step {StepName}.")]
    private static partial void LogReportFailed(ILogger logger, Exception ex, ImagingStepName stepName);
}

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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Device Gateway API enforces 10 calls / 30s per session (RateLimitingMiddleware). A
    // percent-progress callback fires on every ~80 KB download chunk / every DISM console line,
    // which for a multi-GB WIM is easily hundreds of calls a second if sent unthrottled — that
    // flooded the gateway with 429s (visible as a wall of "Failed to report progress" warnings)
    // and, since ReportProgressFunction is also what updates the session's LastHeartbeatAt, made
    // it look like heartbeat reporting itself was broken. Only ever throttle percent-progress
    // ("InProgress" + a percent value) updates — step start/completion/failure reports are rare,
    // one-off events and must always go through immediately. 4s keeps progress updates well under
    // the limit (max ~7-8 calls/30s from progress alone) even on a very fast/small download that
    // reaches 100% in well under a second.
    private static readonly TimeSpan MinProgressReportInterval = TimeSpan.FromSeconds(4);

    private ImagingStepName? _lastProgressStep;
    private DateTime _lastProgressReportUtc = DateTime.MinValue;

    public ImagingProgressReporter(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        ILogger<ImagingProgressReporter> logger)
    {
        _gatewayClient = gatewayClient;
        _sessionId     = sessionId;
        _logger        = logger;
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
            && stepProgressPercent is { } percent
            && !ShouldSendProgressUpdate(stepName, percent))
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
    /// limited calls on. Always lets through the first update for a new step and the 0%/100%
    /// boundary values; otherwise only sends once the minimum interval has passed since the last
    /// update actually sent (not merely the last one attempted).
    /// </summary>
    private bool ShouldSendProgressUpdate(ImagingStepName stepName, int percent)
    {
        var now = DateTime.UtcNow;
        var isNewStep      = _lastProgressStep != stepName;
        var isBoundary      = percent <= 0 || percent >= 100;
        var intervalPassed  = now - _lastProgressReportUtc >= MinProgressReportInterval;

        if (!isNewStep && !isBoundary && !intervalPassed)
        {
            return false;
        }

        _lastProgressStep      = stepName;
        _lastProgressReportUtc = now;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Progress reported: step={StepName} status={Status} subProgress={SubProgress}%.")]
    private static partial void LogReported(
        ILogger logger, ImagingStepName stepName, ImagingStepStatus status, int subProgress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report progress for step {StepName}.")]
    private static partial void LogReportFailed(ILogger logger, Exception ex, ImagingStepName stepName);
}

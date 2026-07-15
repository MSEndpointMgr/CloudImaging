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

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Progress reported: step={StepName} status={Status} subProgress={SubProgress}%.")]
    private static partial void LogReported(
        ILogger logger, ImagingStepName stepName, ImagingStepStatus status, int subProgress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report progress for step {StepName}.")]
    private static partial void LogReportFailed(ILogger logger, Exception ex, ImagingStepName stepName);
}

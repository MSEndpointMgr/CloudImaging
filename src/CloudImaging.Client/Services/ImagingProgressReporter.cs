using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CloudImaging.Client.Logging;
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

    // Device Gateway API enforces 30 calls / 30s per session (RateLimitingMiddleware; a gateway
    // from before 2026-11 allows 10), shared
    // across EVERY authenticated endpoint the client calls for that session (progress, status
    // poll, SAS refresh, ...): not a separate budget per endpoint. A percent-progress callback
    // fires on every ~80 KB download chunk / every DISM console line, which for a multi-GB WIM is
    // easily hundreds of calls a second if sent unthrottled: that flooded the gateway with 429s
    // (visible as a wall of "Failed to report progress" warnings) and, since ReportProgressFunction
    // is also what updates the session's LastHeartbeatAt, made it look like heartbeat reporting
    // itself was broken. Only ever throttle percent-progress ("InProgress" + a percent value)
    // updates: step start/completion/failure reports are rare, one-off events and must always go
    // through immediately.
    //
    // Budget for the shared 30-calls/30s session bucket: 3 progress reports (this 10s interval)
    // + 1 SessionHeartbeatCoordinator check-in + the step-transition reports in that window.
    // Raising the cadence here eats into the heartbeat's budget, so don't lower this interval
    // without re-doing that arithmetic. Steps that take a second or less (Prepare setup,
    // Configure boot) put several step reports into one window on their own, so a step's start,
    // end and failure reports are sent again after the gateway's Retry-After: a lost end report
    // leaves the session running in the portal while the device restarts into Windows.
    //
    // IMPORTANT: do NOT special-case percent 0/100 as an always-send "boundary". A prior version
    // of this gate did exactly that (to guarantee the very first/last update always got through),
    // but integer-truncated percent legitimately stays at 0 for a long stretch at the start of a
    // large download (or briefly at 100 right at the end), so that "boundary" bypass fired on
    // every single tick during those stretches and defeated the whole throttle: the exact 429
    // flood this class exists to prevent. isNewStep alone already guarantees the first update for
    // a step gets through; nothing else may bypass the interval check.
    private static readonly TimeSpan MinProgressReportInterval = TimeSpan.FromSeconds(10);

    private const int DeliveryAttempts = 3;

    private readonly object _throttleGate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private ImagingStepName? _lastProgressStep;
    private DateTimeOffset _lastProgressReportUtc = DateTimeOffset.MinValue;

    // Reports go out one at a time, in the order they were made. The pipeline does not wait for
    // percent updates, so without this one could reach the gateway after its step's end report and
    // show the finished step running again; the session would then never complete.
    private readonly object _sendGate = new();
    private readonly HashSet<ImagingStepName> _endedSteps = [];
    private Task _previousSend = Task.CompletedTask;

    /// <summary>Builds the reporter over the Device Gateway API client for the given session.</summary>
    public ImagingProgressReporter(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        ILogger<ImagingProgressReporter> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gatewayClient = gatewayClient;
        _sessionId = sessionId;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Reports a step status update to the Device Gateway API. A percent update is sent once; a
    /// step's start, end or failure is sent again when the gateway is rate limiting or unreachable.
    /// Reports are delivered in the order they are made, and a start or percent update made after
    /// the step's end report is not sent.
    /// </summary>
    public async Task ReportAsync(
        ImagingStepName stepName,
        ImagingStepStatus status,
        int? stepProgressPercent = null,
        string? errorDetail = null,
        CancellationToken ct = default)
    {
        var isPercentUpdate = status == ImagingStepStatus.InProgress && stepProgressPercent.HasValue;
        if (isPercentUpdate && !ShouldSendProgressUpdate(stepName))
        {
            return;
        }

        // The start report already tells the portal the step runs, so the first percent update waits for the interval.
        if (status == ImagingStepStatus.InProgress && !isPercentUpdate)
        {
            MarkProgressSent(stepName);
        }

        try
        {
            var sent = await SendInOrderAsync(stepName, status, new
            {
                stepName = stepName.ToString(),
                status = status.ToString(),
                stepProgressPercent,
                errorDetail,
            }, retry: !isPercentUpdate, ct);

            if (sent)
            {
                LogReported(_logger, stepName, status, stepProgressPercent ?? -1);
            }
        }
        catch (Exception ex)
        {
            LogReportFailed(_logger, LoggedFailure.StackTrace(ex), stepName, LoggedFailure.Reason(ex));
            // Do NOT rethrow: progress reporting is best-effort; imaging continues
        }
    }

    /// <summary>
    /// Reports the final status of a step together with its per-item results in one call, so a
    /// step with items costs no more of the session's rate limit than any other step end. Never
    /// throttled, and sent again when the gateway is rate limiting or unreachable; the items
    /// replace any the step reported before.
    /// </summary>
    public async Task ReportResultAsync(
        ImagingStepName stepName,
        ImagingStepStatus status,
        int warningCount,
        IReadOnlyList<CloudImaging.Contracts.Models.SetupProfiles.ImagingStepItem> items,
        string? errorDetail = null,
        string? statusNote = null,
        CancellationToken ct = default)
    {
        try
        {
            var sent = await SendInOrderAsync(stepName, status, new
            {
                stepName = stepName.ToString(),
                status = status.ToString(),
                stepProgressPercent = (int?)null,
                errorDetail,
                warningCount,
                items,
                statusNote,
            }, retry: true, ct);

            if (sent)
            {
                LogResultReported(_logger, stepName, status, warningCount, items.Count);
            }
        }
        catch (Exception ex)
        {
            LogReportFailed(_logger, LoggedFailure.StackTrace(ex), stepName, LoggedFailure.Reason(ex));
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
            var isNewStep = _lastProgressStep != stepName;
            var intervalPassed = now - _lastProgressReportUtc >= MinProgressReportInterval;

            if (!isNewStep && !intervalPassed)
            {
                return false;
            }

            _lastProgressStep = stepName;
            _lastProgressReportUtc = now;
            return true;
        }
    }

    private void MarkProgressSent(ImagingStepName stepName)
    {
        lock (_throttleGate)
        {
            _lastProgressStep = stepName;
            _lastProgressReportUtc = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Sends once every report made before this one has finished, delivered or not. False, without
    /// sending, for a start or percent update made after its step's end report.
    /// </summary>
    private async Task<bool> SendInOrderAsync(ImagingStepName stepName, ImagingStepStatus status, object payload, bool retry, CancellationToken ct)
    {
        var ends = status is ImagingStepStatus.Completed or ImagingStepStatus.Failed;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Task.CompletedTask;
        bool late;
        lock (_sendGate)
        {
            late = !ends && _endedSteps.Contains(stepName);
            if (!late)
            {
                if (ends)
                {
                    _endedSteps.Add(stepName);
                }

                previous = _previousSend;
                _previousSend = done.Task;
            }
        }

        if (late)
        {
            LogLateReportDropped(_logger, stepName, status);
            return false;
        }

        try
        {
            // Never faults: each send only ever completes its own marker with SetResult.
            await previous;
            await SendAsync(stepName, payload, retry, ct);
            return true;
        }
        finally
        {
            done.SetResult();
        }
    }

    private async Task SendAsync(ImagingStepName stepName, object payload, bool retry, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _gatewayClient.ReportProgressAsync(_sessionId, payload, ct);
                return;
            }
            catch (Exception ex) when (retry && attempt < DeliveryAttempts && DeviceGatewayApiClient.RetryDelay(ex, attempt) is { } wait)
            {
                LogReportRetry(_logger, stepName, attempt, wait.TotalSeconds, LoggedFailure.Reason(ex));
                await _delay(wait, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Progress reported: step={StepName} status={Status} subProgress={SubProgress}%.")]
    private static partial void LogReported(
        ILogger logger, ImagingStepName stepName, ImagingStepStatus status, int subProgress);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Step result reported: step={StepName} status={Status} warnings={WarningCount} items={ItemCount}.")]
    private static partial void LogResultReported(
        ILogger logger, ImagingStepName stepName, ImagingStepStatus status, int warningCount, int itemCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Report for step {StepName} not delivered on attempt {Attempt} ({Reason}); sending again in {Seconds} s.")]
    private static partial void LogReportRetry(ILogger logger, ImagingStepName stepName, int attempt, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Report for step {StepName} ({Status}) not sent: the step already reported its end.")]
    private static partial void LogLateReportDropped(ILogger logger, ImagingStepName stepName, ImagingStepStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report progress for step {StepName}: {Reason}")]
    private static partial void LogReportFailed(ILogger logger, Exception? ex, ImagingStepName stepName, string reason);
}

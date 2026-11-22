using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/{sessionId}/progress: Accepts progress reports from Device Gateway API (T048, FR-007).
///
/// Request body:
/// {
///   "stepName": "FormatDisk|DownloadImage|ApplyImage|ConfigureBoot|ApplyRecoveryImage",
///   "status": "Pending|InProgress|Completed|Failed",
///   "stepProgressPercent": 0-100,   // optional; relevant for InProgress
///   "errorDetail": "..."            // optional; populated when status=Failed
/// }
///
/// On success, recalculates overallProgressPercent, updates currentStep,
/// and advances session state if all steps complete.
///
/// A start or percent report for a step that has already ended is ignored with 204: the device
/// does not wait for its percent reports, so one can arrive after the step's end report. Steps are
/// written only while their row is unchanged since it was read, so a report that raced with
/// another for the same step is judged again against the one stored first.
/// </summary>
public sealed partial class ReportProgressFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Only the device's own reports write its steps, so a few attempts settle any race; 503 makes it send again.
    private const int MaxWriteAttempts = 5;

    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ImagingStepRepository _stepRepo;
    private readonly ImagingStepItemRepository _itemRepo;
    private readonly SessionHistoryRepository _historyRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<ReportProgressFunction> _logger;

    /// <param name="sessionRepo">Device session repository.</param>
    /// <param name="stepRepo">Imaging step repository.</param>
    /// <param name="itemRepo">Per-item step outcomes.</param>
    /// <param name="historyRepo">Session history repository.</param>
    /// <param name="configRepo">Portal configuration repository.</param>
    /// <param name="logger">Logger for this function.</param>
    public ReportProgressFunction(
        DeviceSessionRepository sessionRepo,
        ImagingStepRepository stepRepo,
        ImagingStepItemRepository itemRepo,
        SessionHistoryRepository historyRepo,
        PortalConfigurationRepository configRepo,
        ILogger<ReportProgressFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _stepRepo = stepRepo;
        _itemRepo = itemRepo;
        _historyRepo = historyRepo;
        _configRepo = configRepo;
        _logger = logger;
    }

    /// <summary>POST internal/sessions/{sessionId}/progress. Records an imaging step progress/status update from the device.</summary>
    [Function(nameof(ReportProgressFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/progress")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        ProgressPayload? payload;
        try
        {
            payload = await DeserializePayloadAsync(req.Body, context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Items only travel on a step's final report; an older Client sends none.
        var isFinal = payload.Status is ImagingStepStatus.Completed or ImagingStepStatus.Failed;
        var (items, truncated) = isFinal && payload.Items is { } reported ? NormalizeItems(reported) : ([], false);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
            if (session is null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            // Only a session the operator has actually started may report steps. Without this the
            // reducer accepts reports in any state, so a device holding a valid token could drive an
            // expired or already-terminal session back to Completed or Failed.
            if (session.State is not (SessionState.SessionStarted or SessionState.SessionInProgress))
            {
                LogProgressRejected(_logger, sessionGuid, session.State);
                return req.CreateResponse(HttpStatusCode.Conflict);
            }

            var stored = await _stepRepo.GetStoredBySessionAsync(sessionGuid, context.CancellationToken);
            var existing = stored.FirstOrDefault(s => s.Step.StepName == payload.StepName);
            var existingStep = existing?.Step;

            // Storing a late start or percent report would show the ended step running again, and the
            // session could never complete: it stays in progress until the inactivity sweep fails it.
            if (!isFinal && existingStep is { Status: ImagingStepStatus.Completed or ImagingStepStatus.Failed } ended)
            {
                LogLateReportIgnored(_logger, sessionGuid, payload.StepName, payload.Status, ended.Status);
                return req.CreateResponse(HttpStatusCode.NoContent);
            }

            // Preserve the initial start timestamp when a later completion/failure report replaces
            // the same Table Storage row. Without this, completed steps lose their elapsed-time data.
            var now = DateTimeOffset.UtcNow;
            var step = new ImagingStep
            {
                StepName = payload.StepName,
                Status = payload.Status,
                StepProgressPercent = payload.StepProgressPercent,
                ErrorDetail = payload.ErrorDetail,
                StartedAt = existingStep?.StartedAt
                            ?? (payload.Status == ImagingStepStatus.InProgress ? now : null),
                CompletedAt = isFinal ? now : null,
                WarningCount = isFinal ? payload.WarningCount : existingStep?.WarningCount,
                ItemCounts = isFinal && payload.Items is not null ? CountItems(items) : existingStep?.ItemCounts ?? [],
                ItemsTruncated = isFinal ? truncated : existingStep?.ItemsTruncated ?? false,
                StatusNote = Cap(payload.StatusNote, ImagingStep.MaxStatusNoteLength) ?? existingStep?.StatusNote,
            };

            if (isFinal && payload.Items is not null)
            {
                await _itemRepo.ReplaceAsync(sessionGuid, payload.StepName, items, context.CancellationToken);
            }

            // Another report for this step was stored after the read above; judge this one again against it.
            if (!await _stepRepo.TryWriteAsync(sessionGuid, step, existing?.ETag, context.CancellationToken))
            {
                continue;
            }

            await UpdateSessionAsync(session, [.. stored.Select(s => s.Step).Where(s => s.StepName != step.StepName), step], step, context.CancellationToken);
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        LogProgressContended(_logger, sessionGuid, payload.StepName, MaxWriteAttempts);
        return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
    }

    // Recalculates the session summary from its steps, and records the session history once it ends.
    private async Task UpdateSessionAsync(DeviceSession session, IReadOnlyList<ImagingStep> allSteps, ImagingStep reported, CancellationToken ct)
    {
        var overallPercent = OverallProgressCalculator.Calculate(allSteps);
        var activeStep = OverallProgressCalculator.CurrentOrLastStepName(allSteps);

        // Determine new session state
        bool anyFailed = allSteps.Any(s => s.Status == ImagingStepStatus.Failed);
        bool allCompleted = OverallProgressCalculator.IsPipelineComplete(allSteps);

        // A step that has started but not yet reported any sub-progress still means the device is
        // imaging (the first FormatDisk report is InProgress at 0%, which leaves overallPercent at
        // 0), so don't gate the transition on the percentage alone.
        bool anyStarted = allSteps.Any(s => s.Status is ImagingStepStatus.InProgress or ImagingStepStatus.Completed);

        var newState = anyFailed ? SessionState.SessionFailed
                     : allCompleted ? SessionState.SessionCompleted
                     : anyStarted ? SessionState.SessionInProgress
                     : session.State;

        var updated = session with
        {
            State = newState,
            OverallProgressPercent = overallPercent,
            CurrentStep = activeStep,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            TerminalAt = anyFailed || allCompleted ? DateTimeOffset.UtcNow : null,
        };

        await _sessionRepo.UpdateAsync(updated, ct);
        LogProgressReceived(_logger, session.SessionId, reported.StepName, reported.Status, overallPercent);

        if (anyFailed || allCompleted)
        {
            var failedStep = allSteps.FirstOrDefault(s => s.Status == ImagingStepStatus.Failed);
            var config = await _configRepo.GetAsync(ct);
            var history = new SessionHistoryRecord
            {
                SessionId = updated.SessionId,
                FinalState = updated.State,
                DeviceSerialNumber = updated.DeviceSerialNumber,
                DeviceManufacturer = updated.DeviceManufacturer,
                DeviceModel = updated.DeviceModel,
                LocationId = updated.LocationId,
                LocationName = updated.LocationName,
                PreFlightAuthorizationResult = updated.PreFlightAuthorizationResult,
                PreFlightChecks = updated.PreFlightChecks,
                Architecture = updated.Architecture,
                AssignedOsImageId = updated.AssignedOsImageId,
                FailedStepName = failedStep?.StepName,
                ErrorDetail = failedStep?.ErrorDetail,
                CreatedAt = updated.CreatedAt,
                TerminalAt = updated.TerminalAt ?? DateTimeOffset.UtcNow,
            };
            await _historyRepo.CreateAsync(history, config.SessionHistoryRetentionDays, ct);
        }
    }

    internal static ValueTask<ProgressPayload?> DeserializePayloadAsync(
        Stream body,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync<ProgressPayload>(body, JsonOptions, cancellationToken);

    internal sealed class ProgressPayload
    {
        public required ImagingStepName StepName { get; init; }
        public required ImagingStepStatus Status { get; init; }
        public int? StepProgressPercent { get; init; }
        public string? ErrorDetail { get; init; }
        public int? WarningCount { get; init; }
        public IReadOnlyList<ImagingStepItem>? Items { get; init; }
        public string? StatusNote { get; init; }
    }

    /// <summary>
    /// Truncates text fields and the list to their limits instead of rejecting the report, so a
    /// long script output line never costs the device its final step report.
    /// </summary>
    internal static (List<ImagingStepItem> Items, bool Truncated) NormalizeItems(IReadOnlyList<ImagingStepItem> items)
    {
        var kept = items.Take(ImagingStepItem.MaxItemsPerReport).Select(item => item with
        {
            Name = Cap(item.Name, ImagingStepItem.MaxNameLength) ?? string.Empty,
            Target = Cap(item.Target, ImagingStepItem.MaxTargetLength),
            Reason = Cap(item.Reason, ImagingStepItem.MaxReasonLength),
            LastOutputLine = Cap(item.LastOutputLine, ImagingStepItem.MaxLastOutputLineLength),
            ComponentVersion = Cap(item.ComponentVersion, ImagingStepItem.MaxNameLength),
        }).ToList();
        return (kept, items.Count > ImagingStepItem.MaxItemsPerReport);
    }

    /// <summary>Counts items by kind and outcome.</summary>
    internal static List<ImagingStepItemCount> CountItems(IEnumerable<ImagingStepItem> items) =>
        [.. items
            .GroupBy(item => (item.Kind, item.Outcome))
            .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.Outcome)
            .Select(group => new ImagingStepItemCount { Kind = group.Key.Kind, Outcome = group.Key.Outcome, Count = group.Count(), FromUsbCache = group.Count(item => item.FromUsbCache == true) is > 0 and var usb ? usb : null })];

    private static string? Cap(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Progress: session={SessionId} step={StepName} status={Status} overall={Overall}%.")]
    private static partial void LogProgressReceived(
        ILogger logger, Guid sessionId, ImagingStepName stepName, ImagingStepStatus status, int overall);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Progress rejected: session={SessionId} is in {State}, which cannot report steps.")]
    private static partial void LogProgressRejected(ILogger logger, Guid sessionId, SessionState state);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Progress ignored: session={SessionId} step={StepName} reported {Status} after the step ended {Ended}.")]
    private static partial void LogLateReportIgnored(ILogger logger, Guid sessionId, ImagingStepName stepName, ImagingStepStatus status, ImagingStepStatus ended);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Progress not stored: session={SessionId} step={StepName} changed during each of {Attempts} attempts to save it.")]
    private static partial void LogProgressContended(ILogger logger, Guid sessionId, ImagingStepName stepName, int attempts);
}

using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/{sessionId}/progress — Accepts progress reports from Device Gateway API (T048, FR-007).
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
/// </summary>
public sealed partial class ReportProgressFunction
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ImagingStepRepository _stepRepo;
    private readonly SessionHistoryRepository _historyRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<ReportProgressFunction> _logger;

    public ReportProgressFunction(
        DeviceSessionRepository sessionRepo,
        ImagingStepRepository stepRepo,
        SessionHistoryRepository historyRepo,
        PortalConfigurationRepository configRepo,
        ILogger<ReportProgressFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _stepRepo = stepRepo;
        _historyRepo = historyRepo;
        _configRepo = configRepo;
        _logger = logger;
    }

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
            payload = await JsonSerializer.DeserializeAsync<ProgressPayload>(
                req.Body, cancellationToken: context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
        if (session is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var allSteps = (await _stepRepo.GetBySessionAsync(sessionGuid, context.CancellationToken)).ToList();
        var existingStep = allSteps.FirstOrDefault(existing => existing.StepName == payload.StepName);
        var now = DateTimeOffset.UtcNow;

        // Preserve the initial start timestamp when a later completion/failure report replaces
        // the same Table Storage row. Without this, completed steps lose their elapsed-time data.
        var step = new ImagingStep
        {
            StepName = payload.StepName,
            Status = payload.Status,
            StepProgressPercent = payload.StepProgressPercent,
            ErrorDetail = payload.ErrorDetail,
            StartedAt = existingStep?.StartedAt
                        ?? (payload.Status == ImagingStepStatus.InProgress ? now : null),
            CompletedAt = payload.Status is ImagingStepStatus.Completed or ImagingStepStatus.Failed
                                  ? now : null,
        };

        await _stepRepo.UpsertAsync(sessionGuid, step, context.CancellationToken);

        if (existingStep is not null)
        {
            allSteps.Remove(existingStep);
        }
        allSteps.Add(step);

        // Recalculate session summary from the updated step collection.
        var overallPercent = OverallProgressCalculator.Calculate(allSteps);
        var activeStep = OverallProgressCalculator.CurrentOrLastStepName(allSteps);

        // Determine new session state
        bool anyFailed = allSteps.Any(s => s.Status == ImagingStepStatus.Failed);
        bool allCompleted = allSteps.Count == OverallProgressCalculator.PipelineStepCount
                            && allSteps.All(s => s.Status == ImagingStepStatus.Completed);

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

        await _sessionRepo.UpdateAsync(updated, context.CancellationToken);
        LogProgressReceived(_logger, sessionGuid, payload.StepName, payload.Status, overallPercent);

        if (anyFailed || allCompleted)
        {
            var failedStep = allSteps.FirstOrDefault(s => s.Status == ImagingStepStatus.Failed);
            var config = await _configRepo.GetAsync(context.CancellationToken);
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
                AssignedOsImageId = updated.AssignedOsImageId,
                FailedStepName = failedStep?.StepName,
                ErrorDetail = failedStep?.ErrorDetail,
                CreatedAt = updated.CreatedAt,
                TerminalAt = updated.TerminalAt ?? DateTimeOffset.UtcNow,
            };
            await _historyRepo.CreateAsync(history, config.SessionHistoryRetentionDays, context.CancellationToken);
        }

        var response = req.CreateResponse(HttpStatusCode.NoContent);
        return response;
    }

    private sealed class ProgressPayload
    {
        public ImagingStepName StepName { get; init; }
        public ImagingStepStatus Status { get; init; }
        public int? StepProgressPercent { get; init; }
        public string? ErrorDetail { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Progress: session={SessionId} step={StepName} status={Status} overall={Overall}%.")]
    private static partial void LogProgressReceived(
        ILogger logger, Guid sessionId, ImagingStepName stepName, ImagingStepStatus status, int overall);
}

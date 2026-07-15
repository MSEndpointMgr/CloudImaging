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
///   "stepName": "FormatDisk|DownloadImage|ApplyImage",
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
    private readonly ImagingStepRepository  _stepRepo;
    private readonly ILogger<ReportProgressFunction> _logger;

    public ReportProgressFunction(
        DeviceSessionRepository sessionRepo,
        ImagingStepRepository stepRepo,
        ILogger<ReportProgressFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _stepRepo    = stepRepo;
        _logger      = logger;
    }

    [Function(nameof(ReportProgressFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/progress")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        ProgressPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ProgressPayload>(
                req.Body, cancellationToken: context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null) return req.CreateResponse(HttpStatusCode.BadRequest);

        var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
        if (session is null) return req.CreateResponse(HttpStatusCode.NotFound);

        // Persist the step record
        var step = new ImagingStep
        {
            StepName            = payload.StepName,
            Status              = payload.Status,
            StepProgressPercent = payload.StepProgressPercent,
            ErrorDetail         = payload.ErrorDetail,
            StartedAt           = payload.Status == ImagingStepStatus.InProgress ? DateTimeOffset.UtcNow : null,
            CompletedAt         = payload.Status is ImagingStepStatus.Completed or ImagingStepStatus.Failed
                                  ? DateTimeOffset.UtcNow : null,
        };

        await _stepRepo.UpsertAsync(sessionGuid, step, context.CancellationToken);

        // Reload all steps to recalculate overall progress
        var allSteps = await _stepRepo.GetBySessionAsync(sessionGuid, context.CancellationToken);
        var overallPercent = OverallProgressCalculator.Calculate(allSteps);
        var activeStep     = OverallProgressCalculator.ActiveStepName(allSteps);

        // Determine new session state
        bool anyFailed    = allSteps.Any(s => s.Status == ImagingStepStatus.Failed);
        bool allCompleted = allSteps.Count == 3 && allSteps.All(s => s.Status == ImagingStepStatus.Completed);

        var newState = anyFailed ? SessionState.SessionFailed
                     : allCompleted ? SessionState.SessionCompleted
                     : overallPercent > 0 ? SessionState.SessionInProgress
                     : session.State;

        var updated = new DeviceSession
        {
            SessionId                    = session.SessionId,
            State                        = newState,
            DeviceSerialNumber           = session.DeviceSerialNumber,
            DeviceManufacturer           = session.DeviceManufacturer,
            DeviceModel                  = session.DeviceModel,
            HardwareMetadata             = session.HardwareMetadata,
            PreFlightAuthorizationResult = session.PreFlightAuthorizationResult,
            Passcode                     = session.Passcode,
            PasscodeExpiresAt            = session.PasscodeExpiresAt,
            PasscodeConsumed             = session.PasscodeConsumed,
            DeviceSessionToken           = session.DeviceSessionToken,
            DeviceSessionTokenExpiresAt  = session.DeviceSessionTokenExpiresAt,
            AssignedOsImageId            = session.AssignedOsImageId,
            SasTokenUrl                  = session.SasTokenUrl,
            SasTokenUrlExpiresAt         = session.SasTokenUrlExpiresAt,
            OverallProgressPercent       = overallPercent,
            CurrentStep                  = activeStep,
            CreatedAt                    = session.CreatedAt,
            LastHeartbeatAt              = DateTimeOffset.UtcNow,
            TerminalAt                   = anyFailed || allCompleted ? DateTimeOffset.UtcNow : null,
        };

        await _sessionRepo.UpdateAsync(updated, context.CancellationToken);
        LogProgressReceived(_logger, sessionGuid, payload.StepName, payload.Status, overallPercent);

        var response = req.CreateResponse(HttpStatusCode.NoContent);
        return response;
    }

    private sealed class ProgressPayload
    {
        public ImagingStepName   StepName            { get; init; }
        public ImagingStepStatus Status              { get; init; }
        public int?              StepProgressPercent { get; init; }
        public string?           ErrorDetail         { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Progress: session={SessionId} step={StepName} status={Status} overall={Overall}%.")]
    private static partial void LogProgressReceived(
        ILogger logger, Guid sessionId, ImagingStepName stepName, ImagingStepStatus status, int overall);
}

using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Domain;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions — Internal endpoint called by Device Gateway API over Private Link.
/// Creates a device imaging session with one-time passcode and runs device pre-flight authorization (T030).
///
/// Response shape:
/// {
///   "sessionId": "...",
///   "passcode": "ABCDEF",           // plain passcode — returned ONCE at session init only
///   "deviceSessionToken": "...",    // opaque bearer token for subsequent Device Gateway calls
///   "state": "SessionInit",
///   "preFlightResult": "Skipped|MatchedAutopilotV1|MatchedCorporateIdentifier|NotAuthorized"
/// }
/// </summary>
public sealed partial class CreateSessionFunction
{
    private static readonly JsonSerializerOptions SchemeJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DeviceSessionRepository _sessionRepo;
    private readonly DevicePreFlightAuthorizationService _preFlight;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly PartitioningSchemeRepository _partitioningSchemeRepo;
    private readonly SessionHistoryRepository _historyRepo;
    private readonly PreFlightOverrideRepository _overrideRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<CreateSessionFunction> _logger;

    public CreateSessionFunction(
        DeviceSessionRepository sessionRepo,
        DevicePreFlightAuthorizationService preFlight,
        PortalConfigurationRepository configRepo,
        PartitioningSchemeRepository partitioningSchemeRepo,
        SessionHistoryRepository historyRepo,
        PreFlightOverrideRepository overrideRepo,
        IConfiguration config,
        ILogger<CreateSessionFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _preFlight = preFlight;
        _configRepo = configRepo;
        _partitioningSchemeRepo = partitioningSchemeRepo;
        _historyRepo = historyRepo;
        _overrideRepo = overrideRepo;
        _config = config;
        _logger = logger;
    }

    [Function(nameof(CreateSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions")] HttpRequestData req,
        FunctionContext context)
    {
        DeviceRegistrationPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<DeviceRegistrationPayload>(
                req.Body,
                SchemeJsonOptions,
                context.CancellationToken);
        }
        catch (JsonException ex)
        {
            LogInvalidPayload(_logger, ex);
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync($"Invalid registration payload: {ex.Message}", context.CancellationToken);
            return bad;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.SerialNumber))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("SerialNumber is required.", context.CancellationToken);
            return bad;
        }

        // Read configuration. Azure Function App settings are surfaced as OS environment
        // variables, which the environment-variables configuration provider re-keys from
        // "Security__PasscodeTtlMinutes" to "Security:PasscodeTtlMinutes" — so the colon form
        // must be queried first (falling back to the literal double-underscore form for
        // local.settings.json, which is not re-keyed the same way).
        int passcodeTtlMinutes = _config.GetValue<int?>("Security:PasscodeTtlMinutes")
            ?? _config.GetValue<int?>("Security__PasscodeTtlMinutes")
            ?? 10;
        int sessionInactivityMinutes = _config.GetValue<int?>("Security:SessionInactivityMinutes")
            ?? _config.GetValue<int?>("Security__SessionInactivityMinutes")
            ?? 30;

        var passcodeTtl = TimeSpan.FromMinutes(passcodeTtlMinutes);
        var sessionInactivityTimeout = TimeSpan.FromMinutes(sessionInactivityMinutes);

        // Create session with passcode
        var (session, plainPasscode) = DeviceSessionFactory.CreateNew(
            payload, passcodeTtl, sessionInactivityTimeout);

        // Run device pre-flight checks. Evaluated once here and stored; later policy changes never touch this session.
        var portalConfig = await _configRepo.GetAsync(context.CancellationToken);
        var preFlightResult = await _preFlight.EvaluateAsync(payload, portalConfig, context.CancellationToken);
        var preFlightChecks = PreFlightCheckEvaluator.Evaluate(portalConfig, payload.SecurityPosture, preFlightResult);
        if (PreFlightCheckEvaluator.FailedChecks(preFlightChecks).Count > 0
            && await TryUseOverrideAsync(payload.SerialNumber, preFlightChecks, context.CancellationToken) is { } approvedChecks)
        {
            preFlightChecks = approvedChecks;
        }

        var failedChecks = PreFlightCheckEvaluator.FailedChecks(preFlightChecks);

        // Determine target state
        var targetState = failedChecks.Count > 0
            ? SessionState.SessionNotAuthorized
            : SessionState.SessionAllowed;

        // Snapshot the partitioning scheme currently in effect onto the session (locked at
        // creation time — later admin edits to the global scheme do not affect this session).
        var partitioningScheme = await _partitioningSchemeRepo.GetAsync(context.CancellationToken);
        var partitioningSchemeSnapshotJson = JsonSerializer.Serialize(partitioningScheme, SchemeJsonOptions);
        DateTimeOffset? terminalAt = targetState == SessionState.SessionNotAuthorized
            ? DateTimeOffset.UtcNow
            : null;

        // Issue device-session token if not NotAuthorized
        // (The actual token service is in DeviceGatewayApi — ImagingCoreApi returns the raw session,
        //  and DeviceGatewayApi issues the bearer token before returning to the device)
        var finalSession = session with
        {
            State = targetState,
            PreFlightAuthorizationResult = preFlightResult,
            PreFlightChecks = preFlightChecks,
            PasscodeConsumed = false,
            OverallProgressPercent = 0,
            PartitioningSchemeSnapshotJson = partitioningSchemeSnapshotJson,
            TerminalAt = terminalAt,
            PurgeAt = terminalAt + DeviceSessionLifecycleService.TerminalPurgeTtl,
        };

        await _sessionRepo.CreateAsync(finalSession, context.CancellationToken);
        LogSessionCreated(_logger, finalSession.SessionId, targetState, preFlightResult);
        if (failedChecks.Count > 0)
        {
            LogSessionBlocked(_logger, finalSession.SessionId, finalSession.DeviceSerialNumber, string.Join(", ", failedChecks));
        }

        if (targetState == SessionState.SessionNotAuthorized)
        {
            var history = new SessionHistoryRecord
            {
                SessionId = finalSession.SessionId,
                FinalState = finalSession.State,
                DeviceSerialNumber = finalSession.DeviceSerialNumber,
                DeviceManufacturer = finalSession.DeviceManufacturer,
                DeviceModel = finalSession.DeviceModel,
                LocationId = finalSession.LocationId,
                LocationName = finalSession.LocationName,
                PreFlightAuthorizationResult = finalSession.PreFlightAuthorizationResult,
                PreFlightChecks = finalSession.PreFlightChecks,
                Architecture = finalSession.Architecture,
                AssignedOsImageId = finalSession.AssignedOsImageId,
                CreatedAt = finalSession.CreatedAt,
                TerminalAt = finalSession.TerminalAt ?? DateTimeOffset.UtcNow,
            };
            await _historyRepo.CreateAsync(history, portalConfig.SessionHistoryRetentionDays, context.CancellationToken);
        }

        // Return session info including the PLAIN passcode (only returned at creation)
        var responseBody = new
        {
            sessionId = finalSession.SessionId,
            passcode = plainPasscode,
            state = finalSession.State.ToString(),
            preFlightResult = finalSession.PreFlightAuthorizationResult.ToString(),
        };

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(responseBody), context.CancellationToken);
        return response;
    }

    /// <summary>
    /// Uses the device's administrator override when it covers every failed check. Returns the checks
    /// with those failures marked approved, or null when there is no usable override.
    /// </summary>
    private async Task<IReadOnlyList<PreFlightCheckResult>?> TryUseOverrideAsync(
        string serialNumber, IReadOnlyList<PreFlightCheckResult> checks, CancellationToken ct)
    {
        if (await _overrideRepo.GetAsync(serialNumber, ct) is not { } stored)
        {
            return null;
        }

        var approved = PreFlightCheckEvaluator.ApplyOverride(checks, stored.Override, DateTimeOffset.UtcNow);
        if (approved is null)
        {
            return null;
        }

        // ETag-guarded delete: a concurrent session from the same serial cannot use the same override.
        if (!await _overrideRepo.TryConsumeAsync(serialNumber, stored.ETag, ct))
        {
            return null;
        }

        LogOverrideUsed(_logger, serialNumber, stored.Override.ApprovedBy, stored.Override.SourceSessionId);
        return approved;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid device registration payload.")]
    private static partial void LogInvalidPayload(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} for device {SerialNumber} blocked by pre-flight checks: {FailedChecks}.")]
    private static partial void LogSessionBlocked(ILogger logger, Guid sessionId, string serialNumber, string failedChecks);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Device {SerialNumber} used the pre-flight override approved by {ApprovedBy} from session {SourceSessionId}.")]
    private static partial void LogOverrideUsed(ILogger logger, string serialNumber, string approvedBy, Guid sourceSessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created with state {State} (pre-flight: {PreFlightResult}).")]
    private static partial void LogSessionCreated(
        ILogger logger, Guid sessionId, SessionState state, PreFlightAuthorizationResult preFlightResult);
}

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
    private readonly IConfiguration _config;
    private readonly ILogger<CreateSessionFunction> _logger;

    public CreateSessionFunction(
        DeviceSessionRepository sessionRepo,
        DevicePreFlightAuthorizationService preFlight,
        PortalConfigurationRepository configRepo,
        PartitioningSchemeRepository partitioningSchemeRepo,
        SessionHistoryRepository historyRepo,
        IConfiguration config,
        ILogger<CreateSessionFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _preFlight = preFlight;
        _configRepo = configRepo;
        _partitioningSchemeRepo = partitioningSchemeRepo;
        _historyRepo = historyRepo;
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

        // Run device pre-flight authorization
        var preFlightResult = await _preFlight.EvaluateAsync(payload, context.CancellationToken);

        // Determine target state
        var targetState = preFlightResult == PreFlightAuthorizationResult.NotAuthorized
            ? SessionState.SessionNotAuthorized
            : SessionState.SessionAllowed;

        // Snapshot the partitioning scheme currently in effect onto the session (locked at
        // creation time — later admin edits to the global scheme do not affect this session).
        var partitioningScheme = await _partitioningSchemeRepo.GetAsync(context.CancellationToken);
        var partitioningSchemeSnapshotJson = JsonSerializer.Serialize(partitioningScheme, SchemeJsonOptions);

        // Issue device-session token if not NotAuthorized
        // (The actual token service is in DeviceGatewayApi — ImagingCoreApi returns the raw session,
        //  and DeviceGatewayApi issues the bearer token before returning to the device)
        var finalSession = new DeviceSession
        {
            SessionId = session.SessionId,
            State = targetState,
            DeviceSerialNumber = session.DeviceSerialNumber,
            DeviceManufacturer = session.DeviceManufacturer,
            DeviceModel = session.DeviceModel,
            HardwareMetadata = session.HardwareMetadata,
            LocationId = session.LocationId,
            LocationName = session.LocationName,
            PreFlightAuthorizationResult = preFlightResult,
            Passcode = session.Passcode,
            PasscodeExpiresAt = session.PasscodeExpiresAt,
            PasscodeConsumed = false,
            OverallProgressPercent = 0,
            CreatedAt = session.CreatedAt,
            LastHeartbeatAt = session.LastHeartbeatAt,
            PartitioningSchemeSnapshotJson = partitioningSchemeSnapshotJson,
        };

        await _sessionRepo.CreateAsync(finalSession, context.CancellationToken);
        LogSessionCreated(_logger, finalSession.SessionId, targetState, preFlightResult);

        if (targetState == SessionState.SessionNotAuthorized)
        {
            var portalConfig = await _configRepo.GetAsync(context.CancellationToken);
            var history = new SessionHistoryRecord
            {
                SessionId = finalSession.SessionId,
                FinalState = finalSession.State,
                DeviceSerialNumber = finalSession.DeviceSerialNumber,
                DeviceManufacturer = finalSession.DeviceManufacturer,
                DeviceModel = finalSession.DeviceModel,
                PreFlightAuthorizationResult = finalSession.PreFlightAuthorizationResult,
                AssignedOsImageId = finalSession.AssignedOsImageId,
                CreatedAt = finalSession.CreatedAt,
                TerminalAt = DateTimeOffset.UtcNow,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid device registration payload.")]
    private static partial void LogInvalidPayload(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created with state {State} (pre-flight: {PreFlightResult}).")]
    private static partial void LogSessionCreated(
        ILogger logger, Guid sessionId, SessionState state, PreFlightAuthorizationResult preFlightResult);
}

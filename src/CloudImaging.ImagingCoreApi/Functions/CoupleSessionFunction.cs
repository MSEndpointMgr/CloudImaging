using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Domain;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/couple — Couples a session by verifying the one-time passcode (T039a, FR-021).
///
/// Rules:
///   1. Passcode must match the stored hash (constant-time comparison).
///   2. Passcode must not be expired.
///   3. Passcode must not be already consumed (prevents replay).
///   4. Session must be in <see cref="SessionState.SessionAllowed"/> state.
///   5. On success, mark passcode consumed and transition to <see cref="SessionState.SessionAssigned"/>.
///
/// Response codes:
///   201 Created — coupling succeeded; returns updated session.
///   404 Not Found — unknown passcode or session not in allowed state.
///   409 Conflict — passcode already consumed.
/// </summary>
public sealed partial class CoupleSessionFunction
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ILogger<CoupleSessionFunction> _logger;

    public CoupleSessionFunction(
        DeviceSessionRepository sessionRepo,
        ILogger<CoupleSessionFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    [Function(nameof(CoupleSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/couple")] HttpRequestData req,
        FunctionContext context)
    {
        // Deserialize request body: { "passcode": "ABCDEF" }
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("passcode", out var passcodeProp)
            || string.IsNullOrWhiteSpace(passcodeProp.GetString()))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("passcode field is required.", context.CancellationToken);
            return bad;
        }

        var submittedPasscode = passcodeProp.GetString()!;
        var passcodeHash = PasscodeSecurityPolicy.HashPasscode(submittedPasscode);

        var session = await _sessionRepo.FindByPasscodeHashAsync(passcodeHash, context.CancellationToken);

        // 404 if no session found or session not in SessionAllowed state
        if (session is null || session.State != SessionState.SessionAllowed)
        {
            LogPasscodeNotFound(_logger, submittedPasscode[..2] + "****");
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        // 409 if already consumed
        if (session.PasscodeConsumed)
        {
            LogPasscodeConsumed(_logger, session.SessionId);
            return req.CreateResponse(HttpStatusCode.Conflict);
        }

        // Check expiry
        if (session.PasscodeExpiresAt.HasValue && DateTimeOffset.UtcNow > session.PasscodeExpiresAt.Value)
        {
            LogPasscodeExpired(_logger, session.SessionId);
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        // Transition session: SessionAllowed → SessionAssigned, mark passcode consumed
        var coupled = new DeviceSession
        {
            SessionId = session.SessionId,
            State = SessionState.SessionAssigned,
            DeviceSerialNumber = session.DeviceSerialNumber,
            DeviceManufacturer = session.DeviceManufacturer,
            DeviceModel = session.DeviceModel,
            HardwareMetadata = session.HardwareMetadata,
            PreFlightAuthorizationResult = session.PreFlightAuthorizationResult,
            Passcode = session.Passcode,
            PasscodeExpiresAt = session.PasscodeExpiresAt,
            PasscodeConsumed = true,             // Invalidate passcode on success
            DeviceSessionToken = session.DeviceSessionToken,
            DeviceSessionTokenExpiresAt = session.DeviceSessionTokenExpiresAt,
            OverallProgressPercent = session.OverallProgressPercent,
            CreatedAt = session.CreatedAt,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
        };

        await _sessionRepo.UpdateAsync(coupled, context.CancellationToken);
        LogSessionCoupled(_logger, coupled.SessionId);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            sessionId = coupled.SessionId,
            state = coupled.State.ToString(),
        }), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passcode not found or session not in allowed state (prefix={Prefix}).")]
    private static partial void LogPasscodeNotFound(ILogger logger, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passcode already consumed for session {SessionId}.")]
    private static partial void LogPasscodeConsumed(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Passcode expired for session {SessionId}.")]
    private static partial void LogPasscodeExpired(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} coupled — state=SessionAssigned.")]
    private static partial void LogSessionCoupled(ILogger logger, Guid sessionId);
}

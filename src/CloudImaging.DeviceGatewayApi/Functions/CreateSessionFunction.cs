using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions — Public device-facing session bootstrap endpoint (T031, FR-001, FR-010).
/// Exempt from mTLS validation (<see cref="MtlsCertificateValidationMiddleware.ExemptFunction"/>)
/// and from device-session token validation.
///
/// Accepts a <see cref="DeviceRegistrationPayload"/>, forwards it to ImagingCoreApi over
/// Private Link, issues a device-session token, and returns the opaque token + one-time passcode
/// to the Cloud Imaging Client.
///
/// Response shape (on success):
/// {
///   "sessionId": "...",
///   "deviceSessionToken": "Bearer ...",
///   "passcode": "ABCDEF",
///   "state": "SessionAllowed|SessionNotAuthorized"
/// }
/// </summary>
public sealed partial class CreateSessionFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly DeviceSessionTokenService _tokenService;
    private readonly ILogger<CreateSessionFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CreateSessionFunction(
        ImagingCoreClient coreClient,
        DeviceSessionTokenService tokenService,
        ILogger<CreateSessionFunction> logger)
    {
        _coreClient   = coreClient;
        _tokenService = tokenService;
        _logger       = logger;
    }

    [Function("CreateSession")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions")] HttpRequestData req,
        FunctionContext context)
    {
        DeviceRegistrationPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<DeviceRegistrationPayload>(
                req.Body,
                cancellationToken: context.CancellationToken);
        }
        catch (JsonException ex)
        {
            LogInvalidPayload(_logger, ex);
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid registration payload.", context.CancellationToken);
            return bad;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.SerialNumber))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("SerialNumber is required.", context.CancellationToken);
            return bad;
        }

        // Forward to ImagingCoreApi (Private Link)
        var coreResponse = await _coreClient.CreateSessionAsync(payload, context.CancellationToken);

        if (!coreResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)coreResponse.StatusCode, payload.SerialNumber);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Session creation failed. Please retry.", context.CancellationToken);
            return err;
        }

        // Parse the core response to get sessionId and passcode
        using var coreJson = await coreResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(coreJson, cancellationToken: context.CancellationToken);
        var root = doc.RootElement;

        var sessionId    = root.GetProperty("sessionId").GetGuid();
        var passcode     = root.GetProperty("passcode").GetString()!;
        var state        = root.GetProperty("state").GetString()!;
        var preFlightResult = root.GetProperty("preFlightResult").GetString()!;

        // Issue device-session token (opaque 256-bit bearer)
        var token = _tokenService.IssueToken(sessionId);

        LogSessionCreated(_logger, sessionId, state, preFlightResult);

        var responseBody = new
        {
            sessionId,
            deviceSessionToken = token,
            passcode,
            state,
        };

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(responseBody), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid device registration payload.")]
    private static partial void LogInvalidPayload(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "ImagingCoreApi returned HTTP {StatusCode} for device {SerialNumber} session creation.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created (state={State}, preFlightResult={PreFlightResult}); token issued.")]
    private static partial void LogSessionCreated(
        ILogger logger, Guid sessionId, string state, string preFlightResult);
}

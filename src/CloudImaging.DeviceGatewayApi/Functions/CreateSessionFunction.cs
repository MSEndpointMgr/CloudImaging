using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions — Device-facing session bootstrap endpoint (T031, FR-001, FR-010).
/// Requires the boot-media mTLS client certificate (validated by
/// <see cref="MtlsCertificateValidationMiddleware"/>); the certificate is embedded in the WIM
/// and loaded by the Cloud Imaging Client at startup, so it is available for this call.
/// Exempt only from device-session token validation — the opaque token is issued by this call.
///
/// In addition to the mTLS thumbprint check, this endpoint requires an application-layer
/// proof-of-possession: the client signs a fresh challenge with the boot-media private key and
/// this function verifies the signature with the public key from the presented certificate
/// (FR-069). This defends session bootstrap even if the forwarded <c>X-ARR-ClientCert</c> header
/// trust is ever weakened, because the embedded public certificate alone is insufficient. The
/// signed nonce is additionally recorded as single-use (<see cref="DeviceSessionNonceStore"/>) so a
/// captured request cannot be replayed within the clock-skew window.
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
    private const int DefaultMaxSkewSeconds = 300;

    private readonly ImagingCoreClient _coreClient;
    private readonly DeviceSessionTokenService _tokenService;
    private readonly DeviceSessionNonceStore _nonceStore;
    private readonly TimeSpan _maxSignatureSkew;
    private readonly ILogger<CreateSessionFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CreateSessionFunction(
        ImagingCoreClient coreClient,
        DeviceSessionTokenService tokenService,
        DeviceSessionNonceStore nonceStore,
        IConfiguration configuration,
        ILogger<CreateSessionFunction> logger)
    {
        _coreClient = coreClient;
        _tokenService = tokenService;
        _nonceStore = nonceStore;
        _logger = logger;

        var skewSeconds = configuration.GetValue<int?>("MtlsProofOfPossession:MaxSkewSeconds")
            ?? DefaultMaxSkewSeconds;
        _maxSignatureSkew = TimeSpan.FromSeconds(skewSeconds > 0 ? skewSeconds : DefaultMaxSkewSeconds);
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

        // Verify application-layer proof-of-possession of the boot-media private key (FR-069).
        // The certificate was parsed + thumbprint-validated by MtlsCertificateValidationMiddleware
        // and stashed in FunctionContext.Items; we verify the signature with its public key.
        if (context.Items.TryGetValue(MtlsCertificateValidationMiddleware.ClientCertificateItemKey, out var certObj)
            && certObj is X509Certificate2 clientCert)
        {
            var popResult = DevicePayloadSignatureVerifier.Verify(
                clientCert,
                payload,
                _maxSignatureSkew,
                DateTimeOffset.UtcNow);

            if (popResult != DevicePayloadSignatureVerifier.Result.Valid)
            {
                LogProofOfPossessionRejected(_logger, popResult.ToString(), payload.SerialNumber);
                var denied = req.CreateResponse(HttpStatusCode.Unauthorized);
                // Include the specific reason (not just "failed") so the Cloud Imaging Client's
                // local log — the only diagnostic surface a field technician has in WinPE — shows
                // enough to distinguish e.g. a clock-skew "Expired" from a genuine "InvalidSignature"
                // without needing access to this API's own logs.
                await denied.WriteStringAsync(
                    $"Proof-of-possession verification failed ({popResult}).", context.CancellationToken);
                return denied;
            }

            // Enforce single-use of the signed nonce to eliminate the replay window (FR-069).
            // The nonce need only be remembered until the skew window elapses; after that the
            // timestamp check in DevicePayloadSignatureVerifier rejects a replay regardless.
            var proof = payload.ProofOfPossession!; // non-null once verification returned Valid
            bool firstUse;
            try
            {
                firstUse = await _nonceStore.TryConsumeAsync(
                    proof.Nonce,
                    DateTimeOffset.UtcNow.Add(_maxSignatureSkew),
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                // Fail closed: if uniqueness cannot be verified, do not create a session.
                LogNonceStoreUnavailable(_logger, ex);
                var unavailable = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await unavailable.WriteStringAsync(
                    "Session creation temporarily unavailable. Please retry.", context.CancellationToken);
                return unavailable;
            }

            if (!firstUse)
            {
                LogNonceReplay(_logger, payload.SerialNumber);
                var denied = req.CreateResponse(HttpStatusCode.Unauthorized);
                await denied.WriteStringAsync(
                    "Proof-of-possession has already been used.", context.CancellationToken);
                return denied;
            }
        }
        else
        {
            // The mTLS middleware must have populated the certificate. Its absence means the
            // request bypassed validation — fail closed.
            LogClientCertificateMissing(_logger, payload.SerialNumber);
            var denied = req.CreateResponse(HttpStatusCode.Unauthorized);
            await denied.WriteStringAsync(
                "Client certificate context is missing.", context.CancellationToken);
            return denied;
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

        var sessionId = root.GetProperty("sessionId").GetGuid();
        var passcode = root.GetProperty("passcode").GetString()!;
        var state = root.GetProperty("state").GetString()!;
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Proof-of-possession rejected ({Reason}) for device {SerialNumber}.")]
    private static partial void LogProofOfPossessionRejected(ILogger logger, string reason, string serialNumber);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Proof-of-possession nonce replay rejected for device {SerialNumber}.")]
    private static partial void LogNonceReplay(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Nonce store unavailable; failing closed on session creation.")]
    private static partial void LogNonceStoreUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Client certificate missing from context for device {SerialNumber}; failing closed.")]
    private static partial void LogClientCertificateMissing(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "ImagingCoreApi returned HTTP {StatusCode} for device {SerialNumber} session creation.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created (state={State}, preFlightResult={PreFlightResult}); token issued.")]
    private static partial void LogSessionCreated(
        ILogger logger, Guid sessionId, string state, string preFlightResult);
}

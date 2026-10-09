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
/// Device-facing Windows Autopilot hardware hash registration. Independent of imaging sessions,
/// so a device can submit before, after or instead of imaging.
///
/// GET  /api/v1/autopilot/availability        mTLS only
/// POST /api/v1/autopilot/registrations       mTLS + proof-of-possession over the hash + single-use nonce
/// GET  /api/v1/autopilot/registrations/{id}  mTLS + the status token returned by the submission
/// </summary>
public sealed partial class AutopilotRegistrationFunctions
{
    private const int DefaultMaxSkewSeconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ImagingCoreClient _coreClient;
    private readonly DeviceSessionNonceStore _nonceStore;
    private readonly TimeSpan _maxSignatureSkew;
    private readonly ILogger<AutopilotRegistrationFunctions> _logger;

    /// <param name="coreClient">Imaging Core API client the registration/status requests are proxied to.</param>
    /// <param name="nonceStore">Single-use nonce store guarding proof-of-possession replay.</param>
    /// <param name="configuration">Used to resolve the configured proof-of-possession max clock skew.</param>
    /// <param name="logger">Logger for this function group.</param>
    public AutopilotRegistrationFunctions(
        ImagingCoreClient coreClient,
        DeviceSessionNonceStore nonceStore,
        IConfiguration configuration,
        ILogger<AutopilotRegistrationFunctions> logger)
    {
        _coreClient = coreClient;
        _nonceStore = nonceStore;
        _logger = logger;

        var skewSeconds = configuration.GetValue<int?>("MtlsProofOfPossession:MaxSkewSeconds") ?? DefaultMaxSkewSeconds;
        _maxSignatureSkew = TimeSpan.FromSeconds(skewSeconds > 0 ? skewSeconds : DefaultMaxSkewSeconds);
    }

    /// <summary>GET v1/autopilot/availability. mTLS only; proxies the Imaging Core API's availability check.</summary>
    [Function("GetAutopilotAvailability")]
    public async Task<HttpResponseData> GetAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/autopilot/availability")] HttpRequestData req,
        FunctionContext context)
    {
        using var coreResponse = await _coreClient.GetAutopilotAvailabilityAsync(context.CancellationToken);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>POST v1/autopilot/registrations. Verifies proof-of-possession and nonce uniqueness, then forwards the hardware hash submission.</summary>
    [Function("SubmitAutopilotRegistration")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/autopilot/registrations")] HttpRequestData req,
        FunctionContext context)
    {
        AutopilotHashSubmission? submission;
        try
        {
            submission = await JsonSerializer.DeserializeAsync<AutopilotHashSubmission>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, $"Invalid submission payload: {ex.Message}", context.CancellationToken);
        }

        if (submission is null || string.IsNullOrWhiteSpace(submission.SerialNumber) || string.IsNullOrWhiteSpace(submission.HardwareHash))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "SerialNumber and HardwareHash are required.", context.CancellationToken);
        }

        if (!context.Items.TryGetValue(MtlsCertificateValidationMiddleware.ClientCertificateItemKey, out var certObj)
            || certObj is not X509Certificate2 clientCert)
        {
            LogClientCertificateMissing(_logger, submission.SerialNumber);
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Client certificate context is missing.", context.CancellationToken);
        }

        var popResult = DevicePayloadSignatureVerifier.VerifyAutopilot(clientCert, submission, _maxSignatureSkew, DateTimeOffset.UtcNow);
        if (popResult != DevicePayloadSignatureVerifier.Result.Valid)
        {
            LogProofOfPossessionRejected(_logger, popResult, submission.SerialNumber);
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, $"Proof-of-possession verification failed ({popResult}).", context.CancellationToken);
        }

        bool firstUse;
        try
        {
            firstUse = await _nonceStore.TryConsumeAsync(submission.ProofOfPossession!.Nonce, DateTimeOffset.UtcNow.Add(_maxSignatureSkew), context.CancellationToken);
        }
        catch (Exception ex)
        {
            // Fail closed: without a uniqueness check a captured submission could be replayed.
            LogNonceStoreUnavailable(_logger, ex);
            return await ProblemAsync(req, HttpStatusCode.ServiceUnavailable, "Registration is temporarily unavailable. Please retry.", context.CancellationToken);
        }

        if (!firstUse)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Proof-of-possession has already been used.", context.CancellationToken);
        }

        using var coreResponse = await _coreClient.SubmitAutopilotRegistrationAsync(submission, context.CancellationToken);
        LogSubmissionForwarded(_logger, submission.SerialNumber, (int)coreResponse.StatusCode);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>GET v1/autopilot/registrations/{requestId}. Requires the bearer status token returned by <see cref="Submit"/>.</summary>
    [Function("GetAutopilotRegistrationStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/autopilot/registrations/{requestId}")] HttpRequestData req,
        string requestId,
        FunctionContext context)
    {
        if (!Guid.TryParse(requestId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid request id.", context.CancellationToken);
        }

        if (!context.Items.TryGetValue("BearerToken", out var tokenObj) || tokenObj is not string statusToken)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "A status token is required.", context.CancellationToken);
        }

        using var coreResponse = await _coreClient.GetAutopilotDeviceStatusAsync(id, statusToken, context.CancellationToken);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyAsync(HttpRequestData req, HttpResponseMessage upstream, CancellationToken ct)
    {
        // Upstream outages surface as 503 so the device shows a retryable error rather than a Core internals leak.
        var status = (int)upstream.StatusCode >= 500 && upstream.StatusCode != HttpStatusCode.BadGateway
            ? HttpStatusCode.ServiceUnavailable
            : upstream.StatusCode;
        var response = req.CreateResponse(status);
        var content = await upstream.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            response.Headers.Add("Content-Type", upstream.Content.Headers.ContentType?.MediaType ?? "application/json");
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }

    private static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { title = status.ToString(), status = (int)status, detail }, JsonOptions), ct);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Autopilot proof-of-possession rejected ({Reason}) for device {SerialNumber}.")]
    private static partial void LogProofOfPossessionRejected(ILogger logger, DevicePayloadSignatureVerifier.Result reason, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error, Message = "Client certificate missing from context for Autopilot submission from {SerialNumber}; failing closed.")]
    private static partial void LogClientCertificateMissing(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nonce store unavailable; failing closed on Autopilot submission.")]
    private static partial void LogNonceStoreUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot submission from {SerialNumber} forwarded; Imaging Core returned HTTP {StatusCode}.")]
    private static partial void LogSubmissionForwarded(ILogger logger, string serialNumber, int statusCode);
}

using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions/{sessionId}/cache/validate — Cache hash validation endpoint (T056b, FR-009d).
/// Allows the Cloud Imaging Client to validate whether a locally-cached WIM
/// matches the server-side SHA-256 hash for the assigned OS image.
///
/// Request body: { "sha256Hash": "&lt;client-computed hex hash&gt;" }
/// Response: 200 OK { "valid": true|false }
///   true  → cached WIM hash matches the assigned OS image; skip download.
///   false → hashes differ; download the image from the SAS URL.
///
/// Requires valid device-session Bearer token.
/// </summary>
public sealed partial class CacheValidationFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<CacheValidationFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CacheValidationFunction(
        ImagingCoreClient coreClient,
        ILogger<CacheValidationFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("CacheValidation")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions/{sessionId}/cache/validate")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || string.IsNullOrWhiteSpace(hashProp.GetString()))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("sha256Hash field is required.", context.CancellationToken);
            return bad;
        }

        var clientHash = hashProp.GetString()!;

        // Proxy the validation request to ImagingCoreApi which knows the authoritative hash
        var coreResponse = await _coreClient.ValidateCacheHashAsync(
            sessionGuid,
            new { sha256Hash = clientHash },
            context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        if (coreResponse.IsSuccessStatusCode)
        {
            // Relay ImagingCore's answer
            var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
            await response.WriteStringAsync(content, context.CancellationToken);
        }
        else
        {
            // Could not validate (e.g. session not found) — signal invalid
            await response.WriteStringAsync(
                JsonSerializer.Serialize(new { valid = false }), context.CancellationToken);
        }

        LogValidated(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Cache validation for session {SessionId} — ImagingCore HTTP {StatusCode}.")]
    private static partial void LogValidated(ILogger logger, Guid sessionId, int statusCode);
}

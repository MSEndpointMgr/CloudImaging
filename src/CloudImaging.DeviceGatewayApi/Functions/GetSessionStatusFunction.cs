using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/sessions/{sessionId}/status — Polls session state for the Cloud Imaging Client (T043, FR-031).
///
/// Response contract (plan.md constraint):
///   - <c>currentStep</c> (string | null): active <see cref="ImagingStep"/> name, null until imaging begins.
///   - <c>overallProgressPercent</c> (int): overall imaging completion 0–100.
///   - <c>state</c>: current <see cref="SessionState"/> as a string.
///   - <c>sasTokenUrl</c>: SAS URL for the OS image blob (populated after assignment).
///
/// Requires valid device-session Bearer token (DeviceSessionTokenValidationMiddleware).
/// Rate-limited by RateLimitingMiddleware.
/// </summary>
public sealed partial class GetSessionStatusFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<GetSessionStatusFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GetSessionStatusFunction(
        ImagingCoreClient coreClient,
        ILogger<GetSessionStatusFunction> logger)
    {
        _coreClient = coreClient;
        _logger     = logger;
    }

    [Function("GetSessionStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/sessions/{sessionId}/status")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        // Validate that the requested session ID matches the token's embedded session ID
        if (context.Items.TryGetValue("SessionIdKey", out var tokenSessionIdObj)
            && tokenSessionIdObj is Guid tokenSessionId
            && tokenSessionId != sessionGuid)
        {
            LogSessionIdMismatch(_logger, sessionGuid, tokenSessionId);
            return req.CreateResponse(HttpStatusCode.Forbidden);
        }

        var coreResponse = await _coreClient.GetSessionStatusAsync(sessionGuid, context.CancellationToken);

        if (coreResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            return req.CreateResponse(HttpStatusCode.NotFound);

        if (!coreResponse.IsSuccessStatusCode)
        {
            LogUpstreamError(_logger, sessionGuid, (int)coreResponse.StatusCode);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        using var coreJson = await coreResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(coreJson, cancellationToken: context.CancellationToken);
        var root = doc.RootElement;

        // Map to contract response — always include currentStep and overallProgressPercent (plan.md)
        var responseBody = new
        {
            sessionId             = root.TryGetProperty("sessionId",             out var sid) ? sid.GetGuid()    : sessionGuid,
            state                 = root.TryGetProperty("state",                 out var st)  ? st.GetString()   : "Unknown",
            currentStep           = root.TryGetProperty("currentStep",           out var cs)  ? cs.GetString()   : null,
            overallProgressPercent= root.TryGetProperty("overallProgressPercent",out var op)  ? op.GetInt32()    : 0,
            sasTokenUrl           = root.TryGetProperty("sasTokenUrl",           out var su)  ? su.GetString()   : null,
            sasTokenUrlExpiresAt  = root.TryGetProperty("sasTokenUrlExpiresAt",  out var se)  ? se.GetString()   : null,
            sha256Hash            = root.TryGetProperty("sha256Hash",            out var sh)  ? sh.GetString()   : null,
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(responseBody, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session ID mismatch: requested {RequestedId} but token is for {TokenId}.")]
    private static partial void LogSessionIdMismatch(ILogger logger, Guid requestedId, Guid tokenId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "ImagingCoreApi returned HTTP {StatusCode} for session {SessionId} status.")]
    private static partial void LogUpstreamError(ILogger logger, Guid sessionId, int statusCode);
}

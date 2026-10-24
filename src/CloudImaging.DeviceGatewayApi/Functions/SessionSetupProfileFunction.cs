using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/sessions/{sessionId}/setup-profile. Returns the setup profile version pinned on the
/// device's session: a descriptor with a short-lived read URL and the content hash. Core's JSON is
/// mapped field by field, so a future Core field is never published to devices by accident.
/// </summary>
public sealed partial class SessionSetupProfileFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<SessionSetupProfileFunction> _logger;

    /// <param name="coreClient">Imaging Core API client.</param>
    /// <param name="logger">Logger.</param>
    public SessionSetupProfileFunction(ImagingCoreClient coreClient, ILogger<SessionSetupProfileFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET v1/sessions/{sessionId}/setup-profile.</summary>
    [Function("GetSessionSetupProfile")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/sessions/{sessionId}/setup-profile")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        if (context.Items.TryGetValue(DeviceSessionTokenValidationMiddleware.SessionIdKey, out var tokenSession)
            && tokenSession is Guid tokenSessionId && tokenSessionId != sessionGuid)
        {
            return req.CreateResponse(HttpStatusCode.Forbidden);
        }

        HttpResponseMessage coreResponse;
        try
        {
            coreResponse = await _coreClient.GetSessionSetupProfileAsync(sessionGuid, ct);
        }
        catch (HttpRequestException ex)
        {
            LogCoreUnreachable(_logger, ex, sessionGuid);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        using (coreResponse)
        {
            var status = (int)coreResponse.StatusCode;
            if (status is 204 or 404 or 409)
            {
                return req.CreateResponse(coreResponse.StatusCode);
            }
            if (!coreResponse.IsSuccessStatusCode)
            {
                LogCoreFailed(_logger, sessionGuid, status);
                return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            }

            using var document = await JsonDocument.ParseAsync(await coreResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            var contentUrl = root.GetProperty("contentUrl").GetString();
            var body = new
            {
                profileId = root.GetProperty("profileId").GetGuid(),
                profileName = root.GetProperty("profileName").GetString(),
                version = root.GetProperty("version").GetInt32(),
                failureHandling = root.GetProperty("failureHandling").GetString(),
                contentUrl,
                contentUrlExpiresAt = root.GetProperty("contentUrlExpiresAt").GetDateTimeOffset(),
                contentSha256 = root.GetProperty("contentSha256").GetString(),
                contentSizeBytes = root.GetProperty("contentSizeBytes").GetInt64(),
                scripts = root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array
                    ? [.. scripts.EnumerateArray().Select(Text)]
                    : new List<object>(),
                answerFile = root.TryGetProperty("answerFile", out var answerFile) && answerFile.ValueKind == JsonValueKind.Object ? Text(answerFile) : null,
            };

            // The SAS URL is a credential: log the host only.
            LogDescriptorIssued(_logger, sessionGuid, body.version, Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri) ? uri.Host : "unknown");
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
            return response;
        }
    }

    private static object Text(JsonElement text) => new
    {
        id = text.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : (Guid?)null,
        contentSha256 = text.GetProperty("contentSha256").GetString(),
        sizeBytes = text.GetProperty("sizeBytes").GetInt64(),
        contentUrl = text.GetProperty("contentUrl").GetString(),
        contentUrlExpiresAt = text.GetProperty("contentUrlExpiresAt").GetDateTimeOffset(),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile version {Version} issued to session {SessionId} from {Host}.")]
    private static partial void LogDescriptorIssued(ILogger logger, Guid sessionId, int version, string host);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core returned HTTP {StatusCode} for the setup profile of session {SessionId}.")]
    private static partial void LogCoreFailed(ILogger logger, Guid sessionId, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core could not be reached for the setup profile of session {SessionId}.")]
    private static partial void LogCoreUnreachable(ILogger logger, Exception ex, Guid sessionId);
}

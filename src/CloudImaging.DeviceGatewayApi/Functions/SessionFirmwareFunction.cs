using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/sessions/{sessionId}/firmware. The BIOS pinned on the device's session at
/// registration, with a 60-minute read link; 204 when the device gets no BIOS update, 410 when its
/// file is no longer stored. Core's JSON
/// is mapped field by field, so a future Core field is never published to devices by accident.
/// </summary>
public sealed partial class SessionFirmwareFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<SessionFirmwareFunction> _logger;

    /// <param name="coreClient">Imaging Core API client.</param>
    /// <param name="logger">Logger.</param>
    public SessionFirmwareFunction(ImagingCoreClient coreClient, ILogger<SessionFirmwareFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET v1/sessions/{sessionId}/firmware.</summary>
    [Function("GetSessionFirmware")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/sessions/{sessionId}/firmware")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (!context.Items.TryGetValue(DeviceSessionTokenValidationMiddleware.SessionIdKey, out var tokenSession)
            || tokenSession is not Guid tokenSessionId || tokenSessionId != sessionGuid)
        {
            return req.CreateResponse(HttpStatusCode.Forbidden);
        }

        HttpResponseMessage coreResponse;
        try
        {
            coreResponse = await _coreClient.GetSessionFirmwareAsync(sessionGuid, ct);
        }
        catch (HttpRequestException ex)
        {
            LogCoreUnreachable(_logger, ex, sessionGuid);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        using (coreResponse)
        {
            var status = (int)coreResponse.StatusCode;
            if (status is 204 or 404 or 409 or 410)
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
            var downloadUrl = root.GetProperty("downloadUrl").GetString();
            var body = new
            {
                platformId = root.GetProperty("platformId").GetString(),
                version = root.GetProperty("version").GetString(),
                installedVersion = root.TryGetProperty("installedVersion", out var installed) && installed.ValueKind == JsonValueKind.String ? installed.GetString() : null,
                fileName = root.GetProperty("fileName").GetString(),
                sha256 = root.GetProperty("sha256").GetString(),
                sizeBytes = root.GetProperty("sizeBytes").GetInt64(),
                downloadUrl,
                downloadUrlExpiresAt = root.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset(),
            };

            // The read link is a credential: log the host only.
            LogDescriptorIssued(_logger, sessionGuid, body.version ?? "unknown", Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ? uri.Host : "unknown");
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
            return response;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "BIOS {Version} issued to session {SessionId} from {Host}.")]
    private static partial void LogDescriptorIssued(ILogger logger, Guid sessionId, string version, string host);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core returned HTTP {StatusCode} for the BIOS of session {SessionId}.")]
    private static partial void LogCoreFailed(ILogger logger, Guid sessionId, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core could not be reached for the BIOS of session {SessionId}.")]
    private static partial void LogCoreUnreachable(ILogger logger, Exception ex, Guid sessionId);
}

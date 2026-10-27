using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// The driver set pinned on the device's session (contracts/device-gateway.md).
///
/// GET  /api/v1/sessions/{sessionId}/driver-set                            descriptor with a read link per component
/// POST /api/v1/sessions/{sessionId}/driver-set/components/{index}/link    a new read link for one component
///
/// Core's JSON is mapped field by field, so a future Core field is never published to devices by
/// accident, and read links are logged by host only.
/// </summary>
public sealed partial class SessionDriverSetFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<SessionDriverSetFunction> _logger;

    /// <param name="coreClient">Imaging Core API client.</param>
    /// <param name="logger">Logger.</param>
    public SessionDriverSetFunction(ImagingCoreClient coreClient, ILogger<SessionDriverSetFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET v1/sessions/{sessionId}/driver-set.</summary>
    [Function("GetSessionDriverSet")]
    public async Task<HttpResponseData> GetDriverSet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/sessions/{sessionId}/driver-set")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (Refuse(req, sessionId, context, out var sessionGuid) is { } refused)
        {
            return refused;
        }

        return await RelayAsync(req, sessionGuid, () => _coreClient.GetSessionDriverSetAsync(sessionGuid, context.CancellationToken), MapDescriptor, context.CancellationToken);
    }

    /// <summary>POST v1/sessions/{sessionId}/driver-set/components/{index}/link.</summary>
    [Function("RefreshSessionDriverComponentLink")]
    public async Task<HttpResponseData> RefreshLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions/{sessionId}/driver-set/components/{index}/link")] HttpRequestData req,
        string sessionId,
        string index,
        FunctionContext context)
    {
        if (Refuse(req, sessionId, context, out var sessionGuid) is { } refused)
        {
            return refused;
        }

        if (!int.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        return await RelayAsync(req, sessionGuid, () => _coreClient.RefreshSessionDriverComponentLinkAsync(sessionGuid, position, context.CancellationToken), MapLink, context.CancellationToken);
    }

    private static HttpResponseData? Refuse(HttpRequestData req, string sessionId, FunctionContext context, out Guid sessionGuid)
    {
        if (!Guid.TryParse(sessionId, out sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        return context.Items.TryGetValue(DeviceSessionTokenValidationMiddleware.SessionIdKey, out var tokenSession) && tokenSession is Guid tokenSessionId && tokenSessionId == sessionGuid
            ? null
            : req.CreateResponse(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseData> RelayAsync(HttpRequestData req, Guid sessionId, Func<Task<HttpResponseMessage>> call, Func<JsonElement, (object Body, string? Host)> map, CancellationToken ct)
    {
        HttpResponseMessage coreResponse;
        try
        {
            coreResponse = await call();
        }
        catch (HttpRequestException ex)
        {
            LogCoreUnreachable(_logger, ex, sessionId);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        using (coreResponse)
        {
            var status = (int)coreResponse.StatusCode;
            if (status is 400 or 404 or 409 or 429)
            {
                var relayed = req.CreateResponse(coreResponse.StatusCode);
                if (coreResponse.Headers.RetryAfter?.Delta is { } retryAfter)
                {
                    relayed.Headers.Add("Retry-After", ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return relayed;
            }

            if (!coreResponse.IsSuccessStatusCode)
            {
                LogCoreFailed(_logger, sessionId, status);
                return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            }

            using var document = await JsonDocument.ParseAsync(await coreResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var (body, host) = map(document.RootElement);

            // Read links are credentials: log the host only.
            LogRelayed(_logger, sessionId, host ?? "none");
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
            return response;
        }
    }

    private static (object Body, string? Host) MapDescriptor(JsonElement root)
    {
        var components = root.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(MapComponent).ToList()
            : [];
        var body = new
        {
            skipReason = String(root, "skipReason"),
            matchKey = String(root, "matchKey"),
            modelName = String(root, "modelName"),
            release = String(root, "release"),
            setVersion = root.TryGetProperty("setVersion", out var version) && version.ValueKind == JsonValueKind.Number ? version.GetInt32() : (int?)null,
            components,
        };
        var host = components.Count > 0 && Uri.TryCreate(components[0].downloadUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
        return (body, host);
    }

    private static Component MapComponent(JsonElement c) => new(
        c.GetProperty("index").GetInt32(),
        c.GetProperty("name").GetString(),
        c.GetProperty("componentId").GetString(),
        String(c, "version"),
        String(c, "origin"),
        String(c, "category"),
        String(c, "action"),
        c.TryGetProperty("critical", out var critical) && critical.ValueKind == JsonValueKind.True,
        c.GetProperty("sizeBytes").GetInt64(),
        c.GetProperty("sha256").GetString(),
        c.GetProperty("fileName").GetString(),
        c.GetProperty("downloadUrl").GetString(),
        c.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset(),
        String(c, "extract"),
        c.TryGetProperty("installCommand", out var command) && command.ValueKind == JsonValueKind.Object
            ? new InstallCommand(command.GetProperty("executable").GetString(), String(command, "arguments"))
            : null,
        c.TryGetProperty("pciIds", out var pciIds) && pciIds.ValueKind == JsonValueKind.Array
            ? pciIds.EnumerateArray().Select(p => new PciId(String(p, "vendor"), String(p, "device"), String(p, "subVendor"), String(p, "subDevice"))).ToList()
            : []);

    private static (object Body, string? Host) MapLink(JsonElement root)
    {
        var url = root.GetProperty("downloadUrl").GetString();
        return (new { downloadUrl = url, downloadUrlExpiresAt = root.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset() }, Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

#pragma warning disable IDE1006 // Lower-case members are the wire names.
    private sealed record Component(int index, string? name, string? componentId, string? version, string? origin, string? category, string? action, bool critical, long sizeBytes, string? sha256, string? fileName, string? downloadUrl, DateTimeOffset downloadUrlExpiresAt, string? extract, InstallCommand? installCommand, IReadOnlyList<PciId> pciIds);

    private sealed record InstallCommand(string? executable, string? arguments);

    private sealed record PciId(string? vendor, string? device, string? subVendor, string? subDevice);
#pragma warning restore IDE1006

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver set relayed to session {SessionId}; read links on {Host}.")]
    private static partial void LogRelayed(ILogger logger, Guid sessionId, string host);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core returned HTTP {StatusCode} for the driver set of session {SessionId}.")]
    private static partial void LogCoreFailed(ILogger logger, Guid sessionId, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core could not be reached for the driver set of session {SessionId}.")]
    private static partial void LogCoreUnreachable(ILogger logger, Exception ex, Guid sessionId);
}

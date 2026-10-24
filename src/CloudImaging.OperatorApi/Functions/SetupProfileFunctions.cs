using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Setup profile proxy endpoints. All rules live in the Imaging Core API; this layer checks route
/// parameters, forwards the body unchanged and relays status and body. PortalAccess only.
///
/// GET    /api/setup-profiles                          profile list
/// GET    /api/setup-profiles/{id}                     profile with current content
/// POST   /api/setup-profiles                          create
/// PUT    /api/setup-profiles/{id}                     save
/// DELETE /api/setup-profiles/{id}?changedBy=          delete
/// POST   /api/setup-profiles/{id}/restore             restore a version as a new version
/// GET    /api/setup-profiles/{id}/versions            retained versions
/// GET    /api/setup-profiles/{id}/versions/{version}  one version with content
/// GET    /api/setup-profiles/{id}/history             targeting and profile history
/// POST   /api/setup-profiles/content?kind=&amp;uploadedBy=  upload a script or answer file text
/// GET    /api/setup-profiles/{id}/content/{sha256}    read a text the profile uses
/// </summary>
public sealed partial class SetupProfileFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<SetupProfileFunctions> _logger;

    /// <param name="coreClient">Imaging Core API client the requests are forwarded to.</param>
    /// <param name="logger">Logger for this function group.</param>
    public SetupProfileFunctions(ImagingCoreClient coreClient, ILogger<SetupProfileFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET setup-profiles.</summary>
    [Function("GetSetupProfiles")]
    public async Task<HttpResponseData> GetSetupProfiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles")] HttpRequestData req,
        FunctionContext context) =>
        await ProxyResponseAsync(req, await _coreClient.GetSetupProfilesAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>GET setup-profiles/{id}.</summary>
    [Function("GetSetupProfile")]
    public async Task<HttpResponseData> GetSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.GetSetupProfileAsync(profileId, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST setup-profiles.</summary>
    [Function("CreateSetupProfile")]
    public async Task<HttpResponseData> CreateSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "setup-profiles")] HttpRequestData req,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.CreateSetupProfileAsync(payload, context.CancellationToken);
        LogChanged(_logger, "created", null, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>PUT setup-profiles/{id}.</summary>
    [Function("UpdateSetupProfile")]
    public async Task<HttpResponseData> UpdateSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var profileId) || payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.UpdateSetupProfileAsync(profileId, payload, context.CancellationToken);
        LogChanged(_logger, "saved", profileId, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>DELETE setup-profiles/{id}?changedBy=.</summary>
    [Function("DeleteSetupProfile")]
    public async Task<HttpResponseData> DeleteSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var changedBy = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["changedBy"];
        var coreResponse = await _coreClient.DeleteSetupProfileAsync(profileId, changedBy, context.CancellationToken);
        LogChanged(_logger, "deleted", profileId, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>POST setup-profiles/{id}/restore.</summary>
    [Function("RestoreSetupProfileVersion")]
    public async Task<HttpResponseData> RestoreSetupProfileVersion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "setup-profiles/{id}/restore")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var profileId) || payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.RestoreSetupProfileVersionAsync(profileId, payload, context.CancellationToken);
        LogChanged(_logger, "restored", profileId, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>GET setup-profiles/{id}/versions.</summary>
    [Function("GetSetupProfileVersions")]
    public async Task<HttpResponseData> GetSetupProfileVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles/{id}/versions")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.GetSetupProfileVersionsAsync(profileId, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET setup-profiles/{id}/versions/{version}.</summary>
    [Function("GetSetupProfileVersion")]
    public async Task<HttpResponseData> GetSetupProfileVersion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles/{id}/versions/{version}")] HttpRequestData req,
        string id,
        string version,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId) || !int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var versionNumber) || versionNumber < 1)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.GetSetupProfileVersionAsync(profileId, versionNumber, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET setup-profiles/{id}/history.</summary>
    [Function("GetSetupProfileHistory")]
    public async Task<HttpResponseData> GetSetupProfileHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles/{id}/history")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.GetSetupProfileHistoryAsync(profileId, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST setup-profiles/content?kind=. The raw body goes to Core unparsed; Core applies every limit.</summary>
    [Function("UploadSetupProfileContent")]
    public async Task<HttpResponseData> UploadSetupProfileContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "setup-profiles/content")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var contentType = req.Headers.TryGetValues("Content-Type", out var types) ? types.FirstOrDefault() : null;
        long? length = req.Headers.TryGetValues("Content-Length", out var lengths) && long.TryParse(lengths.FirstOrDefault(), out var parsed) ? parsed : null;
        var coreResponse = await _coreClient.UploadSetupProfileContentAsync(query["kind"], req.Body, contentType, length, query["uploadedBy"], context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>GET setup-profiles/{id}/content/{sha256}. Relays the text's bytes and media type.</summary>
    [Function("GetSetupProfileContent")]
    public async Task<HttpResponseData> GetSetupProfileContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "setup-profiles/{id}/content/{sha256}")] HttpRequestData req,
        string id,
        string sha256,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        using var coreResponse = await _coreClient.GetSetupProfileContentAsync(profileId, sha256, context.CancellationToken);
        var response = req.CreateResponse((HttpStatusCode)(int)coreResponse.StatusCode);
        if (coreResponse.IsSuccessStatusCode)
        {
            response.Headers.Add("Content-Type", coreResponse.Content.Headers.ContentType?.ToString() ?? "text/plain; charset=utf-8");
            await using var stream = await coreResponse.Content.ReadAsStreamAsync(context.CancellationToken);
            await stream.CopyToAsync(response.Body, context.CancellationToken);
        }
        return response;
    }

    private static async Task<object?> ReadPayloadAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<HttpResponseData> ProxyResponseAsync(HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        using (coreResponse)
        {
            var response = req.CreateResponse((HttpStatusCode)(int)coreResponse.StatusCode);
            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(content))
            {
                // Keep Core's media type: refusals are problem+json with the problems the editor shows.
                var mediaType = coreResponse.Content.Headers.ContentType?.MediaType;
                response.Headers.Add("Content-Type", mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ? mediaType : "text/plain");
                await response.WriteStringAsync(content, ct);
            }
            return response;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile {Action} ({ProfileId}); Imaging Core returned HTTP {StatusCode}.")]
    private static partial void LogChanged(ILogger logger, string action, Guid? profileId, int statusCode);
}

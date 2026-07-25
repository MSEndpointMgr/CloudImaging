using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Portal-role boot image lifecycle CRUD proxy endpoints (T116, FR-063).
/// Requires CloudImaging.Administrator role (enforced by AppRoleAuthorizationMiddleware).
///
/// POST   /api/boot-images/publish    → ImagingCore POST /api/internal/boot-images/publish
/// DELETE /api/boot-images/{id}       → ImagingCore DELETE /api/internal/boot-images/{id}
/// </summary>
public sealed partial class BootImageLifecycleFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BootImageLifecycleFunctions> _logger;

    public BootImageLifecycleFunctions(
        ImagingCoreClient coreClient,
        ILogger<BootImageLifecycleFunctions> logger)
    {
        _coreClient = coreClient;
        _logger     = logger;
    }

    // ── POST /api/boot-images/publish ─────────────────────────────────────────

    [Function("PublishBootImage")]
    public async Task<HttpResponseData> PublishBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "boot-images/publish")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());

        var coreResponse = await _coreClient.PublishBootImageAsync(payload!, context.CancellationToken);

        LogPublishProxied(_logger, (int)coreResponse.StatusCode);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    // ── DELETE /api/boot-images/{id} ─────────────────────────────────────────

    [Function("DeleteBootImage")]
    public async Task<HttpResponseData> DeleteBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "boot-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var bootImageId))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var coreResponse = await _coreClient.DeleteBootImageAsync(bootImageId, context.CancellationToken);
        LogDeleteProxied(_logger, bootImageId, (int)coreResponse.StatusCode);
        return req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<HttpResponseData> ProxyAsync(
        HttpRequestData req,
        HttpResponseMessage coreResponse,
        CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(await coreResponse.Content.ReadAsStringAsync(ct), ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image publish proxied — ImagingCore HTTP {StatusCode}.")]
    private static partial void LogPublishProxied(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image delete proxied for {BootImageId} — ImagingCore HTTP {StatusCode}.")]
    private static partial void LogDeleteProxied(ILogger logger, Guid bootImageId, int statusCode);
}

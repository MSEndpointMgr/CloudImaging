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
/// DELETE /api/boot-images/{id}       → ImagingCore DELETE /api/internal/boot-images/{id}
///
/// Publishing is not here: it happens through the staged upload pipeline
/// (BootImageUploadFunctions), which verifies the blob before it reaches the catalog.
/// </summary>
public sealed partial class BootImageLifecycleFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BootImageLifecycleFunctions> _logger;

    /// <summary>Initializes a new instance of the <see cref="BootImageLifecycleFunctions"/> class.</summary>
    public BootImageLifecycleFunctions(
        ImagingCoreClient coreClient,
        ILogger<BootImageLifecycleFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    // ── DELETE /api/boot-images/{id} ─────────────────────────────────────────

    /// <summary>Deletes a boot image from the catalog.</summary>
    [Function("DeleteBootImage")]
    public async Task<HttpResponseData> DeleteBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "boot-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var bootImageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.DeleteBootImageAsync(bootImageId, context.CancellationToken);
        LogDeleteProxied(_logger, bootImageId, (int)coreResponse.StatusCode);
        // Forward the body, not just the status code. ImagingCore answers 409 with a plain-text
        // explanation ("Cannot delete the currently published boot image. Publish a replacement
        // first.") and returning the bare status dropped it, so the portal had nothing to show
        // and the click looked like it did nothing at all.
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<HttpResponseData> ProxyAsync(
        HttpRequestData req,
        HttpResponseMessage coreResponse,
        CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        // Forward the body regardless of content type — plain-text error messages were previously
        // dropped here because only "json" content types were forwarded, so the browser saw the
        // right status code but an empty/generic body.
        var body = await coreResponse.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(body))
        {
            response.Headers.Add("Content-Type", coreResponse.Content.Headers.ContentType?.MediaType ?? "text/plain");
            await response.WriteStringAsync(body, ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image delete proxied for {BootImageId} — ImagingCore HTTP {StatusCode}.")]
    private static partial void LogDeleteProxied(ILogger logger, Guid bootImageId, int statusCode);
}

using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Boot image query and SAS issuance proxy endpoints (T067, FR-053, FR-056).
///
/// GET  /api/boot-images              — lists active boot images; MUST include sha256Hash + isLatestPublished
/// GET  /api/boot-images/{id}         — single boot image
/// POST /api/boot-images/{id}/sas     — issues SAS + sha256Hash (FR-056: Media Builder must verify WIM)
/// </summary>
public sealed partial class BootImageFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BootImageFunctions> _logger;

    public BootImageFunctions(
        ImagingCoreClient coreClient,
        ILogger<BootImageFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    // ── GET /api/boot-images ──────────────────────────────────────────────────

    [Function("GetBootImages")]
    public async Task<HttpResponseData> GetBootImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "boot-images")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetBootImagesAsync(context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    // ── GET /api/boot-images/{id} ─────────────────────────────────────────────

    [Function("GetBootImageById")]
    public async Task<HttpResponseData> GetBootImageById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "boot-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var bootImageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.GetBootImageByIdAsync(bootImageId, context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    // ── POST /api/boot-images/{id}/sas ────────────────────────────────────────

    [Function("GetBootImageSasUrl")]
    public async Task<HttpResponseData> GetBootImageSasUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "boot-images/{id}/sas")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var bootImageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Response MUST include sha256Hash (FR-056 — Media Builder verifies WIM before USB deploy)
        var coreResponse = await _coreClient.GetBootImageSasAsync(bootImageId, context.CancellationToken);
        LogSasProxied(_logger, bootImageId, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<HttpResponseData> ProxyResponseAsync(
        HttpRequestData req,
        HttpResponseMessage coreResponse,
        CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image SAS proxied for {BootImageId} — HTTP {StatusCode}.")]
    private static partial void LogSasProxied(ILogger logger, Guid bootImageId, int statusCode);
}

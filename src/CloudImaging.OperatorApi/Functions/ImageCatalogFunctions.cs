using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// OS image catalog proxy endpoints (T085, FR-036, FR-037).
/// Require CloudImaging.PortalAccess (read) or CloudImaging.Administrator (write).
/// Role enforcement is applied by AppRoleAuthorizationMiddleware.
/// </summary>
public sealed partial class ImageCatalogFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<ImageCatalogFunctions> _logger;

    public ImageCatalogFunctions(
        ImagingCoreClient coreClient,
        ILogger<ImageCatalogFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetImages")]
    public async Task<HttpResponseData> GetImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "images")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.GetImagesAsync(context.CancellationToken), context.CancellationToken);

    [Function("GetImageById")]
    public async Task<HttpResponseData> GetImageById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        return await Proxy(req, await _coreClient.GetImagesAsync(context.CancellationToken), context.CancellationToken);
    }

    [Function("CreateImage")]
    public async Task<HttpResponseData> CreateImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "images")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.CreateImageAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    [Function("UpdateImage")]
    public async Task<HttpResponseData> UpdateImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UpdateImageAsync(imageId, payload!, context.CancellationToken), context.CancellationToken);
    }

    [Function("DeleteImage")]
    public async Task<HttpResponseData> DeleteImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        return await Proxy(req, await _coreClient.DeleteImageAsync(imageId, context.CancellationToken), context.CancellationToken);
    }

    private static async Task<HttpResponseData> Proxy(
        HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(await coreResponse.Content.ReadAsStringAsync(ct), ct);
        }
        return response;
    }
}

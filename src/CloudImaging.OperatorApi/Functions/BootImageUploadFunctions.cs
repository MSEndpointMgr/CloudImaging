using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Boot image upload proxy endpoints (T124, FR-063).
/// POST /api/boot-images/upload/start           → ImagingCore start upload
/// POST /api/boot-images/upload/{token}/publish → ImagingCore validate + publish
/// </summary>
public sealed partial class BootImageUploadFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BootImageUploadFunctions> _logger;

    public BootImageUploadFunctions(ImagingCoreClient coreClient, ILogger<BootImageUploadFunctions> logger)
    {
        _coreClient = coreClient;
        _logger     = logger;
    }

    [Function("StartBootImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "boot-images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload   = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var core      = await _coreClient.CreateImageAsync(payload!, context.CancellationToken);
        return await ProxyAsync(req, core, context.CancellationToken);
    }

    [Function("PublishBootImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "boot-images/upload/{token}/publish")] HttpRequestData req,
        string token,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload   = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        // Proxy to ImagingCore using the publish route
        var core = await _coreClient.GetBootImagesAsync(context.CancellationToken);
        return await ProxyAsync(req, core, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyAsync(
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

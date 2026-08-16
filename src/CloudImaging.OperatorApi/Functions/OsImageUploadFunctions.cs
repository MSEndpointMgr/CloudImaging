using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// OS image upload proxy endpoints (mirrors BootImageUploadFunctions).
/// POST /api/images/upload/start             → ImagingCore start upload
/// POST /api/images/upload/{uploadId}/publish → ImagingCore commit + validate + publish
/// </summary>
public sealed partial class OsImageUploadFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<OsImageUploadFunctions> _logger;

    public OsImageUploadFunctions(ImagingCoreClient coreClient, ILogger<OsImageUploadFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("StartOsImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var core = await _coreClient.StartOsImageUploadAsync(payload!, context.CancellationToken);
        return await ProxyAsync(req, core, context.CancellationToken);
    }

    [Function("PublishOsImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "images/upload/{uploadId}/publish")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var core = await _coreClient.PublishOsImageUploadAsync(uploadId, payload!, context.CancellationToken);
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

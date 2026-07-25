using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Branding proxy endpoints (T093, FR-038).
/// GET /api/branding, PUT /api/branding, GET /api/branding/logo/sas.
/// </summary>
public sealed partial class BrandingFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BrandingFunctions> _logger;

    public BrandingFunctions(ImagingCoreClient coreClient, ILogger<BrandingFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetBranding")]
    public async Task<HttpResponseData> GetBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.GetBrandingAsync(context.CancellationToken), context.CancellationToken);

    [Function("PutBranding")]
    public async Task<HttpResponseData> PutBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UpdateBrandingAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    [Function("GetBrandingLogoSas")]
    public async Task<HttpResponseData> GetBrandingLogoSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/logo/sas")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.GetBrandingLogoSasAsync(context.CancellationToken), context.CancellationToken);

    [Function("UploadBrandingLogo")]
    public async Task<HttpResponseData> UploadBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding/logo")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UploadBrandingLogoAsync(payload!, context.CancellationToken), context.CancellationToken);
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

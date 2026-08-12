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

    [Function("UploadBrandingPortalLogo")]
    public async Task<HttpResponseData> UploadBrandingPortalLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding/portal-logo")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UploadBrandingPortalLogoAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    [Function("GetBrandingLogoContent")]
    public async Task<HttpResponseData> GetBrandingLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/logo/content")] HttpRequestData req,
        FunctionContext context)
        => await ProxyBinary(req, await _coreClient.GetBrandingLogoContentAsync(context.CancellationToken), context.CancellationToken);

    [Function("GetBrandingPortalLogoContent")]
    public async Task<HttpResponseData> GetBrandingPortalLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/portal-logo/content")] HttpRequestData req,
        FunctionContext context)
        => await ProxyBinary(req, await _coreClient.GetBrandingPortalLogoContentAsync(context.CancellationToken), context.CancellationToken);

    [Function("DeleteBrandingLogo")]
    public async Task<HttpResponseData> DeleteBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "branding/logo")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.DeleteBrandingLogoAsync(context.CancellationToken), context.CancellationToken);

    [Function("DeleteBrandingPortalLogo")]
    public async Task<HttpResponseData> DeleteBrandingPortalLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "branding/portal-logo")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.DeleteBrandingPortalLogoAsync(context.CancellationToken), context.CancellationToken);

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

    /// <summary>Streams a binary (image) response from the Core API through unchanged, preserving content-type.</summary>
    private static async Task<HttpResponseData> ProxyBinary(
        HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.IsSuccessStatusCode)
        {
            response.Headers.Add("Content-Type", coreResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
            var cacheControl = coreResponse.Content.Headers.TryGetValues("Cache-Control", out var values)
                ? string.Join(", ", values)
                : "no-cache";
            response.Headers.Add("Cache-Control", cacheControl);
            await response.WriteBytesAsync(await coreResponse.Content.ReadAsByteArrayAsync(ct), ct);
        }
        return response;
    }
}

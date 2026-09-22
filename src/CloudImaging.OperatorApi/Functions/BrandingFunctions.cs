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

    /// <summary>Initializes a new instance of the <see cref="BrandingFunctions"/> class.</summary>
    public BrandingFunctions(ImagingCoreClient coreClient, ILogger<BrandingFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>Returns the current branding configuration.</summary>
    [Function("GetBranding")]
    public async Task<HttpResponseData> GetBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.GetBrandingAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>Updates the branding configuration.</summary>
    [Function("PutBranding")]
    public async Task<HttpResponseData> PutBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UpdateBrandingAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>Requests a SAS URL for uploading the branding logo.</summary>
    [Function("GetBrandingLogoSas")]
    public async Task<HttpResponseData> GetBrandingLogoSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/logo/sas")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.GetBrandingLogoSasAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>Uploads the branding logo.</summary>
    [Function("UploadBrandingLogo")]
    public async Task<HttpResponseData> UploadBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding/logo")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UploadBrandingLogoAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>Uploads the portal branding logo.</summary>
    [Function("UploadBrandingPortalLogo")]
    public async Task<HttpResponseData> UploadBrandingPortalLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "branding/portal-logo")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        return await Proxy(req, await _coreClient.UploadBrandingPortalLogoAsync(payload!, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>Returns the branding logo image content.</summary>
    [Function("GetBrandingLogoContent")]
    public async Task<HttpResponseData> GetBrandingLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/logo/content")] HttpRequestData req,
        FunctionContext context)
        => await ProxyBinary(req, await _coreClient.GetBrandingLogoContentAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>Returns the portal branding logo image content.</summary>
    [Function("GetBrandingPortalLogoContent")]
    public async Task<HttpResponseData> GetBrandingPortalLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "branding/portal-logo/content")] HttpRequestData req,
        FunctionContext context)
        => await ProxyBinary(req, await _coreClient.GetBrandingPortalLogoContentAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>Deletes the branding logo.</summary>
    [Function("DeleteBrandingLogo")]
    public async Task<HttpResponseData> DeleteBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "branding/logo")] HttpRequestData req,
        FunctionContext context)
        => await Proxy(req, await _coreClient.DeleteBrandingLogoAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>Deletes the portal branding logo.</summary>
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

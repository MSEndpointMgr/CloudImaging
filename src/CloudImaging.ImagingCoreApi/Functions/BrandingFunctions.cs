using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Branding get/put and logo SAS endpoints (T092, FR-038).
///
/// GET  /api/internal/branding            — returns current branding config
/// PUT  /api/internal/branding            — replaces branding config
/// GET  /api/internal/branding/logo/sas   — issues a time-limited SAS for the logo blob
/// </summary>
public sealed partial class BrandingFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int LogoSasMinutes = 60;

    private readonly BrandingRepository _brandingRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BrandingFunctions> _logger;

    public BrandingFunctions(
        BrandingRepository brandingRepo,
        BlobServiceClient blobClient,
        ILogger<BrandingFunctions> logger)
    {
        _brandingRepo = brandingRepo;
        _blobClient   = blobClient;
        _logger       = logger;
    }

    [Function("GetBranding")]
    public async Task<HttpResponseData> GetBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/branding")] HttpRequestData req,
        FunctionContext context)
    {
        var branding = await _brandingRepo.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(branding, JsonOptions), context.CancellationToken);
        return response;
    }

    [Function("PutBranding")]
    public async Task<HttpResponseData> PutBranding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/branding")] HttpRequestData req,
        FunctionContext context)
    {
        BrandingConfiguration? payload;
        try { payload = await JsonSerializer.DeserializeAsync<BrandingConfiguration>(req.Body, JsonOptions, context.CancellationToken); }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null) return req.CreateResponse(HttpStatusCode.BadRequest);

        await _brandingRepo.UpsertAsync(payload, context.CancellationToken);
        LogBrandingUpdated(_logger);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [Function("GetBrandingLogoSas")]
    public async Task<HttpResponseData> GetBrandingLogoSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/branding/logo/sas")] HttpRequestData req,
        FunctionContext context)
    {
        var branding = await _brandingRepo.GetAsync(context.CancellationToken);
        if (string.IsNullOrEmpty(branding.LogoBlobPath))
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("No branding logo configured.", context.CancellationToken);
            return notFound;
        }

        var sasUrl   = GenerateSasUrl(branding.LogoBlobPath, TimeSpan.FromMinutes(LogoSasMinutes));
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { sasTokenUrl = sasUrl, expiresAt = DateTimeOffset.UtcNow.AddMinutes(LogoSasMinutes) }),
            context.CancellationToken);
        return response;
    }

    private string GenerateSasUrl(string storagePath, TimeSpan expiry)
    {
        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) return storagePath;
        var container  = storagePath[..slash];
        var blobName   = storagePath[(slash + 1)..];
        var blobClient = _blobClient.GetBlobContainerClient(container).GetBlobClient(blobName);
        if (!blobClient.CanGenerateSasUri) return blobClient.Uri.ToString();
        var builder    = new BlobSasBuilder { BlobContainerName = container, BlobName = blobName, Resource = "b", ExpiresOn = DateTimeOffset.UtcNow + expiry };
        builder.SetPermissions(BlobSasPermissions.Read);
        return blobClient.GenerateSasUri(builder).ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding configuration updated.")]
    private static partial void LogBrandingUpdated(ILogger logger);
}

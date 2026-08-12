using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
    private const string LogoContainer = "branding";
    private const int MaxLogoBytes = 1024 * 1024; // 1 MB decoded

    private readonly BrandingRepository _brandingRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BrandingFunctions> _logger;

    public BrandingFunctions(
        BrandingRepository brandingRepo,
        BlobServiceClient blobClient,
        ILogger<BrandingFunctions> logger)
    {
        _brandingRepo = brandingRepo;
        _blobClient = blobClient;
        _logger = logger;
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

        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Logo paths are owned by the dedicated upload endpoints — preserve them here so a
        // colour/name save cannot accidentally clear a configured logo.
        var existing = await _brandingRepo.GetAsync(context.CancellationToken);
        var merged = new BrandingConfiguration
        {
            LogoBlobPath = existing.LogoBlobPath,
            PortalLogoBlobPath = existing.PortalLogoBlobPath,
            PrimaryColor = payload.PrimaryColor,
            AccentColor = payload.AccentColor,
            ApplicationName = payload.ApplicationName,
        };
        await _brandingRepo.UpsertAsync(merged, context.CancellationToken);
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

        var sasUrl = GenerateSasUrl(branding.LogoBlobPath, TimeSpan.FromMinutes(LogoSasMinutes));
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { sasTokenUrl = sasUrl, expiresAt = DateTimeOffset.UtcNow.AddMinutes(LogoSasMinutes) }),
            context.CancellationToken);
        return response;
    }

    [Function("UploadBrandingLogo")]
    public Task<HttpResponseData> UploadBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/branding/logo")] HttpRequestData req,
        FunctionContext context)
        => ProcessLogoUploadAsync(req, isPortal: false, context.CancellationToken);

    [Function("UploadBrandingPortalLogo")]
    public Task<HttpResponseData> UploadBrandingPortalLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/branding/portal-logo")] HttpRequestData req,
        FunctionContext context)
        => ProcessLogoUploadAsync(req, isPortal: true, context.CancellationToken);

    /// <summary>Streams the boot image logo bytes directly (managed identity read, no SAS).</summary>
    [Function("GetBrandingLogoContent")]
    public Task<HttpResponseData> GetBrandingLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/branding/logo/content")] HttpRequestData req,
        FunctionContext context)
        => StreamLogoAsync(req, isPortal: false, context.CancellationToken);

    /// <summary>Streams the portal logo bytes directly (managed identity read, no SAS).</summary>
    [Function("GetBrandingPortalLogoContent")]
    public Task<HttpResponseData> GetBrandingPortalLogoContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/branding/portal-logo/content")] HttpRequestData req,
        FunctionContext context)
        => StreamLogoAsync(req, isPortal: true, context.CancellationToken);

    /// <summary>Clears the boot image logo, reverting the boot media to the built-in default artwork.</summary>
    [Function("DeleteBrandingLogo")]
    public Task<HttpResponseData> DeleteBrandingLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/branding/logo")] HttpRequestData req,
        FunctionContext context)
        => ResetLogoAsync(req, isPortal: false, context.CancellationToken);

    /// <summary>Clears the portal logo, reverting the portal UI to the built-in default artwork.</summary>
    [Function("DeleteBrandingPortalLogo")]
    public Task<HttpResponseData> DeleteBrandingPortalLogo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/branding/portal-logo")] HttpRequestData req,
        FunctionContext context)
        => ResetLogoAsync(req, isPortal: true, context.CancellationToken);

    private async Task<HttpResponseData> ResetLogoAsync(HttpRequestData req, bool isPortal, CancellationToken ct)
    {
        var branding = await _brandingRepo.GetAsync(ct);
        await TryDeleteBlobAsync(isPortal ? branding.PortalLogoBlobPath : branding.LogoBlobPath, ct);

        var updated = new BrandingConfiguration
        {
            LogoBlobPath = isPortal ? branding.LogoBlobPath : null,
            PortalLogoBlobPath = isPortal ? null : branding.PortalLogoBlobPath,
            PrimaryColor = branding.PrimaryColor,
            AccentColor = branding.AccentColor,
            ApplicationName = branding.ApplicationName,
        };
        await _brandingRepo.UpsertAsync(updated, ct);
        LogBrandingUpdated(_logger);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(updated, JsonOptions), ct);
        return response;
    }

    private async Task<HttpResponseData> StreamLogoAsync(HttpRequestData req, bool isPortal, CancellationToken ct)
    {
        var branding = await _brandingRepo.GetAsync(ct);
        var storagePath = isPortal ? branding.PortalLogoBlobPath : branding.LogoBlobPath;
        if (string.IsNullOrEmpty(storagePath))
        {
            return await Text(req, HttpStatusCode.NotFound, "No logo configured.", ct);
        }

        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return await Text(req, HttpStatusCode.NotFound, "Logo path is malformed.", ct);
        }

        var blob = _blobClient.GetBlobContainerClient(storagePath[..slash]).GetBlobClient(storagePath[(slash + 1)..]);
        try
        {
            var result = await blob.DownloadContentAsync(ct);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", result.Value.Details.ContentType ?? "image/png");
            response.Headers.Add("Cache-Control", "no-cache");
            await response.WriteBytesAsync(result.Value.Content.ToArray(), ct);
            return response;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return await Text(req, HttpStatusCode.NotFound, "Logo blob not found.", ct);
        }
    }

    private async Task<HttpResponseData> ProcessLogoUploadAsync(HttpRequestData req, bool isPortal, CancellationToken ct)
    {
        LogoUploadRequest? payload;
        try { payload = await JsonSerializer.DeserializeAsync<LogoUploadRequest>(req.Body, JsonOptions, ct); }
        catch (JsonException) { return await Text(req, HttpStatusCode.BadRequest, "Request body is not valid JSON.", ct); }

        if (payload is null || string.IsNullOrWhiteSpace(payload.DataBase64))
        {
            return await Text(req, HttpStatusCode.BadRequest, "A base64-encoded logo image is required.", ct);
        }

        var contentType = string.IsNullOrWhiteSpace(payload.ContentType) ? "image/png" : payload.ContentType.Trim();
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return await Text(req, HttpStatusCode.BadRequest, "Only image content types are accepted for the logo.", ct);
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(StripDataUriPrefix(payload.DataBase64)); }
        catch (FormatException) { return await Text(req, HttpStatusCode.BadRequest, "Logo data is not valid base64.", ct); }

        if (bytes.Length == 0)
        {
            return await Text(req, HttpStatusCode.BadRequest, "Logo image is empty.", ct);
        }

        if (bytes.Length > MaxLogoBytes)
        {
            return await Text(req, HttpStatusCode.RequestEntityTooLarge, "Logo image exceeds the 1 MB limit.", ct);
        }

        var branding = await _brandingRepo.GetAsync(ct);
        var extension = ResolveExtension(contentType, payload.FileName);
        var prefix = isPortal ? "portal-logo" : "logo";
        var blobName = $"{prefix}/{Guid.NewGuid():N}.{extension}";
        var container = _blobClient.GetBlobContainerClient(LogoContainer);
        var blob = container.GetBlobClient(blobName);

        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await blob.UploadAsync(
                stream,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
                ct);
        }

        // Best-effort removal of the previously configured logo blob for this scenario.
        await TryDeleteBlobAsync(isPortal ? branding.PortalLogoBlobPath : branding.LogoBlobPath, ct);

        var newPath = $"{LogoContainer}/{blobName}";
        var updated = new BrandingConfiguration
        {
            LogoBlobPath = isPortal ? branding.LogoBlobPath : newPath,
            PortalLogoBlobPath = isPortal ? newPath : branding.PortalLogoBlobPath,
            PrimaryColor = branding.PrimaryColor,
            AccentColor = branding.AccentColor,
            ApplicationName = branding.ApplicationName,
        };
        await _brandingRepo.UpsertAsync(updated, ct);
        LogBrandingUpdated(_logger);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(updated, JsonOptions), ct);
        return response;
    }

    private async Task TryDeleteBlobAsync(string? storagePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(storagePath))
        {
            return;
        }

        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return;
        }

        try
        {
            var container = _blobClient.GetBlobContainerClient(storagePath[..slash]);
            await container.GetBlobClient(storagePath[(slash + 1)..]).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (RequestFailedException)
        {
            // Ignore cleanup failures — the new logo is already committed.
        }
    }

    private static string StripDataUriPrefix(string data)
    {
        var comma = data.IndexOf(',', StringComparison.Ordinal);
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? data[(comma + 1)..]
            : data;
    }

    private static string ResolveExtension(string contentType, string? fileName)
    {
        var ext = contentType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
            _ => string.Empty,
        };
        if (!string.IsNullOrEmpty(ext))
        {
            return ext;
        }

        var fromName = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(fromName) ? "png" : fromName;
    }

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode status, string message, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        await response.WriteStringAsync(message, ct);
        return response;
    }

    private string GenerateSasUrl(string storagePath, TimeSpan expiry)
    {
        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return storagePath;
        }

        var container = storagePath[..slash];
        var blobName = storagePath[(slash + 1)..];
        var blobClient = _blobClient.GetBlobContainerClient(container).GetBlobClient(blobName);
        if (!blobClient.CanGenerateSasUri)
        {
            return blobClient.Uri.ToString();
        }

        var builder = new BlobSasBuilder { BlobContainerName = container, BlobName = blobName, Resource = "b", ExpiresOn = DateTimeOffset.UtcNow + expiry };
        builder.SetPermissions(BlobSasPermissions.Read);
        return blobClient.GenerateSasUri(builder).ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding configuration updated.")]
    private static partial void LogBrandingUpdated(ILogger logger);
}

/// <summary>Payload for a branding logo upload (base64-encoded image).</summary>
public sealed record LogoUploadRequest(string? FileName, string? ContentType, string DataBase64);

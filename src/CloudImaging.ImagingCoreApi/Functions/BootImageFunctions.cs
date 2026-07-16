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
/// Boot image query and SAS issuance endpoints (T066, FR-053, FR-056).
///
/// GET  /api/internal/boot-images         — lists active boot images
/// GET  /api/internal/boot-images/{id}    — gets a single boot image
/// POST /api/internal/boot-images/{id}/sas — issues a time-limited SAS URL + sha256Hash
/// </summary>
public sealed partial class BootImageFunctions
{
    private readonly BootImageRepository _bootImageRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BootImageFunctions> _logger;

    public BootImageFunctions(
        BootImageRepository bootImageRepo,
        PortalConfigurationRepository configRepo,
        BlobServiceClient blobClient,
        ILogger<BootImageFunctions> logger)
    {
        _bootImageRepo = bootImageRepo;
        _configRepo    = configRepo;
        _blobClient    = blobClient;
        _logger        = logger;
    }

    // ── GET /api/internal/boot-images ────────────────────────────────────────

    [Function("GetBootImages")]
    public async Task<HttpResponseData> GetBootImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/boot-images")] HttpRequestData req,
        FunctionContext context)
    {
        var activeImages = await _bootImageRepo.ListActiveAsync(context.CancellationToken).ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(activeImages.Select(MapToDto)),
            context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/boot-images/{id} ───────────────────────────────────

    [Function("GetBootImageById")]
    public async Task<HttpResponseData> GetBootImageById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/boot-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var image = await _bootImageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(MapToDto(image)), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/boot-images/{id}/sas ──────────────────────────────

    [Function("GetBootImageSasUrl")]
    public async Task<HttpResponseData> GetBootImageSasUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/{id}/sas")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var image = await _bootImageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var config    = await _configRepo.GetAsync(context.CancellationToken);
        var sasExpiry = TimeSpan.FromMinutes(
            config.BootImageSasExpiryMinutes > 0 ? config.BootImageSasExpiryMinutes : 60);

        var sasUrl = GenerateSasUrl(image.StoragePath, sasExpiry);

        LogSasIssued(_logger, imageId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            sasTokenUrl = sasUrl,
            expiresAt   = DateTimeOffset.UtcNow + sasExpiry,
            sha256Hash  = image.Sha256Hash,
        }), context.CancellationToken);
        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GenerateSasUrl(string storagePath, TimeSpan expiry)
    {
        try
        {
            var slash  = storagePath.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0) return storagePath;
            var container  = storagePath[..slash];
            var blobName   = storagePath[(slash + 1)..];
            var blobClient = _blobClient.GetBlobContainerClient(container).GetBlobClient(blobName);
            if (!blobClient.CanGenerateSasUri) return blobClient.Uri.ToString();
            var builder = new BlobSasBuilder
            {
                BlobContainerName = container,
                BlobName          = blobName,
                Resource          = "b",
                ExpiresOn         = DateTimeOffset.UtcNow + expiry,
            };
            builder.SetPermissions(BlobSasPermissions.Read);
            return blobClient.GenerateSasUri(builder).ToString();
        }
        catch { return storagePath; }
    }

    private static object MapToDto(BootImage b) => new
    {
        bootImageId      = b.BootImageId,
        version          = b.Version,
        createdAt        = b.CreatedAt,
        sizeBytes        = b.SizeBytes,
        storagePath      = b.StoragePath,
        manifestVersion  = b.ManifestVersion,
        sha256Hash       = b.Sha256Hash,
        isLatestPublished= b.IsLatestPublished,
        isActive         = b.IsActive,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image SAS issued for {BootImageId}.")]
    private static partial void LogSasIssued(ILogger logger, Guid bootImageId);
}

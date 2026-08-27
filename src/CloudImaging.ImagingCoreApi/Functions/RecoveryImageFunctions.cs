using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Recovery (WinRE) image query and SAS issuance endpoints, mirroring <see cref="BootImageFunctions"/>.
///
/// GET  /api/internal/recovery-images          — lists active recovery images
/// GET  /api/internal/recovery-images/{id}     — gets a single recovery image
/// POST /api/internal/recovery-images/{id}/sas — issues a time-limited SAS URL + sha256Hash
/// </summary>
public sealed partial class RecoveryImageFunctions
{
    private readonly RecoveryImageRepository _recoveryImageRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<RecoveryImageFunctions> _logger;

    public RecoveryImageFunctions(
        RecoveryImageRepository recoveryImageRepo,
        PortalConfigurationRepository configRepo,
        BlobServiceClient blobClient,
        ILogger<RecoveryImageFunctions> logger)
    {
        _recoveryImageRepo = recoveryImageRepo;
        _configRepo = configRepo;
        _blobClient = blobClient;
        _logger = logger;
    }

    // ── GET /api/internal/recovery-images ────────────────────────────────────

    [Function("GetRecoveryImages")]
    public async Task<HttpResponseData> GetRecoveryImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/recovery-images")] HttpRequestData req,
        FunctionContext context)
    {
        var activeImages = await _recoveryImageRepo.ListActiveAsync(context.CancellationToken).ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(activeImages.Select(MapToDto)),
            context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/recovery-images/{id} ───────────────────────────────

    [Function("GetRecoveryImageById")]
    public async Task<HttpResponseData> GetRecoveryImageById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/recovery-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var image = await _recoveryImageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(MapToDto(image)), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/recovery-images/{id}/sas ──────────────────────────

    [Function("GetRecoveryImageSasUrl")]
    public async Task<HttpResponseData> GetRecoveryImageSasUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/recovery-images/{id}/sas")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var image = await _recoveryImageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var config = await _configRepo.GetAsync(context.CancellationToken);
        var sasExpiry = TimeSpan.FromMinutes(
            config.BootImageSasExpiryMinutes > 0 ? config.BootImageSasExpiryMinutes : 120);

        string sasUrl;
        try
        {
            sasUrl = await GenerateSasUrlAsync(image.StoragePath, sasExpiry, context.CancellationToken);
        }
        catch (Exception ex)
        {
            LogSasGenerationFailed(_logger, imageId, ex);
            var failure = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await failure.WriteStringAsync(
                "Recovery image download URL is temporarily unavailable. Please retry.", context.CancellationToken);
            return failure;
        }

        LogSasIssued(_logger, imageId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            sasTokenUrl = sasUrl,
            expiresAt = DateTimeOffset.UtcNow + sasExpiry,
            sha256Hash = image.Sha256Hash,
        }), context.CancellationToken);
        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerateSasUrlAsync(string storagePath, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            throw new InvalidOperationException(
                $"Recovery image storage path '{storagePath}' is malformed — expected '<container>/<blobName>'.");
        }

        var container = storagePath[..slash];
        var blobName = storagePath[(slash + 1)..];
        return await BlobSasUrlGenerator.GenerateAsync(
            _blobClient, container, blobName, BlobSasPermissions.Read, expiry, cancellationToken);
    }

    private static object MapToDto(RecoveryImage i) => new
    {
        recoveryImageId = i.RecoveryImageId,
        version = i.Version,
        description = i.Description,
        createdAt = i.UploadedAt,
        sizeBytes = i.SizeBytes,
        storagePath = i.StoragePath,
        sha256Hash = i.Sha256Hash,
        isLatestPublished = i.IsLatestPublished,
        isActive = i.IsActive,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovery image SAS issued for {RecoveryImageId}.")]
    private static partial void LogSasIssued(ILogger logger, Guid recoveryImageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovery image SAS generation failed for {RecoveryImageId}.")]
    private static partial void LogSasGenerationFailed(ILogger logger, Guid recoveryImageId, Exception ex);
}

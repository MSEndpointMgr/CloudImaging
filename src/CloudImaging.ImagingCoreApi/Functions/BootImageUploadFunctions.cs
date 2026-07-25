using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Staged boot image upload and publish-commit endpoints (T125, T125a, FR-063).
///
/// POST   /api/internal/boot-images/upload/start      — start a staged upload session; returns blobUploadUrl
/// POST   /api/internal/boot-images/upload/{token}/publish — validate SHA-256 and publish
/// </summary>
public sealed partial class BootImageUploadFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UploadContainer = "boot-images";

    private readonly BootImageRepository _bootImageRepo;
    private readonly BootImageValidationService _validator;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BootImageUploadFunctions> _logger;

    public BootImageUploadFunctions(
        BootImageRepository bootImageRepo,
        BootImageValidationService validator,
        BlobServiceClient blobClient,
        ILogger<BootImageUploadFunctions> logger)
    {
        _bootImageRepo = bootImageRepo;
        _validator = validator;
        _blobClient = blobClient;
        _logger = logger;
    }

    // ── POST /api/internal/boot-images/upload/start ───────────────────────────

    [Function("StartBootImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var version = versionProp.GetString() ?? string.Empty;
        var sha256Hash = hashProp.GetString() ?? string.Empty;
        var uploadId = Guid.NewGuid().ToString("N");
        var blobName = $"uploads/{uploadId}/{version.Replace(' ', '-')}.wim";

        // Generate a SAS URL with Write permission for the client to upload directly to Blob Storage
        var containerClient = _blobClient.GetBlobContainerClient(UploadContainer);
        var blobClient = containerClient.GetBlobClient(blobName);

        string uploadUrl;
        if (blobClient.CanGenerateSasUri)
        {
            var builder = new Azure.Storage.Sas.BlobSasBuilder
            {
                BlobContainerName = UploadContainer,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(4),
            };
            builder.SetPermissions(Azure.Storage.Sas.BlobSasPermissions.Create | Azure.Storage.Sas.BlobSasPermissions.Write);
            uploadUrl = blobClient.GenerateSasUri(builder).ToString();
        }
        else
        {
            uploadUrl = blobClient.Uri.ToString();
        }

        LogUploadStarted(_logger, uploadId, version);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            uploadId,
            blobName,
            uploadUrl,
            sha256Hash,
            expiresAt = DateTimeOffset.UtcNow.AddHours(4),
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/boot-images/upload/{token}/publish ─────────────────

    [Function("PublishBootImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/upload/{token}/publish")] HttpRequestData req,
        string token,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("blobName", out var blobNameProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || !body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sizeBytes", out var sizeProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blobName = blobNameProp.GetString()!;
        var sha256Hash = hashProp.GetString()!;
        var version = versionProp.GetString()!;
        var sizeBytes = sizeProp.GetInt64();

        // Download blob and validate SHA-256 (T125a)
        var blobClient = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(blobName);
        var download = await blobClient.OpenReadAsync(cancellationToken: context.CancellationToken);
        var validation = await _validator.ValidateAsync(download, sha256Hash, context.CancellationToken);

        if (!validation.Valid)
        {
            LogValidationFailed(_logger, blobName, validation.FailureReason ?? "unknown");
            // Clean up the invalid upload blob
            await blobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
            var bad = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await bad.WriteStringAsync(validation.FailureReason ?? "Checksum validation failed.", context.CancellationToken);
            return bad;
        }

        // Atomically publish: move blob to final path and create catalog entry
        var finalBlobName = $"published/{Guid.NewGuid():N}/{version.Replace(' ', '-')}.wim";
        var finalBlob = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(finalBlobName);
        await finalBlob.StartCopyFromUriAsync(blobClient.Uri, cancellationToken: context.CancellationToken);
        await blobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);

        var image = new BootImage
        {
            BootImageId = Guid.NewGuid(),
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = sizeBytes,
            StoragePath = $"{UploadContainer}/{finalBlobName}",
            ManifestVersion = "1.0",
            Sha256Hash = validation.ActualHash!,
        };

        var published = await _bootImageRepo.PublishAsync(image, context.CancellationToken);
        LogPublished(_logger, published.BootImageId, version);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(published, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image upload started: id={UploadId} version={Version}.")]
    private static partial void LogUploadStarted(ILogger logger, string uploadId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image validation failed for {BlobName}: {Reason}.")]
    private static partial void LogValidationFailed(ILogger logger, string blobName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image published: {BootImageId} v{Version}.")]
    private static partial void LogPublished(ILogger logger, Guid bootImageId, string version);
}

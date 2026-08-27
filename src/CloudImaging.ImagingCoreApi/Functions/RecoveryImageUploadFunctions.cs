using System.IO;
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
/// Staged, direct-to-blob upload for recovery (WinRE) images, mirroring
/// <see cref="BootImageUploadFunctions"/> (recovery images are boot-media-sized, not multi-GB
/// like OS images, so a single whole-file PUT is used rather than OsImageUploadFunctions'
/// block-staged chunked upload).
///
/// POST /api/internal/recovery-images/upload/start           — start a staged upload; returns a
///   write SAS URL for a single direct-to-blob PUT from the browser.
/// POST /api/internal/recovery-images/upload/{uploadId}/publish — validates the uploaded blob's
///   SHA-256 and file signature, then publishes the recovery image catalog entry (auto-promoted
///   to isLatestPublished, mirroring the boot image convention).
/// </summary>
public sealed partial class RecoveryImageUploadFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UploadContainer = "recovery-images";

    private readonly RecoveryImageRepository _recoveryImageRepo;
    private readonly BootImageValidationService _validator;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<RecoveryImageUploadFunctions> _logger;

    public RecoveryImageUploadFunctions(
        RecoveryImageRepository recoveryImageRepo,
        BootImageValidationService validator,
        BlobServiceClient blobClient,
        ILogger<RecoveryImageUploadFunctions> logger)
    {
        _recoveryImageRepo = recoveryImageRepo;
        _validator = validator;
        _blobClient = blobClient;
        _logger = logger;
    }

    // ── POST /api/internal/recovery-images/upload/start ──────────────────────

    [Function("StartRecoveryImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/recovery-images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || !body.RootElement.TryGetProperty("fileName", out var fileNameProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var version = versionProp.GetString() ?? string.Empty;
        var sha256Hash = hashProp.GetString() ?? string.Empty;
        var fileName = fileNameProp.GetString() ?? string.Empty;
        var extension = Path.GetExtension(fileName);

        if (!BootImageValidationService.IsAllowedExtension(extension))
        {
            LogUnsupportedExtension(_logger, extension);
            var rejected = req.CreateResponse(HttpStatusCode.BadRequest);
            await rejected.WriteStringAsync(
                $"Unsupported file extension '{extension}'. Only {string.Join(", ", BootImageValidationService.AllowedExtensions)} are allowed.",
                context.CancellationToken);
            return rejected;
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var blobName = $"uploads/{uploadId}/winre-{version.Replace(' ', '-')}{extension}";

        // Single write SAS for one whole-file PUT (BlobSasPermissions.Create|Write, not Add) —
        // matches how the client (recoveryImageUploadService.ts) actually uploads: a single
        // XHR PUT of the entire file, the same as Boot Images, not a chunked block-staged upload.
        var uploadUrl = await BlobSasUrlGenerator.GenerateAsync(
            _blobClient,
            UploadContainer,
            blobName,
            BlobSasPermissions.Create | BlobSasPermissions.Write,
            TimeSpan.FromHours(4),
            context.CancellationToken);

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

    // ── POST /api/internal/recovery-images/upload/{uploadId}/publish ─────────

    [Function("PublishRecoveryImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/recovery-images/upload/{uploadId}/publish")] HttpRequestData req,
        string uploadId,
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
        var description = body.RootElement.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
        var extension = Path.GetExtension(blobName);

        // The client uploaded the whole file in a single PUT (see StartUpload), so there is no
        // block list to commit here — just validate the already-complete blob directly, mirroring
        // BootImageUploadFunctions.PublishUpload.
        var blobClient = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(blobName);
        var validation = await _validator.ValidateAsync(blobClient, sha256Hash, extension, context.CancellationToken);

        if (!validation.Valid)
        {
            LogValidationFailed(_logger, blobName, validation.FailureReason ?? "unknown");
            await blobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
            var bad = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await bad.WriteStringAsync(validation.FailureReason ?? "Checksum validation failed.", context.CancellationToken);
            return bad;
        }

        var finalBlobName = $"published/{Guid.NewGuid():N}/winre-{version.Replace(' ', '-')}{extension}";
        var finalBlob = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(finalBlobName);
        await finalBlob.StartCopyFromUriAsync(blobClient.Uri, cancellationToken: context.CancellationToken);
        await blobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);

        var image = await _recoveryImageRepo.PublishAsync(new RecoveryImage
        {
            RecoveryImageId = Guid.NewGuid(),
            Version = version,
            Description = description,
            SizeBytes = sizeBytes,
            StoragePath = $"{UploadContainer}/{finalBlobName}",
            Sha256Hash = validation.ActualHash!,
        }, context.CancellationToken);

        LogPublished(_logger, image.RecoveryImageId, version);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(image, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── DELETE /api/internal/recovery-images/{id} ─────────────────────────────

    [Function("DeleteRecoveryImage")]
    public async Task<HttpResponseData> DeleteRecoveryImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/recovery-images/{id}")] HttpRequestData req,
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

        if (image.IsLatestPublished)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync(
                "Cannot delete the currently published recovery image. Publish a replacement first.",
                context.CancellationToken);
            return conflict;
        }

        await _recoveryImageRepo.DeleteAsync(imageId, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovery image upload started: id={UploadId} version={Version}.")]
    private static partial void LogUploadStarted(ILogger logger, string uploadId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery image validation failed for {BlobName}: {Reason}.")]
    private static partial void LogValidationFailed(ILogger logger, string blobName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovery image published: {RecoveryImageId} v{Version}.")]
    private static partial void LogPublished(ILogger logger, Guid recoveryImageId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery image upload rejected: unsupported extension '{Extension}'.")]
    private static partial void LogUnsupportedExtension(ILogger logger, string extension);
}

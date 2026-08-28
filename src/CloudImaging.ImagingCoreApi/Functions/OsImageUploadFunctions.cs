using System.IO;
using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Staged, direct-to-blob chunked upload for large OS images (5–10 GB), mirroring the boot image
/// staged-upload pattern (BootImageUploadFunctions) rather than proxying bytes through any App
/// Service/Function tier. Replaces the previous portal-server "chunked-upload" stub, which never
/// forwarded block bytes to Azure Storage at all and never registered the resulting catalog entry.
///
/// POST /api/internal/images/upload/start           — start a staged upload; returns a single
///   write SAS URL valid for repeated "stage block" calls directly from the browser.
/// POST /api/internal/images/upload/{uploadId}/publish — commits the block list server-side
///   (no SAS needed — uses this Function's own managed identity), validates SHA-256, and
///   registers the OS image catalog entry.
/// </summary>
public sealed partial class OsImageUploadFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UploadContainer = "os-images";

    private readonly OsImageRepository _imageRepo;
    private readonly BootImageValidationService _validator;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<OsImageUploadFunctions> _logger;

    public OsImageUploadFunctions(
        OsImageRepository imageRepo,
        BootImageValidationService validator,
        BlobServiceClient blobClient,
        ILogger<OsImageUploadFunctions> logger)
    {
        _imageRepo = imageRepo;
        _validator = validator;
        _blobClient = blobClient;
        _logger = logger;
    }

    // ── POST /api/internal/images/upload/start ────────────────────────────────

    [Function("StartOsImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("name", out var nameProp)
            || !body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var name = nameProp.GetString() ?? string.Empty;
        var version = versionProp.GetString() ?? string.Empty;
        var sha256Hash = hashProp.GetString() ?? string.Empty;
        var extension = Path.GetExtension(name);

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
        var baseName = Path.GetFileNameWithoutExtension(name).Replace(' ', '-');
        var blobName = $"uploads/{uploadId}/{baseName}-{version.Replace(' ', '-')}{extension}";

        // A single blob-level SAS with Create+Write covers every "stage block" call plus the
        // final "commit block list" call, so the browser can upload all blocks directly to Blob
        // Storage without round-tripping through any API tier for the bytes themselves.
        var uploadUrl = await BlobSasUrlGenerator.GenerateAsync(
            _blobClient,
            UploadContainer,
            blobName,
            BlobSasPermissions.Create | BlobSasPermissions.Write,
            TimeSpan.FromHours(8),
            context.CancellationToken);

        LogUploadStarted(_logger, uploadId, name, version);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            uploadId,
            blobName,
            uploadUrl,
            blockSize = 4 * 1024 * 1024,
            expiresAt = DateTimeOffset.UtcNow.AddHours(8),
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/images/upload/{uploadId}/publish ───────────────────

    [Function("PublishOsImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/images/upload/{uploadId}/publish")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("blobName", out var blobNameProp)
            || !body.RootElement.TryGetProperty("blockIds", out var blockIdsProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || !body.RootElement.TryGetProperty("name", out var nameProp)
            || !body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sizeBytes", out var sizeProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blobName = blobNameProp.GetString()!;
        var blockIds = blockIdsProp.EnumerateArray().Select(e => e.GetString()!).ToList();
        var sha256Hash = hashProp.GetString()!;
        var name = nameProp.GetString()!;
        var version = versionProp.GetString()!;
        var sizeBytes = sizeProp.GetInt64();

        // Reject before committing any blocks so the caller isn't left holding an orphaned blob.
        var activeCount = await _imageRepo.GetActiveCountAsync(context.CancellationToken);
        if (activeCount >= OsImageRepository.MaxActiveEntries)
        {
            LogCatalogAtCapacity(_logger, activeCount);
            var full = req.CreateResponse(HttpStatusCode.Conflict);
            await full.WriteStringAsync(
                $"OS image catalog is at capacity ({OsImageRepository.MaxActiveEntries}). Remove an unused image before uploading another.",
                context.CancellationToken);
            return full;
        }

        var blockBlobClient = _blobClient.GetBlobContainerClient(UploadContainer).GetBlockBlobClient(blobName);

        // Commit the staged blocks — this call uses the Function's own managed identity, no SAS
        // required (only the per-block PUTs from the browser needed the SAS).
        await blockBlobClient.CommitBlockListAsync(blockIds, cancellationToken: context.CancellationToken);

        // Validate the file signature (rejects renamed/spoofed files) and SHA-256 (mirrors
        // BootImageUploadFunctions.PublishUpload).
        var extension = Path.GetExtension(blobName);
        var validation = await _validator.ValidateAsync(blockBlobClient, sha256Hash, extension, context.CancellationToken);

        if (!validation.Valid)
        {
            LogValidationFailed(_logger, blobName, validation.FailureReason ?? "unknown");
            await blockBlobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
            var bad = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await bad.WriteStringAsync(validation.FailureReason ?? "Checksum validation failed.", context.CancellationToken);
            return bad;
        }

        // Atomically publish: move blob to final path and create catalog entry
        var finalBlobName = $"published/{Guid.NewGuid():N}/{Path.GetFileNameWithoutExtension(name).Replace(' ', '-')}-{version.Replace(' ', '-')}{extension}";
        var finalBlob = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(finalBlobName);
        await finalBlob.StartCopyFromUriAsync(blockBlobClient.Uri, cancellationToken: context.CancellationToken);
        await blockBlobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);

        var image = new OsImage
        {
            ImageId = Guid.NewGuid(),
            Name = name,
            Version = version,
            SizeBytes = sizeBytes,
            StoragePath = $"{UploadContainer}/{finalBlobName}",
            UploadedAt = DateTimeOffset.UtcNow,
            Sha256Hash = validation.ActualHash!,
        };

        await _imageRepo.CreateAsync(image, context.CancellationToken);
        LogPublished(_logger, image.ImageId, name, version);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(image, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/images/upload/{uploadId}/abandon ───────────────────

    /// <summary>
    /// Deletes the uncommitted staged blob for a cancelled/abandoned upload (T128a). Best-effort
    /// cleanup called by the portal when the operator cancels mid-upload or discards a resumable
    /// checkpoint — without this, staged blocks would otherwise only be reclaimed by Azure
    /// Storage's ~7-day uncommitted-block garbage collection.
    /// </summary>
    [Function("AbandonOsImageUpload")]
    public async Task<HttpResponseData> AbandonUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/images/upload/{uploadId}/abandon")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("blobName", out var blobNameProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blobName = blobNameProp.GetString() ?? string.Empty;
        // Defense-in-depth: only ever delete a blob under this upload's own staging prefix, so a
        // malformed/forged blobName can't be used to delete an unrelated published image blob.
        if (!blobName.StartsWith($"uploads/{uploadId}/", StringComparison.Ordinal))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blockBlobClient = _blobClient.GetBlobContainerClient(UploadContainer).GetBlockBlobClient(blobName);
        await blockBlobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
        LogUploadAbandoned(_logger, uploadId);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image upload started: id={UploadId} name={Name} version={Version}.")]
    private static partial void LogUploadStarted(ILogger logger, string uploadId, string name, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image upload abandoned and staged blob cleaned up: {UploadId}.")]
    private static partial void LogUploadAbandoned(ILogger logger, string uploadId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image validation failed for {BlobName}: {Reason}.")]
    private static partial void LogValidationFailed(ILogger logger, string blobName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image published: {ImageId} {Name} v{Version}.")]
    private static partial void LogPublished(ILogger logger, Guid imageId, string name, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image upload rejected: unsupported extension '{Extension}'.")]
    private static partial void LogUnsupportedExtension(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image upload rejected: catalog at capacity ({ActiveCount} active entries).")]
    private static partial void LogCatalogAtCapacity(ILogger logger, int activeCount);
}

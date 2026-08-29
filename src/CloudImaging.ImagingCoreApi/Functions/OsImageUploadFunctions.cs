using System.IO;
using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Staged, direct-to-blob chunked upload for large OS images (up to 20 GB), mirroring the boot
/// image staged-upload pattern (BootImageUploadFunctions) rather than proxying bytes through any
/// App Service/Function tier. Replaces the previous portal-server "chunked-upload" stub, which
/// never forwarded block bytes to Azure Storage at all and never registered the resulting catalog
/// entry.
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

    /// <summary>
    /// Block size the browser must use when staging. At 8 MB a 20 GB image stages about 2 560
    /// blocks, well inside Blob Storage's 50 000 block ceiling, and halves both the request count
    /// and the size of the block list posted back at publish time compared with 4 MB blocks.
    /// Clients derive their block boundaries from this value, so it must not change while an
    /// upload is in flight; resumable sessions therefore carry it rather than assuming it.
    /// </summary>
    private const int UploadBlockSizeBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Lifetime of the upload SAS. A 20 GB image over a 5 Mbps link takes roughly nine hours, so
    /// the previous eight hour window could expire mid-upload on a slow connection and strand a
    /// nearly complete transfer. The SAS is still scoped to a single blob with Create and Write
    /// only, so a longer window widens exposure very little.
    /// </summary>
    private static readonly TimeSpan UploadSasLifetime = TimeSpan.FromHours(24);

    private readonly OsImageRepository _imageRepo;
    private readonly UploadJobRepository _jobRepo;
    private readonly BootImageValidationService _validator;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<OsImageUploadFunctions> _logger;

    public OsImageUploadFunctions(
        OsImageRepository imageRepo,
        UploadJobRepository jobRepo,
        BootImageValidationService validator,
        BlobServiceClient blobClient,
        ILogger<OsImageUploadFunctions> logger)
    {
        _imageRepo = imageRepo;
        _jobRepo = jobRepo;
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

        if (!BootImageValidationService.IsAllowedExtension(extension, BootImageValidationService.OsImageExtensions))
        {
            LogUnsupportedExtension(_logger, extension);
            var rejected = req.CreateResponse(HttpStatusCode.BadRequest);
            await rejected.WriteStringAsync(
                $"Unsupported file extension '{extension}'. Only {string.Join(", ", BootImageValidationService.OsImageExtensions)} are allowed.",
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
            UploadSasLifetime,
            context.CancellationToken);

        LogUploadStarted(_logger, uploadId, name, version);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            uploadId,
            blobName,
            uploadUrl,
            blockSize = UploadBlockSizeBytes,
            expiresAt = DateTimeOffset.UtcNow.Add(UploadSasLifetime),
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
        // required (only the per-block PUTs from the browser needed the SAS). A resumed upload
        // (browser tab closed/reloaded mid-upload, then re-selected the same file later) replays
        // a client-persisted list of block IDs it believes are already staged — if any of those
        // blocks were never actually committed to Azure Storage, or fell outside the ~7-day
        // uncommitted-block retention window, Azure rejects the whole commit (e.g.
        // InvalidBlockList) rather than partially succeeding, so this can't silently publish a
        // corrupt/incomplete image. Surface that as a clear, actionable error instead of letting
        // an unhandled RequestFailedException fall through to the generic 500 from
        // ProblemDetailsMiddleware, which left the operator unable to tell a stale resume
        // checkpoint apart from any other backend failure.
        try
        {
            await blockBlobClient.CommitBlockListAsync(blockIds, cancellationToken: context.CancellationToken);
        }
        catch (Azure.RequestFailedException ex)
        {
            LogCommitBlockListFailed(_logger, blobName, ex);
            var staleBlocks = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await staleBlocks.WriteStringAsync(
                "One or more previously staged blocks are missing or expired. Discard this upload and start again.",
                context.CancellationToken);
            return staleBlocks;
        }

        // Only the cheap checks run inline. The file-signature check is a single ranged read of
        // the first ~36 KB, so an operator who picked the wrong file still learns about it
        // immediately; the full-file SHA-256, any ISO extraction, and the copy to the published
        // prefix all scale with image size and are handed to the background worker instead.
        var extension = Path.GetExtension(blobName);
        var signature = await _validator.ValidateSignatureAsync(
            blockBlobClient, extension, BootImageValidationService.OsImageExtensions, context.CancellationToken);

        if (!signature.Valid)
        {
            LogValidationFailed(_logger, blobName, signature.FailureReason ?? "unknown");
            await blockBlobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
            var bad = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await bad.WriteStringAsync(signature.FailureReason ?? "File signature validation failed.", context.CancellationToken);
            return bad;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new UploadJob
        {
            UploadId = uploadId,
            Kind = UploadJobKind.OsImage,
            Status = UploadJobStatus.Pending,
            BlobName = blobName,
            Sha256Hash = sha256Hash,
            Version = version,
            Name = name,
            SizeBytes = sizeBytes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var accepted = await _jobRepo.CreateOrGetAsync(job, context.CancellationToken);
        LogPublishAccepted(_logger, uploadId, name, version);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(accepted, JsonOptions), context.CancellationToken);
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
        // A publish may already have been accepted for this upload id, so discard its job too;
        // otherwise the background worker would go looking for a blob that no longer exists.
        await _jobRepo.DeleteAsync(uploadId, context.CancellationToken);
        LogUploadAbandoned(_logger, uploadId);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image upload started: id={UploadId} name={Name} version={Version}.")]
    private static partial void LogUploadStarted(ILogger logger, string uploadId, string name, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image upload abandoned and staged blob cleaned up: {UploadId}.")]
    private static partial void LogUploadAbandoned(ILogger logger, string uploadId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image validation failed for {BlobName}: {Reason}.")]
    private static partial void LogValidationFailed(ILogger logger, string blobName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "OS image publish accepted for background processing: id={UploadId} name={Name} version={Version}.")]
    private static partial void LogPublishAccepted(ILogger logger, string uploadId, string name, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image upload rejected: unsupported extension '{Extension}'.")]
    private static partial void LogUnsupportedExtension(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image upload rejected: catalog at capacity ({ActiveCount} active entries).")]
    private static partial void LogCatalogAtCapacity(ILogger logger, int activeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image upload commit-block-list failed for {BlobName} (likely a stale/expired resume checkpoint).")]
    private static partial void LogCommitBlockListFailed(ILogger logger, string blobName, Exception ex);
}

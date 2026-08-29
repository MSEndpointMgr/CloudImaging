using System.IO;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Performs the slow half of publishing a staged image upload, out of band from the HTTP request
/// that accepted it.
///
/// <para>
/// Everything here scales with image size and therefore cannot run inside a portal request: the
/// full-file SHA-256 re-verification, the ISO to WIM/ESD extraction, and the copy from the staging
/// prefix to the published prefix. See <see cref="UploadJob"/> for the platform limits that force
/// this split. Driven by <c>UploadJobFunctions.ProcessUploadJobs</c>.
/// </para>
/// </summary>
public sealed partial class UploadPublishService
{
    private const string OsImageContainer = "os-images";
    private const string BootImageContainer = "boot-images";
    private const string RecoveryImageContainer = "recovery-images";

    /// <summary>
    /// Read-ahead buffer used when streaming the staged ISO. The SDK default is 4 MB; a larger
    /// window meaningfully cuts the number of ranged GETs needed to walk a 20 GB image.
    /// </summary>
    private const int IsoReadBufferBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Block size used when streaming the extracted image out to its published blob. At 8 MB a
    /// 20 GB extract commits about 2 560 blocks, comfortably inside Blob Storage's 50 000 block
    /// ceiling while keeping the number of round trips low.
    /// </summary>
    private const int BlobWriteBufferBytes = 8 * 1024 * 1024;

    private readonly UploadJobRepository _jobRepo;
    private readonly OsImageRepository _osImageRepo;
    private readonly BootImageRepository _bootImageRepo;
    private readonly RecoveryImageRepository _recoveryImageRepo;
    private readonly BootImageValidationService _validator;
    private readonly IsoExtractionService _isoExtractor;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<UploadPublishService> _logger;

    public UploadPublishService(
        UploadJobRepository jobRepo,
        OsImageRepository osImageRepo,
        BootImageRepository bootImageRepo,
        RecoveryImageRepository recoveryImageRepo,
        BootImageValidationService validator,
        IsoExtractionService isoExtractor,
        BlobServiceClient blobClient,
        ILogger<UploadPublishService> logger)
    {
        _jobRepo = jobRepo;
        _osImageRepo = osImageRepo;
        _bootImageRepo = bootImageRepo;
        _recoveryImageRepo = recoveryImageRepo;
        _validator = validator;
        _isoExtractor = isoExtractor;
        _blobClient = blobClient;
        _logger = logger;
    }

    /// <summary>
    /// Runs a claimed job to a terminal state. Never throws for a job-level problem: a bad hash or
    /// an unreadable blob is recorded on the job as a failure the operator can read in the portal.
    /// Cancellation is allowed to propagate so a host shutdown leaves the job
    /// <see cref="UploadJobStatus.Processing"/> with a lease that will lapse and be retried, rather
    /// than being wrongly marked failed.
    /// </summary>
    public async Task ProcessAsync(UploadJob job, CancellationToken ct)
    {
        try
        {
            switch (job.Kind)
            {
                case UploadJobKind.OsImage:
                    await CompleteOsImageAsync(job, ct);
                    break;
                case UploadJobKind.BootImage:
                    await CompleteBootImageAsync(job, ct);
                    break;
                case UploadJobKind.RecoveryImage:
                    await CompleteRecoveryImageAsync(job, ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogJobFailed(_logger, job.UploadId, ex);
            await _jobRepo.FailAsync(
                job.UploadId,
                "An unexpected error occurred while publishing this image. Discard the upload and try again.",
                ct);
        }
    }

    // ── OS images ─────────────────────────────────────────────────────────────

    private async Task CompleteOsImageAsync(UploadJob job, CancellationToken ct)
    {
        var container = _blobClient.GetBlobContainerClient(OsImageContainer);
        var staged = container.GetBlockBlobClient(job.BlobName);
        var extension = Path.GetExtension(job.BlobName);
        var name = job.Name ?? job.Version;

        var validation = await _validator.ValidateAsync(
            staged, job.Sha256Hash, extension, BootImageValidationService.OsImageExtensions, ct);

        if (!validation.Valid)
        {
            await staged.DeleteIfExistsAsync(cancellationToken: ct);
            await _jobRepo.FailAsync(job.UploadId, validation.FailureReason ?? "Checksum validation failed.", ct);
            return;
        }

        string finalBlobName;
        string finalHash;
        long finalSizeBytes;

        // An uploaded ISO isn't itself usable — the Client's DISM apply step needs a genuine
        // WIM/ESD — so extract sources\install.wim (or install.esd) once here and publish that
        // instead of the raw ISO. Every other extension (.wim/.esd) publishes as-is below.
        if (string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase))
        {
            var isoResult = await ExtractAndPublishIsoAsync(container, staged, name, job.Version, ct);
            await staged.DeleteIfExistsAsync(cancellationToken: ct);

            if (!isoResult.Found)
            {
                await _jobRepo.FailAsync(
                    job.UploadId,
                    isoResult.FailureReason ?? "Could not extract an install image from the uploaded ISO.",
                    ct);
                return;
            }

            finalBlobName = isoResult.BlobName!;
            finalHash = isoResult.Hash!;
            finalSizeBytes = isoResult.SizeBytes;
            LogIsoExtracted(_logger, job.BlobName, isoResult.SourcePath!, finalSizeBytes);
        }
        else
        {
            finalBlobName =
                $"published/{Guid.NewGuid():N}/{Path.GetFileNameWithoutExtension(name).Replace(' ', '-')}-{job.Version.Replace(' ', '-')}{extension}";
            await MoveBlobAsync(container, staged, finalBlobName, ct);
            finalHash = validation.ActualHash!;
            finalSizeBytes = job.SizeBytes;
        }

        // Re-check capacity here as well as at accept time: jobs are processed asynchronously, so
        // several uploads accepted while there was room could otherwise all land and overshoot.
        var activeCount = await _osImageRepo.GetActiveCountAsync(ct);
        if (activeCount >= OsImageRepository.MaxActiveEntries)
        {
            await container.GetBlobClient(finalBlobName).DeleteIfExistsAsync(cancellationToken: ct);
            await _jobRepo.FailAsync(
                job.UploadId,
                $"OS image catalog is at capacity ({OsImageRepository.MaxActiveEntries}). Remove an unused image before uploading another.",
                ct);
            return;
        }

        var image = new OsImage
        {
            ImageId = Guid.NewGuid(),
            Name = name,
            Version = job.Version,
            SizeBytes = finalSizeBytes,
            StoragePath = $"{OsImageContainer}/{finalBlobName}",
            UploadedAt = DateTimeOffset.UtcNow,
            Sha256Hash = finalHash,
        };

        await _osImageRepo.CreateAsync(image, ct);
        await _jobRepo.CompleteAsync(job.UploadId, image.ImageId, ct);
        LogPublished(_logger, job.Kind, image.ImageId, job.Version);
    }

    // ── Boot images ───────────────────────────────────────────────────────────

    private async Task CompleteBootImageAsync(UploadJob job, CancellationToken ct)
    {
        var container = _blobClient.GetBlobContainerClient(BootImageContainer);
        var staged = container.GetBlobClient(job.BlobName);
        var extension = Path.GetExtension(job.BlobName);

        var validation = await _validator.ValidateAsync(
            staged, job.Sha256Hash, extension, BootImageValidationService.WimOnlyExtensions, ct);

        if (!validation.Valid)
        {
            await staged.DeleteIfExistsAsync(cancellationToken: ct);
            await _jobRepo.FailAsync(job.UploadId, validation.FailureReason ?? "Checksum validation failed.", ct);
            return;
        }

        var finalBlobName = $"published/{Guid.NewGuid():N}/{job.Version.Replace(' ', '-')}{extension}";
        await MoveBlobAsync(container, staged, finalBlobName, ct);

        var published = await _bootImageRepo.PublishAsync(new BootImage
        {
            BootImageId = Guid.NewGuid(),
            Version = job.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = job.SizeBytes,
            StoragePath = $"{BootImageContainer}/{finalBlobName}",
            ManifestVersion = "1.0",
            Sha256Hash = validation.ActualHash!,
        }, ct);

        await _jobRepo.CompleteAsync(job.UploadId, published.BootImageId, ct);
        LogPublished(_logger, job.Kind, published.BootImageId, job.Version);
    }

    // ── Recovery images ───────────────────────────────────────────────────────

    private async Task CompleteRecoveryImageAsync(UploadJob job, CancellationToken ct)
    {
        var container = _blobClient.GetBlobContainerClient(RecoveryImageContainer);
        var staged = container.GetBlobClient(job.BlobName);
        var extension = Path.GetExtension(job.BlobName);

        var validation = await _validator.ValidateAsync(
            staged, job.Sha256Hash, extension, BootImageValidationService.WimOnlyExtensions, ct);

        if (!validation.Valid)
        {
            await staged.DeleteIfExistsAsync(cancellationToken: ct);
            await _jobRepo.FailAsync(job.UploadId, validation.FailureReason ?? "Checksum validation failed.", ct);
            return;
        }

        var finalBlobName = $"published/{Guid.NewGuid():N}/winre-{job.Version.Replace(' ', '-')}{extension}";
        await MoveBlobAsync(container, staged, finalBlobName, ct);

        var published = await _recoveryImageRepo.PublishAsync(new RecoveryImage
        {
            RecoveryImageId = Guid.NewGuid(),
            Version = job.Version,
            Description = job.Description,
            SizeBytes = job.SizeBytes,
            StoragePath = $"{RecoveryImageContainer}/{finalBlobName}",
            Sha256Hash = validation.ActualHash!,
        }, ct);

        await _jobRepo.CompleteAsync(job.UploadId, published.RecoveryImageId, ct);
        LogPublished(_logger, job.Kind, published.RecoveryImageId, job.Version);
    }

    // ── Blob helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the staged blob to its published path and only then deletes the source.
    ///
    /// <para>
    /// Blob Storage has no rename, so a "move" is copy-then-delete, and
    /// <c>StartCopyFromUriAsync</c> is asynchronous server-side: it returns as soon as the copy is
    /// *scheduled*. The previous code deleted the source on the next line without awaiting the
    /// operation, which only appeared to work because same-account copies usually complete
    /// immediately. For a large blob, or under load, that is a race that can delete the source out
    /// from under an in-flight copy and leave a published entry pointing at a truncated or missing
    /// blob. Waiting for completion first makes the move safe.
    /// </para>
    /// </summary>
    private static async Task MoveBlobAsync(
        BlobContainerClient container, BlobBaseClient source, string destinationBlobName, CancellationToken ct)
    {
        var destination = container.GetBlobClient(destinationBlobName);
        var copy = await destination.StartCopyFromUriAsync(source.Uri, cancellationToken: ct);
        await copy.WaitForCompletionAsync(ct);
        await source.DeleteIfExistsAsync(cancellationToken: ct);
    }

    // ── ISO → WIM/ESD extraction (only reachable when extension == ".iso") ────

    private sealed record IsoPublishResult(
        bool Found, string? BlobName, string? Hash, long SizeBytes, string? SourcePath, string? FailureReason)
    {
        public static IsoPublishResult Success(string blobName, string hash, long sizeBytes, string sourcePath) =>
            new(true, blobName, hash, sizeBytes, sourcePath, null);

        public static IsoPublishResult Failure(string reason) => new(false, null, null, 0, null, reason);
    }

    /// <summary>
    /// Reads the staged ISO blob and streams <c>sources\install.wim</c>/<c>install.esd</c> straight
    /// out to its final published blob, hashing the bytes as they pass through, then returns its
    /// storage details. The original ISO is never published, only ever the extracted WIM/ESD, since
    /// that is the only form the Client can actually apply.
    ///
    /// <para>
    /// Nothing is spooled to instance-local disk. An earlier version extracted to a temp file so it
    /// could be hashed and then uploaded, which meant a publish needed as much free local storage
    /// as the extracted image and read/wrote it three times. Neither holds up at the top of the
    /// supported range (a 20 GB ISO), so the copy now goes ISO blob to published blob in one pass
    /// with a <see cref="CryptoStream"/> computing the SHA-256 in flight.
    /// </para>
    /// </summary>
    private async Task<IsoPublishResult> ExtractAndPublishIsoAsync(
        BlobContainerClient container,
        BlockBlobClient stagedBlob,
        string name,
        string version,
        CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        string? finalBlobName = null;
        Stream? blobStream = null;
        CryptoStream? hashingStream = null;

        try
        {
            IsoExtractionService.ExtractionResult extraction;
            var isoStream = await stagedBlob.OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false) { BufferSize = IsoReadBufferBytes }, ct);

            await using (isoStream)
            {
                try
                {
                    extraction = await _isoExtractor.ExtractInstallImageAsync(isoStream, async (foundExtension, innerCt) =>
                    {
                        finalBlobName =
                            $"published/{Guid.NewGuid():N}/{Path.GetFileNameWithoutExtension(name).Replace(' ', '-')}-{version.Replace(' ', '-')}{foundExtension}";
                        blobStream = await container.GetBlockBlobClient(finalBlobName).OpenWriteAsync(
                            overwrite: true,
                            new BlockBlobOpenWriteOptions { BufferSize = BlobWriteBufferBytes },
                            innerCt);
                        hashingStream = new CryptoStream(blobStream, sha256, CryptoStreamMode.Write, leaveOpen: true);
                        return hashingStream;
                    }, ct);
                }
                finally
                {
                    // Disposing the CryptoStream flushes its final block into the blob stream;
                    // disposing the blob stream is what commits the staged blocks. Both must happen
                    // in that order, and must happen even when the extract threw, so a partial blob
                    // is at least well-formed enough to delete below.
                    if (hashingStream is not null)
                    {
                        await hashingStream.DisposeAsync();
                    }

                    if (blobStream is not null)
                    {
                        await blobStream.DisposeAsync();
                    }
                }
            }

            if (!extraction.Found)
            {
                return IsoPublishResult.Failure(extraction.FailureReason!);
            }

            var published = container.GetBlobClient(finalBlobName!);
            var sizeBytes = (await published.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
            var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            return IsoPublishResult.Success(finalBlobName!, hash, sizeBytes, extraction.SourcePath!);
        }
        catch
        {
            // A failed extract leaves a truncated published blob behind; drop it so it can never be
            // picked up as a real image and so it stops accruing storage cost.
            if (finalBlobName is not null)
            {
                await container.GetBlobClient(finalBlobName).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            }

            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} published from upload job: {ImageId} v{Version}.")]
    private static partial void LogPublished(ILogger logger, UploadJobKind kind, Guid imageId, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted install image from ISO {BlobName} ({SourcePath}, {SizeBytes} bytes).")]
    private static partial void LogIsoExtracted(ILogger logger, string blobName, string sourcePath, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Upload publish job {UploadId} failed unexpectedly.")]
    private static partial void LogJobFailed(ILogger logger, string uploadId, Exception exception);
}

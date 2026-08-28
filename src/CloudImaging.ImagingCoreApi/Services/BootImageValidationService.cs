using System.IO;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Validates an uploaded boot/OS/recovery image blob for file-type authenticity, corruption, and
/// checksum integrity (T125a, FR-063). Used during the staged upload finalize-publish flow to
/// ensure the file is a genuine WIM/ISO image — not merely renamed — before it is committed to
/// the catalog. Only the file extensions in <see cref="AllowedExtensions"/> are ever accepted;
/// this guards against malicious files being smuggled into the catalog under a spoofed extension.
/// </summary>
public sealed partial class BootImageValidationService
{
    /// <summary>Allowed image file extensions across boot, recovery, and OS image uploads.</summary>
    public static readonly IReadOnlyCollection<string> AllowedExtensions = new[] { ".wim", ".iso" };

    // WIM (Windows Imaging Format) files begin with the ASCII signature "MSWIM" followed by
    // three null bytes.
    private static readonly byte[] WimMagic = { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00 };

    // ISO 9660 volume descriptors carry the "CD001" standard identifier at byte offset 1 of a
    // 2048-byte sector. The primary volume descriptor is conventionally sector 16, but sectors
    // 17/18 are checked as well to tolerate supplementary/terminator descriptor ordering.
    private static readonly byte[] IsoMagic = { 0x43, 0x44, 0x30, 0x30, 0x31 };
    private static readonly int[] IsoMagicOffsets = { 0x8001, 0x8801, 0x9001 };

    /// <summary>Number of leading bytes downloaded to inspect the file signature (covers the ISO check above).</summary>
    private const int SignatureCheckLength = 0x9001 + 5;

    private readonly ILogger<BootImageValidationService> _logger;

    public BootImageValidationService(ILogger<BootImageValidationService> logger) => _logger = logger;

    public sealed record ValidationResult(bool Valid, string? ActualHash, string? FailureReason);

    /// <summary>Returns whether <paramref name="extension"/> (e.g. ".wim") is an allowed image file type.</summary>
    public static bool IsAllowedExtension(string? extension) =>
        extension is not null && AllowedExtensions.Contains(extension.ToLowerInvariant());

    /// <summary>
    /// Downloads the leading bytes of <paramref name="blobClient"/> to verify its content matches
    /// the magic signature expected for <paramref name="extension"/>, then — only if that passes —
    /// computes the full SHA-256 hash and compares it against <paramref name="expectedHash"/>.
    /// Rejecting on a bad signature before hashing avoids wasting time/bandwidth downloading a
    /// multi-gigabyte file that is not a genuine image to begin with.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        BlobBaseClient blobClient,
        string expectedHash,
        string extension,
        CancellationToken ct = default)
    {
        var normalizedExt = extension.ToLowerInvariant();
        if (!IsAllowedExtension(normalizedExt))
        {
            LogUnsupportedExtension(_logger, extension);
            return new ValidationResult(false, null,
                $"Unsupported file extension '{extension}'. Only {string.Join(", ", AllowedExtensions)} are allowed.");
        }

        byte[] header;
        try
        {
            var options = new Azure.Storage.Blobs.Models.BlobDownloadOptions
            {
                Range = new HttpRange(0, SignatureCheckLength),
            };
            var result = await blobClient.DownloadStreamingAsync(options, ct);
            using var ms = new MemoryStream();
            await result.Value.Content.CopyToAsync(ms, ct);
            header = ms.ToArray();
        }
        catch (Exception ex)
        {
            LogSignatureReadFailed(_logger, ex);
            return new ValidationResult(false, null, "Failed to read file header for signature validation.");
        }

        var signatureValid = normalizedExt == ".wim" ? HasWimSignature(header) : HasIsoSignature(header);
        if (!signatureValid)
        {
            LogSignatureMismatch(_logger, normalizedExt);
            return new ValidationResult(false, null,
                $"File content does not match a valid {normalizedExt} file signature.");
        }

        // Opens a read stream over the full blob to hash it. Not wrapped by the same try/catch
        // as the header read above because this needs its own stream lifetime (`using`) — but it
        // is just as capable of throwing (e.g. a resumed upload's blob became unreadable, or a
        // transient storage fault mid-download of a multi-GB file), so it needs the same
        // "return a validation failure, don't let it bubble to a generic 500" handling.
        Stream download;
        try
        {
            download = await blobClient.OpenReadAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            LogStreamOpenFailed(_logger, ex);
            return new ValidationResult(false, null, "Failed to read file content for checksum validation.");
        }

        await using (download)
        {
            return await ValidateAsync(download, expectedHash, ct);
        }
    }

    private static bool HasWimSignature(byte[] header) =>
        header.Length >= WimMagic.Length && header.AsSpan(0, WimMagic.Length).SequenceEqual(WimMagic);

    private static bool HasIsoSignature(byte[] header)
    {
        foreach (var offset in IsoMagicOffsets)
        {
            if (header.Length >= offset + IsoMagic.Length
                && header.AsSpan(offset, IsoMagic.Length).SequenceEqual(IsoMagic))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes the SHA-256 hash of the blob at <paramref name="blobStream"/> and compares it
    /// against <paramref name="expectedHash"/>. Returns the validation result.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        Stream blobStream,
        string expectedHash,
        CancellationToken ct = default)
    {
        LogValidating(_logger, expectedHash[..Math.Min(12, expectedHash.Length)]);

        string actualHash;
        try
        {
            var hashBytes = await SHA256.HashDataAsync(blobStream, ct);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            LogHashComputeFailed(_logger, ex);
            return new ValidationResult(false, null, "Failed to compute SHA-256 hash.");
        }

        bool valid = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            LogHashMismatch(_logger, expectedHash, actualHash);
            return new ValidationResult(false, actualHash,
                $"SHA-256 mismatch. Expected: {expectedHash}, Actual: {actualHash}");
        }

        LogValidationPassed(_logger, actualHash[..12]);
        return new ValidationResult(true, actualHash, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Validating boot image SHA-256 (expected prefix={Prefix}).")]
    private static partial void LogValidating(ILogger logger, string prefix);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hash computation failed.")]
    private static partial void LogHashComputeFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hash mismatch. Expected={Expected}, Actual={Actual}.")]
    private static partial void LogHashMismatch(ILogger logger, string expected, string actual);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image SHA-256 validated (hash prefix={Prefix}).")]
    private static partial void LogValidationPassed(ILogger logger, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected upload with unsupported extension '{Extension}'.")]
    private static partial void LogUnsupportedExtension(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to read blob header for file signature validation.")]
    private static partial void LogSignatureReadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to open blob stream for checksum validation.")]
    private static partial void LogStreamOpenFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File signature validation failed for expected extension '{Extension}'.")]
    private static partial void LogSignatureMismatch(ILogger logger, string extension);
}

using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Validates an uploaded boot image WIM blob for corruption and checksum integrity (T125a, FR-063).
/// Used during the staged upload finalize-publish flow to ensure the WIM is intact before committing.
/// </summary>
public sealed partial class BootImageValidationService
{
    private readonly ILogger<BootImageValidationService> _logger;

    public BootImageValidationService(ILogger<BootImageValidationService> logger) => _logger = logger;

    public sealed record ValidationResult(bool Valid, string? ActualHash, string? FailureReason);

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
}

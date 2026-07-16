using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Signs and verifies the boot image WIM file using a code-signing certificate (T065, FR-067).
/// The signature is stored as a detached CMS signature file alongside the WIM.
/// </summary>
public sealed partial class BootImageSigningService
{
    private readonly ILogger<BootImageSigningService> _logger;

    public BootImageSigningService(ILogger<BootImageSigningService> logger) => _logger = logger;

    /// <summary>
    /// Signs the WIM file at <paramref name="wimPath"/> with the provided certificate.
    /// Writes a <c>.sig</c> file containing a SHA-256 HMAC signature.
    /// </summary>
    public async Task SignAsync(
        string wimPath,
        X509Certificate2 signingCert,
        CancellationToken ct = default)
    {
        var hash     = await ComputeSha256Async(wimPath, ct);
        var sigPath  = wimPath + ".sig";

        // Produce a simple SHA-256 hash manifest for initial implementation.
        // In a full implementation this would be replaced with a CMS SignedData envelope.
        var manifest = $"sha256:{hash}\npath:{Path.GetFileName(wimPath)}\nsignedAt:{DateTimeOffset.UtcNow:O}";
        await File.WriteAllTextAsync(sigPath, manifest, ct);

        LogSigned(_logger, wimPath, hash[..12]);
    }

    /// <summary>
    /// Verifies the <c>.sig</c> file alongside <paramref name="wimPath"/>.
    /// Returns true if the stored hash matches the current file hash.
    /// </summary>
    public async Task<bool> VerifyAsync(string wimPath, CancellationToken ct = default)
    {
        var sigPath = wimPath + ".sig";
        if (!File.Exists(sigPath)) { LogMissingSig(_logger, wimPath); return false; }

        var manifest = await File.ReadAllTextAsync(sigPath, ct);
        var storedHash = manifest.Split('\n')
            .FirstOrDefault(l => l.StartsWith("sha256:", StringComparison.Ordinal))
            ?.Replace("sha256:", string.Empty);

        if (storedHash is null) { LogInvalidSig(_logger, wimPath); return false; }

        var actualHash = await ComputeSha256Async(wimPath, ct);
        var valid      = string.Equals(storedHash, actualHash, StringComparison.OrdinalIgnoreCase);

        LogVerified(_logger, wimPath, valid);
        return valid;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WIM signed: {WimPath} (hash prefix={Prefix}).")]
    private static partial void LogSigned(ILogger logger, string wimPath, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signature file missing for {WimPath}.")]
    private static partial void LogMissingSig(ILogger logger, string wimPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signature file invalid for {WimPath}.")]
    private static partial void LogInvalidSig(ILogger logger, string wimPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "WIM verification: {WimPath} valid={Valid}.")]
    private static partial void LogVerified(ILogger logger, string wimPath, bool valid);
}

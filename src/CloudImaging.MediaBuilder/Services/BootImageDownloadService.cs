using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Downloads a boot image WIM from the Operator API SAS URL (T069, FR-056).
/// Verifies the SHA-256 hash after download before reporting success.
/// </summary>
public sealed partial class BootImageDownloadService
{
    private const int BufferSize = 81_920;

    private readonly HttpClient _httpClient;
    private readonly ILogger<BootImageDownloadService> _logger;

    public event EventHandler<(long Downloaded, long Total)>? ProgressChanged;

    public BootImageDownloadService(HttpClient httpClient, ILogger<BootImageDownloadService> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;
    }

    /// <summary>
    /// Downloads the boot image WIM from <paramref name="sasUrl"/> to <paramref name="destinationPath"/>.
    /// Verifies SHA-256 against <paramref name="expectedHash"/> after download.
    /// </summary>
    public async Task DownloadAsync(
        string sasUrl,
        string expectedHash,
        string destinationPath,
        CancellationToken ct = default)
    {
        LogDownloadStarting(_logger, destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? string.Empty);

        using var response = await _httpClient.GetAsync(sasUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.OpenWrite(destinationPath);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            ProgressChanged?.Invoke(this, (downloaded, total));
        }

        LogDownloadComplete(_logger, downloaded);

        // SHA-256 verification (FR-056: verify before USB deployment)
        await VerifyHashAsync(destinationPath, expectedHash, ct);
    }

    private async Task VerifyHashAsync(string filePath, string expectedHash, CancellationToken ct)
    {
        LogVerifyingHash(_logger, filePath);
        await using var stream = File.OpenRead(filePath);
        var hash    = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        var actual  = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);
            throw new InvalidDataException(
                $"Boot image SHA-256 mismatch. Expected: {expectedHash}, Actual: {actual}");
        }

        LogHashVerified(_logger, filePath);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading boot image to {Path}.")]
    private static partial void LogDownloadStarting(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download complete: {Bytes} bytes.")]
    private static partial void LogDownloadComplete(ILogger logger, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verifying SHA-256 of {Path}.")]
    private static partial void LogVerifyingHash(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "SHA-256 verified for {Path}.")]
    private static partial void LogHashVerified(ILogger logger, string path);
}

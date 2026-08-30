using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Downloads the OS image blob via the SAS token URL (T052/T056c, FR-009, FR-009d).
/// Checks the local <see cref="ImageCacheService"/> first — if the WIM is cached and
/// the SHA-256 hash matches, the download is skipped (cache hit).
/// Reports download progress as byte-transfer percentage.
/// </summary>
public sealed partial class ImageDownloadService
{
    private const int BufferSize = 81_920; // 80 KB
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly ImageCacheService? _cache;
    private readonly ILogger<ImageDownloadService> _logger;

    public ImageDownloadService(
        HttpClient httpClient,
        ILogger<ImageDownloadService> logger,
        ImageCacheService? cache = null)
    {
        _httpClient = httpClient;
        _cache      = cache;
        _logger     = logger;
    }

    /// <summary>
    /// Returns a local WIM path either from cache (cache hit) or by downloading from
    /// <paramref name="sasUrl"/> (cache miss).  On download, the file is written to
    /// <paramref name="destinationPath"/> and — if a cache is configured — persisted
    /// to the cache for future use.
    /// </summary>
    /// <param name="imageId">Catalog image ID used as the cache key.</param>
    /// <param name="expectedHash">Expected SHA-256 hex hash for integrity verification.</param>
    public async Task<string> EnsureLocalWimAsync(
        string imageId,
        string expectedHash,
        string sasUrl,
        string destinationPath,
        Action<int>? onProgress,
        CancellationToken ct = default)
    {
        WinPeEnvironmentGuard.EnsureRunningInWinPe("Downloading the operating system image");

        // 1. Check cache first (T056c)
        if (_cache is not null)
        {
            var cached = await _cache.TryGetCachedWimAsync(imageId, expectedHash, ct);
            if (cached is not null)
            {
                onProgress?.Invoke(100);
                return cached;
            }
        }

        // 2. Download from SAS URL
        await DownloadAsync(sasUrl, destinationPath, onProgress, ct);

        // 3. Write to cache if available (T056c: only write if hash verified in DownloadAsync caller)
        if (_cache is not null)
        {
            try
            {
                return await _cache.WriteAsync(imageId, destinationPath, expectedHash, ct);
            }
            catch (InvalidDataException ex)
            {
                // Hash mismatch on cache write — the caller should treat this as a download error
                LogCacheWriteFailed(_logger, ex, imageId);
                throw;
            }
        }

        return destinationPath;
    }
    public async Task DownloadAsync(
        string sasUrl,
        string destinationPath,
        Action<int>? onProgress,
        CancellationToken ct = default)
    {
        // Same bounded-retry-with-backoff convention as the sibling
        // CloudImaging.MediaBuilder.Services.BootImageDownloadService (also a SAS-URL blob
        // download) — the WinPE network path has been observed to hit transient connection
        // timeouts (SocketException 10060) during a session, and a single-shot GET here would
        // fail the whole imaging pipeline (forcing a full disk reformat via Retry) rather than
        // just re-attempting the download.
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadOnceAsync(sasUrl, destinationPath, onProgress, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                LogAttemptFailed(_logger, attempt, ex);
                try { File.Delete(destinationPath); } catch { /* best-effort */ }

                if (attempt < MaxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s
                    await Task.Delay(delay, ct);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to download the image after {MaxAttempts} attempts.", lastError);
    }

    private async Task DownloadOnceAsync(
        string sasUrl,
        string destinationPath,
        Action<int>? onProgress,
        CancellationToken ct)
    {
        LogStarting(_logger, sasUrl[..Math.Min(60, sasUrl.Length)]);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? string.Empty);

        using var response = await _httpClient.GetAsync(sasUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.OpenWrite(destinationPath);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            if (totalBytes > 0)
            {
                var percent = (int)((double)downloaded / totalBytes * 100);
                onProgress?.Invoke(percent);
            }
        }

        LogCompleted(_logger, downloaded);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting image download from {UrlPrefix}…")]
    private static partial void LogStarting(ILogger logger, string urlPrefix);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image download complete: {Bytes} bytes written.")]
    private static partial void LogCompleted(ILogger logger, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download attempt {Attempt} failed.")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache write failed for image {ImageId} — hash mismatch.")]
    private static partial void LogCacheWriteFailed(ILogger logger, Exception ex, string imageId);
}

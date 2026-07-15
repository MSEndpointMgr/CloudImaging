using System.IO;
using System.Net.Http;
using CloudImaging.Client.Services;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Downloads the OS image blob via the SAS token URL (T052, FR-009).
/// Reports download progress as byte-transfer percentage.
/// </summary>
public sealed partial class ImageDownloadService
{
    private const int BufferSize = 81_920; // 80 KB

    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageDownloadService> _logger;

    public ImageDownloadService(
        HttpClient httpClient,
        ILogger<ImageDownloadService> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;
    }

    /// <summary>
    /// Downloads the image at <paramref name="sasUrl"/> to <paramref name="destinationPath"/>.
    /// Calls <paramref name="onProgress"/> with 0–100 as bytes are received.
    /// </summary>
    public async Task DownloadAsync(
        string sasUrl,
        string destinationPath,
        Action<int>? onProgress,
        CancellationToken ct = default)
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
}

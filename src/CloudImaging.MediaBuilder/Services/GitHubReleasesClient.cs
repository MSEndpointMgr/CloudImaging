using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Resolves the latest MSEndpointMgr/CloudImaging GitHub release and downloads the Cloud
/// Imaging Client binaries asset for the GenerateBootImageView's "Automatic download" source
/// option (T152/T153, FR-051a).
///
/// Per FR-051a's clarified error-handling behavior: if the GitHub releases API or the asset
/// download fails, this retries up to <see cref="MaxAttempts"/> times with exponential backoff.
/// If every attempt fails, it throws with a clear message; the caller does NOT automatically
/// fall back to another source option — the technician can switch to Custom local path
/// manually.
/// </summary>
public sealed partial class GitHubReleasesClient
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/MSEndpointMgr/CloudImaging/releases/latest";
    private const string ClientAssetName = "CloudImaging.Client.zip";
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly ILogger<GitHubReleasesClient> _logger;

    public GitHubReleasesClient(HttpClient http, ILogger<GitHubReleasesClient> logger)
    {
        _http   = http;
        _logger = logger;

        // GitHub's REST API rejects requests with no User-Agent header.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudImaging-MediaBuilder");
    }

    /// <summary>Raised with a message and 0-100 percent as the release is resolved and the asset downloads/extracts.</summary>
    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <summary>
    /// Resolves the latest release, downloads the <c>CloudImaging.Client.zip</c> asset, and
    /// extracts it into a fresh directory under <c>%TEMP%</c>. Returns the extracted folder
    /// path, ready to use as a Client binaries source.
    /// </summary>
    public async Task<string> DownloadLatestClientAsync(CancellationToken ct = default)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOnceAsync(attempt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                LogAttemptFailed(_logger, attempt, ex);

                if (attempt < MaxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s
                    ReportProgress($"Download failed, retrying in {delay.TotalSeconds:0}s ({attempt}/{MaxAttempts} attempts used)", 0);
                    await Task.Delay(delay, ct);
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not download the Cloud Imaging Client from GitHub Releases after {MaxAttempts} attempts. " +
            "Check your internet connection, or switch to the Custom local path option instead.", lastError);
    }

    private async Task<string> DownloadOnceAsync(int attempt, CancellationToken ct)
    {
        ReportProgress($"Resolving latest GitHub release (attempt {attempt}/{MaxAttempts})", 0);
        var release = await _http.GetFromJsonAsync<GitHubReleaseDto>(ReleasesApiUrl, ct)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ClientAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Release \"{release.TagName}\" does not contain a \"{ClientAssetName}\" asset.");

        var extractDir = Path.Combine(Path.GetTempPath(), $"ci-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        var zipPath = Path.Combine(extractDir, ClientAssetName);

        ReportProgress($"Downloading Cloud Imaging Client {release.TagName}", 10);
        await DownloadWithProgressAsync(asset.BrowserDownloadUrl, zipPath, ct);

        ReportProgress("Extracting Cloud Imaging Client", 90);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { /* best-effort */ }

        ReportProgress("Cloud Imaging Client ready", 100);
        LogDownloaded(_logger, release.TagName, asset.Name);
        return extractDir;
    }

    private async Task DownloadWithProgressAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            if (totalBytes is > 0)
            {
                // Downloading spans 10-90% of this client's own progress range (see
                // DownloadOnceAsync), leaving room either side for release resolution/extraction.
                var percent = 10 + (int)(totalRead * 80 / totalBytes.Value);
                ReportProgress($"Downloading Cloud Imaging Client ({FormatBytes(totalRead)}/{FormatBytes(totalBytes.Value)})", percent);
            }
        }
    }

    private void ReportProgress(string message, int percent) => ProgressChanged?.Invoke(this, (message, percent));

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{bytes / 1024.0:0} KB";

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded Cloud Imaging Client {Tag} ({Asset}) from GitHub Releases.")]
    private static partial void LogDownloaded(ILogger logger, string tag, string asset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub Releases download attempt {Attempt} failed.")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, Exception ex);

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetDto> Assets { get; init; } = [];
    }

    private sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

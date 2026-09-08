using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Resolves the MSEndpointMgr/CloudImaging "mse-ci-client-latest" alias release (always the newest
/// stable Client release; see release-client.yml) and downloads the Cloud Imaging Client
/// binaries asset for the GenerateBootImageView's "Automatic download" source option
/// (T152/T153, FR-051a). Verifies the download against the release's published SHA256SUMS
/// asset before ever extracting or using it.
///
/// Per FR-051a's clarified error-handling behavior: if the GitHub releases API, the asset
/// download, or the checksum verification fails, this retries up to <see cref="MaxAttempts"/>
/// times with exponential backoff. If every attempt fails, it throws with a clear message; the
/// caller does NOT automatically fall back to another source option — the technician can switch
/// to Custom local path manually.
/// </summary>
public sealed partial class GitHubReleasesClient
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/MSEndpointMgr/CloudImaging/releases/tags/mse-ci-client-latest";
    private const string ClientAssetName = "CloudImaging.Client.zip";
    private const string ChecksumAssetName = "SHA256SUMS";
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
    /// Resolves the "mse-ci-client-latest" alias release, downloads the <c>CloudImaging.Client.zip</c>
    /// asset, and extracts it into a fresh directory under <c>%TEMP%</c>. Returns the extracted
    /// folder path, ready to use as a Client binaries source.
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
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Not a transient failure — no stable Client release has ever been published.
                throw new InvalidOperationException(
                    "No published Cloud Imaging Client release was found (the \"mse-ci-client-latest\" " +
                    "release doesn't exist yet). Switch to the Custom local path option, or publish " +
                    "a stable mse-ci-client-vX.Y.Z release first.", ex);
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
        ReportProgress($"Resolving latest Cloud Imaging Client release (attempt {attempt}/{MaxAttempts})", 0);
        var release = await _http.GetFromJsonAsync<GitHubReleaseDto>(ReleasesApiUrl, ct)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ClientAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Release \"{release.TagName}\" does not contain a \"{ClientAssetName}\" asset.");

        var checksumAsset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Release \"{release.TagName}\" does not contain a \"{ChecksumAssetName}\" asset needed to verify the download.");

        var extractDir = Path.Combine(Path.GetTempPath(), $"ci-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        var zipPath = Path.Combine(extractDir, ClientAssetName);

        try
        {
            // release.Name embeds the real mse-ci-client-vX.Y.Z version (see release-client.yml); TagName
            // alone would just say "mse-ci-client-latest", which isn't informative to the technician.
            var displayVersion = string.IsNullOrEmpty(release.Name) ? release.TagName : release.Name;
            ReportProgress($"Downloading Cloud Imaging Client {displayVersion}", 10);
            await DownloadWithProgressAsync(asset.BrowserDownloadUrl, zipPath, ct);

            ReportProgress("Verifying download integrity", 92);
            await VerifyChecksumAsync(checksumAsset.BrowserDownloadUrl, zipPath, ct);

            ReportProgress("Extracting Cloud Imaging Client", 95);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { /* best-effort */ }

            ReportProgress("Cloud Imaging Client ready", 100);
            LogDownloaded(_logger, displayVersion, asset.Name);
            return extractDir;
        }
        catch
        {
            // Don't leak a partial/corrupt download or empty extraction folder into %TEMP% on failure —
            // the caller retries with a brand-new extractDir, so this one no longer serves any purpose.
            try { Directory.Delete(extractDir, recursive: true); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Verifies the downloaded ZIP against the release's published SHA256SUMS asset (format:
    /// "&lt;hex hash&gt;  &lt;filename&gt;" per line, as written by release-client.yml). Throws if the
    /// asset entry is missing or the computed hash doesn't match — a corrupted or tampered download
    /// must never be extracted and used to build a boot image.
    /// </summary>
    private async Task VerifyChecksumAsync(string checksumUrl, string zipPath, CancellationToken ct)
    {
        var sumsText = await _http.GetStringAsync(checksumUrl, ct);
        var expectedHash = ParseExpectedHash(sumsText, ClientAssetName)
            ?? throw new InvalidOperationException(
                $"\"{ChecksumAssetName}\" does not contain an entry for \"{ClientAssetName}\".");

        string actualHash;
        await using (var stream = File.OpenRead(zipPath))
        {
            actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Downloaded \"{ClientAssetName}\" failed SHA-256 verification (expected {expectedHash}, got " +
                $"{actualHash}). The download may be corrupted or tampered with.");
        }

        LogChecksumVerified(_logger, actualHash);
    }

    private static string? ParseExpectedHash(string sumsText, string assetName)
    {
        foreach (var rawLine in sumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1], assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "SHA-256 checksum verified for downloaded Cloud Imaging Client ({Hash}).")]
    private static partial void LogChecksumVerified(ILogger logger, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub Releases download attempt {Attempt} failed.")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, Exception ex);

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

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

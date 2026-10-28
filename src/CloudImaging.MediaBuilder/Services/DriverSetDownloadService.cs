using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CloudImaging.Contracts.Models.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>A driver file ready to copy to the USB drive's CACHE partition.</summary>
/// <param name="Sha256">SHA-256, lower-case hex; also the folder name on the drive.</param>
/// <param name="FileName">File name.</param>
/// <param name="SizeBytes">Size.</param>
/// <param name="SourcePath">Verified local copy.</param>
public sealed record PreloadDriverFile(string Sha256, string FileName, long SizeBytes, string SourcePath);

/// <summary>Driver download progress.</summary>
/// <param name="Files">Files done.</param>
/// <param name="TotalFiles">Files in total.</param>
/// <param name="Bytes">Bytes done.</param>
/// <param name="TotalBytes">Bytes in total.</param>
public sealed record DriverDownloadProgress(int Files, int TotalFiles, long Bytes, long TotalBytes);

/// <summary>Content of <c>metadata.json</c> next to a cached driver file, shared with the Client's USB cache.</summary>
/// <param name="Sha256">SHA-256.</param>
/// <param name="FileName">File name.</param>
/// <param name="SizeBytes">Size.</param>
/// <param name="LastUsedAt">Last write or use, for the 30-day idle purge.</param>
public sealed record DriverCacheMetadata(string Sha256, string FileName, long SizeBytes, DateTimeOffset LastUsedAt);

/// <summary>
/// Downloads the files of chosen driver sets before partitioning (the elevated worker has no
/// network access) into <c>%LOCALAPPDATA%\Cloud Imaging Media Builder\Cache\drivers\&lt;sha256&gt;\</c>,
/// with the boot image download's retry, and keeps them 30 days after last use so preparing
/// several drives downloads once. All work runs on the thread pool.
/// </summary>
public sealed partial class DriverSetDownloadService
{
    /// <summary>Attempts per file.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Files unused this long are removed.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);

    // Links are renewed this long before they expire.
    private static readonly TimeSpan LinkRefreshMargin = TimeSpan.FromMinutes(5);

    internal const string MetadataFileName = "metadata.json";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Plain HTTP client for the read links.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cacheRoot">Cache folder; defaults to the Media Builder cache.</param>
    /// <param name="delay">Wait between attempts (tests).</param>
    public DriverSetDownloadService(HttpClient http, ILogger logger, string? cacheRoot = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _logger = logger;
        _root = cacheRoot ?? DefaultCacheRoot;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The local driver cache folder.</summary>
    public static string DefaultCacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cloud Imaging Media Builder", "Cache", "drivers");

    /// <summary>The message for files that could not be downloaded.</summary>
    /// <param name="failed">Number of files.</param>
    public static string FailedMessage(int failed) => failed == 1
        ? "1 driver file could not be downloaded. Try again, or clear the driver sets."
        : $"{failed} driver files could not be downloaded. Try again, or clear the driver sets.";

    /// <summary>
    /// Makes every file available locally: reuses cached copies, downloads the rest and verifies
    /// them. Throws <see cref="InvalidOperationException"/> naming how many files failed.
    /// </summary>
    /// <param name="files">Distinct files with read links.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="refreshLinks">Fetches new links when one has expired; links last 60 minutes and a large selection can take longer.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<IReadOnlyList<PreloadDriverFile>> DownloadAsync(IReadOnlyList<MediaBuilderDriverFile> files, Action<DriverDownloadProgress>? progress, Func<CancellationToken, Task<IReadOnlyList<MediaBuilderDriverFile>>>? refreshLinks = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => DownloadAllAsync(files, progress, refreshLinks, ct), ct);
    }

    private async Task<IReadOnlyList<PreloadDriverFile>> DownloadAllAsync(IReadOnlyList<MediaBuilderDriverFile> files, Action<DriverDownloadProgress>? progress, Func<CancellationToken, Task<IReadOnlyList<MediaBuilderDriverFile>>>? refreshLinks, CancellationToken ct)
    {
        PurgeIdle(_root, IdleLifetime);
        var totalBytes = files.Sum(f => f.SizeBytes);
        var result = new List<PreloadDriverFile>();
        var failed = 0;
        long bytes = 0;
        Dictionary<string, MediaBuilderDriverFile>? fresh = null;
        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            if (fresh?.GetValueOrDefault(file.Sha256) is { } renewed)
            {
                file = renewed;
            }
            else if (refreshLinks is not null && file.ExpiresAt - DateTimeOffset.UtcNow < LinkRefreshMargin)
            {
                fresh = (await refreshLinks(ct)).ToDictionary(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
                file = fresh.GetValueOrDefault(file.Sha256) ?? file;
            }

            progress?.Invoke(new DriverDownloadProgress(index, files.Count, bytes, totalBytes));
            var local = await EnsureLocalAsync(file, ct);
            if (local is null)
            {
                failed++;
            }
            else
            {
                result.Add(local);
            }

            bytes += file.SizeBytes;
        }

        progress?.Invoke(new DriverDownloadProgress(files.Count, files.Count, totalBytes, totalBytes));
        if (failed > 0)
        {
            throw new InvalidOperationException(FailedMessage(failed));
        }

        return result;
    }

    private async Task<PreloadDriverFile?> EnsureLocalAsync(MediaBuilderDriverFile file, CancellationToken ct)
    {
        // Values from the service become folder and file names, so only a hex hash and a bare file name are accepted.
        var fileName = Path.GetFileName(file.FileName);
        if (!IsSha256(file.Sha256) || string.IsNullOrWhiteSpace(fileName) || fileName != file.FileName)
        {
            LogFileFailed(_logger, file.FileName, "Unexpected file name or hash.");
            return null;
        }

        var sha = file.Sha256.ToLowerInvariant();
        var dir = Path.Combine(_root, sha);
        var path = Path.Combine(dir, fileName);
        var metadataPath = Path.Combine(dir, MetadataFileName);
        // A cached copy is used only when its content still matches; a damaged one is downloaded again.
        if (File.Exists(metadataPath) && File.Exists(path) && new FileInfo(path).Length == file.SizeBytes && await HashMatchesAsync(path, sha, ct))
        {
            TryWriteMetadata(metadataPath, new DriverCacheMetadata(sha, fileName, file.SizeBytes, DateTimeOffset.UtcNow));
            return new PreloadDriverFile(sha, fileName, file.SizeBytes, path);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                Directory.CreateDirectory(dir);
                using (var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is { } announced && announced != file.SizeBytes)
                    {
                        throw new InvalidDataException("The driver file is not the size the service lists.");
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    var hash = await DriverSetPreloadCopier.CopyHashingAsync(input, output, file.SizeBytes, ct);
                    if (!string.Equals(hash, sha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The downloaded driver file does not match its checksum.");
                    }
                }

                await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new DriverCacheMetadata(sha, fileName, file.SizeBytes, DateTimeOffset.UtcNow), Json), ct);
                return new PreloadDriverFile(sha, fileName, file.SizeBytes, path);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                LogFileFailed(_logger, fileName, ex.Message);
                TryDeleteDirectory(dir);
                if (attempt < MaxAttempts)
                {
                    await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }

        return null;
    }

    /// <summary>Removes folders unused for <paramref name="idle"/>, and folders without metadata.</summary>
    /// <param name="root">Cache folder.</param>
    /// <param name="idle">Idle period.</param>
    public static void PurgeIdle(string root, TimeSpan idle)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            DateTimeOffset? lastUsed = null;
            try
            {
                var metadataPath = Path.Combine(dir, MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    lastUsed = JsonSerializer.Deserialize<DriverCacheMetadata>(File.ReadAllText(metadataPath), Json)?.LastUsedAt;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                lastUsed = null;
            }

            if (lastUsed is null || DateTimeOffset.UtcNow - lastUsed.Value > idle)
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void TryWriteMetadata(string path, DriverCacheMetadata metadata)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(metadata, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the idle clock is lost.
            LogCacheWriteFailed(_logger, path, ex.Message);
        }
    }

    internal static bool TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next preparation retries the removal.
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver cache file {Path} could not be written: {Reason}")]
    private static partial void LogCacheWriteFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver file {FileName} could not be downloaded: {Reason}")]
    private static partial void LogFileFailed(ILogger logger, string fileName, string reason);
}

/// <summary>
/// The elevated worker's copy of preloaded driver files to
/// <c>CACHE:\CloudImagingCache\drivers\&lt;sha256&gt;\&lt;fileName&gt;</c>, the layout the Client
/// reads. Each copy is hashed; <c>metadata.json</c> is written last.
/// </summary>
public static class DriverSetPreloadCopier
{
    /// <summary>A copy failed its hash check.</summary>
    public const string DamagedMessage = "A driver file was damaged while copying to the USB drive. Prepare the drive again.";

    /// <summary>Copies every file.</summary>
    /// <param name="cacheRoot">The <c>CloudImagingCache</c> folder on the CACHE partition.</param>
    /// <param name="files">Files to copy.</param>
    /// <param name="progress">Files copied and total.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task CopyAsync(string cacheRoot, IReadOnlyList<PreloadDriverFile> files, Action<int, int>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.Combine(cacheRoot, "drivers");
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var fileName = Path.GetFileName(file.FileName);
            if (!DriverSetDownloadService.IsSha256(file.Sha256) || string.IsNullOrWhiteSpace(fileName) || fileName != file.FileName)
            {
                throw new InvalidOperationException(DamagedMessage);
            }

            var dir = Path.Combine(root, file.Sha256.ToLowerInvariant());
            DriverSetDownloadService.TryDeleteDirectory(dir);
            Directory.CreateDirectory(dir);
            string hash;
            await using (var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(Path.Combine(dir, fileName), FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                hash = await CopyHashingAsync(input, output, ct);
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DriverSetDownloadService.TryDeleteDirectory(dir);
                throw new InvalidOperationException(DamagedMessage);
            }

            var metadata = new DriverCacheMetadata(file.Sha256.ToLowerInvariant(), fileName, file.SizeBytes, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(dir, DriverSetDownloadService.MetadataFileName), JsonSerializer.Serialize(metadata, DriverSetDownloadService.Json), ct);
            progress?.Invoke(index + 1, files.Count);
        }
    }

    /// <summary>Copies while hashing.</summary>
    /// <returns>Lower-case hex SHA-256 of the copied bytes.</returns>
    public static Task<string> CopyHashingAsync(Stream input, Stream output, CancellationToken ct) => CopyHashingAsync(input, output, 0, ct);

    /// <summary>Copies while hashing.</summary>
    /// <param name="input">Source.</param>
    /// <param name="output">Target.</param>
    /// <param name="maxBytes">Stops with <see cref="InvalidDataException"/> past this many bytes; 0 for no limit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Lower-case hex SHA-256 of the copied bytes.</returns>
    public static async Task<string> CopyHashingAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (maxBytes > 0 && total > maxBytes)
            {
                throw new InvalidDataException("The driver file is larger than the service lists.");
            }

            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

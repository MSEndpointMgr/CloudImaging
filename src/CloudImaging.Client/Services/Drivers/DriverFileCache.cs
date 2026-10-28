using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Drivers;

/// <summary>Content of <c>metadata.json</c> next to a cached driver file.</summary>
/// <param name="Sha256">SHA-256 of the file, also the folder name.</param>
/// <param name="FileName">File name inside the folder.</param>
/// <param name="SizeBytes">File size.</param>
/// <param name="LastUsedAt">Last write or hit, for the 30-day idle purge.</param>
public sealed record DriverCacheMetadata(string Sha256, string FileName, long SizeBytes, DateTimeOffset LastUsedAt);

/// <summary>
/// Driver files kept on the USB drive's CACHE partition under
/// <c>CloudImagingCache\drivers\&lt;sha256&gt;\</c>, written by the Client after a verified
/// download or preloaded by Media Builder. <c>metadata.json</c> is always written last, so a
/// folder without it is an interrupted write and is removed. Every hit is hashed again on the
/// Windows volume; nothing is evicted while a step runs.
/// </summary>
public sealed partial class DriverFileCache
{
    /// <summary>Folder under the cache root.</summary>
    public const string FolderName = "drivers";

    /// <summary>Metadata file name, written last.</summary>
    public const string MetadataFileName = "metadata.json";

    /// <summary>Writes stop below this much free space; reads continue.</summary>
    public const long MinFreeBytes = 500L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string? _folder;
    private readonly Func<string, long> _freeSpace;
    private readonly ILogger _logger;

    /// <param name="cacheRoot">The <c>CloudImagingCache</c> folder on the CACHE volume, or null when there is none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="freeSpace">Free bytes on the volume of a path.</param>
    public DriverFileCache(string? cacheRoot, ILogger logger, Func<string, long>? freeSpace = null)
    {
        _folder = string.IsNullOrWhiteSpace(cacheRoot) ? null : Path.Combine(cacheRoot, FolderName);
        _logger = logger;
        _freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
    }

    /// <summary>True when a CACHE volume exists.</summary>
    public bool IsAvailable => _folder is not null;

    /// <summary>Removes folders without <c>metadata.json</c>. Called when the step starts.</summary>
    public void Prepare()
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            return;
        }

        foreach (var dir in SafeDirectories(_folder))
        {
            if (!File.Exists(Path.Combine(dir, MetadataFileName)))
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    /// <summary>True when a complete entry exists for the hash.</summary>
    /// <param name="sha256">File hash.</param>
    public bool Contains(string sha256) => EntryFolder(sha256) is { } dir && File.Exists(Path.Combine(dir, MetadataFileName));

    /// <summary>
    /// Copies a cached file to <paramref name="destination"/> while hashing the copy. Returns true
    /// only when the copy's SHA-256 matches; a mismatch deletes the copy and the cache entry.
    /// </summary>
    /// <param name="sha256">Expected hash.</param>
    /// <param name="destination">Target file path.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> TryCopyAsync(string sha256, string destination, CancellationToken ct = default)
    {
        if (EntryFolder(sha256) is not { } dir)
        {
            return false;
        }

        try
        {
            var metadataPath = Path.Combine(dir, MetadataFileName);
            if (!File.Exists(metadataPath))
            {
                return false;
            }

            var metadata = JsonSerializer.Deserialize<DriverCacheMetadata>(await File.ReadAllTextAsync(metadataPath, ct), Json);
            var source = metadata is null ? null : Path.Combine(dir, Path.GetFileName(metadata.FileName));
            if (source is null || !File.Exists(source))
            {
                TryDeleteDirectory(dir);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string hash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                hash = await CopyHashingAsync(input, output, ct);
            }

            if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                LogDamaged(_logger, sha256);
                TryDeleteFile(destination);
                TryDeleteDirectory(dir);
                return false;
            }

            // A hit refreshes the idle clock; a failure here never fails the copy.
            TryWriteMetadata(metadataPath, metadata! with { LastUsedAt = DateTimeOffset.UtcNow });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogReadFailed(_logger, sha256, ex.Message);
            TryDeleteFile(destination);
            return false;
        }
    }

    /// <summary>Stores a verified download. Never throws; a failed write only logs.</summary>
    /// <param name="sha256">Verified hash of <paramref name="source"/>.</param>
    /// <param name="source">The downloaded file.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task AddAsync(string sha256, string source, CancellationToken ct = default)
    {
        if (EntryFolder(sha256) is not { } dir || Contains(sha256))
        {
            return;
        }

        try
        {
            if (_freeSpace(_folder!) < MinFreeBytes)
            {
                return;
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(source);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(Path.Combine(dir, fileName), FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, ct);
            }

            var metadata = new DriverCacheMetadata(sha256.ToLowerInvariant(), fileName, new FileInfo(source).Length, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(dir, MetadataFileName), JsonSerializer.Serialize(metadata, Json), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, sha256, ex.Message);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>Removes entries not used for <paramref name="idle"/>, and entries without metadata.</summary>
    /// <param name="cacheRoot">The <c>CloudImagingCache</c> folder.</param>
    /// <param name="idle">Idle period.</param>
    /// <returns>Entries removed.</returns>
    public static int PurgeIdle(string cacheRoot, TimeSpan idle)
    {
        var folder = Path.Combine(cacheRoot, FolderName);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var removed = 0;
        foreach (var dir in SafeDirectories(folder))
        {
            var metadataPath = Path.Combine(dir, MetadataFileName);
            DateTimeOffset? lastUsed = null;
            try
            {
                if (File.Exists(metadataPath))
                {
                    lastUsed = JsonSerializer.Deserialize<DriverCacheMetadata>(File.ReadAllText(metadataPath), Json)?.LastUsedAt;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                lastUsed = null;
            }

            if (lastUsed is null || DateTimeOffset.UtcNow - lastUsed.Value > idle)
            {
                if (TryDeleteDirectory(dir))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>Copies while hashing.</summary>
    /// <returns>Lower-case hex SHA-256 of the copied bytes.</returns>
    public static async Task<string> CopyHashingAsync(Stream input, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    // Hashes are hex; anything else never maps to a folder, so a descriptor value cannot escape the cache.
    private string? EntryFolder(string sha256)
    {
        if (_folder is null || string.IsNullOrEmpty(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            return null;
        }

        return Path.Combine(_folder, sha256.ToLowerInvariant());
    }

    private static string[] SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void TryWriteMetadata(string path, DriverCacheMetadata metadata)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(metadata, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only media still serves hits.
            LogCacheIo(_logger, path, ex.Message);
        }
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The component folder is deleted at the end of the step anyway.
            LogCacheIo(_logger, path, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "USB driver cache file {Path} could not be written or removed: {Reason}")]
    private static partial void LogCacheIo(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cached driver file {Sha256} failed its hash check and was removed.")]
    private static partial void LogDamaged(ILogger logger, string sha256);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cached driver file {Sha256} could not be read: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string sha256, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver file {Sha256} could not be written to the USB drive: {Reason}")]
    private static partial void LogWriteFailed(ILogger logger, string sha256, string reason);
}

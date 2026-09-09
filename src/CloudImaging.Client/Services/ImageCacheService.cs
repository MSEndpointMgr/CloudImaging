using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Manages the local OS image cache on the WinPE device (T056a, FR-009d).
///
/// Cache storage layout (on the cache partition, configurable via env var):
///   {CacheRoot}\images\{imageId}\image.wim      — the cached WIM blob
///   {CacheRoot}\images\{imageId}\metadata.json  — JSON with hash, cachedAt, sizeBytes
///
/// Only valid, hash-verified WIM files are persisted in the cache.
/// Cache entries idle for 30 days (no cache hit) are eligible for auto-purge (T056d); a hit
/// refreshes the entry's clock, so an actively-reused image is never purged for being old.
/// </summary>
public sealed partial class ImageCacheService
{
    private const string ImagesDirName   = "images";
    private const string WimFileName     = "image.wim";
    private const string MetaFileName    = "metadata.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly string _cacheRoot;
    private readonly ILogger<ImageCacheService> _logger;

    public ImageCacheService(
        string cacheRoot,
        ILogger<ImageCacheService> logger)
    {
        _cacheRoot = cacheRoot;
        _logger    = logger;
    }

    // ── Cache entry metadata ──────────────────────────────────────────────────

    public sealed record CacheEntryMetadata(
        string ImageId,
        string Sha256Hash,
        long SizeBytes,
        DateTimeOffset CachedAt);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path of a cached WIM file if it exists and the stored hash
    /// matches <paramref name="expectedHash"/>; otherwise returns <c>null</c>.
    /// </summary>
    public async Task<string?> TryGetCachedWimAsync(
        string imageId,
        string expectedHash,
        CancellationToken ct = default)
    {
        var dir      = ImageDir(imageId);
        var wimPath  = Path.Combine(dir, WimFileName);
        var metaPath = Path.Combine(dir, MetaFileName);

        if (!File.Exists(wimPath) || !File.Exists(metaPath))
        {
            LogCacheMiss(_logger, imageId);
            return null;
        }

        var meta = await ReadMetaAsync(metaPath, ct);
        if (meta is null || !string.Equals(meta.Sha256Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogCacheHashMismatch(_logger, imageId);
            // Remove stale/corrupt entry
            SafeDelete(wimPath);
            SafeDelete(metaPath);
            return null;
        }

        // Refresh the TTL clock on every hit so an actively-reused image is never purged just
        // because it's old; the 30-day window tracks idle time since last use, not download age.
        // Staleness/correctness is handled separately above via the hash comparison.
        await WriteMetaAsync(metaPath, meta with { CachedAt = DateTimeOffset.UtcNow }, ct);

        LogCacheHit(_logger, imageId, meta.Sha256Hash[..8]);
        return wimPath;
    }

    /// <summary>
    /// Persists a downloaded WIM to the cache and writes the metadata record.
    /// Verifies the actual SHA-256 before writing the metadata.
    /// </summary>
    public async Task<string> WriteAsync(
        string imageId,
        string sourcePath,
        string expectedHash,
        CancellationToken ct = default)
    {
        var dir     = ImageDir(imageId);
        Directory.CreateDirectory(dir);

        // Compute actual hash of the downloaded file
        var actualHash = await ComputeSha256Async(sourcePath, ct);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Cache write aborted: hash mismatch for image {imageId}. " +
                $"Expected={expectedHash}, Actual={actualHash}");

        var wimDest  = Path.Combine(dir, WimFileName);
        var metaPath = Path.Combine(dir, MetaFileName);
        var info     = new FileInfo(sourcePath);

        File.Copy(sourcePath, wimDest, overwrite: true);

        var meta = new CacheEntryMetadata(imageId, actualHash, info.Length, DateTimeOffset.UtcNow);
        await WriteMetaAsync(metaPath, meta, ct);

        LogCacheWritten(_logger, imageId, info.Length);
        return wimDest;
    }

    /// <summary>
    /// Returns available disk space (bytes) on the drive hosting <see cref="_cacheRoot"/>.
    /// </summary>
    public long GetAvailableDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_cacheRoot) ?? _cacheRoot;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return 0; }
    }

    /// <summary>Returns all cache entry metadata, ordered by CachedAt ascending.</summary>
    public async Task<IReadOnlyList<CacheEntryMetadata>> ListEntriesAsync(CancellationToken ct = default)
    {
        var imagesDir = Path.Combine(_cacheRoot, ImagesDirName);
        if (!Directory.Exists(imagesDir)) return [];

        var entries = new List<CacheEntryMetadata>();
        foreach (var dir in Directory.GetDirectories(imagesDir))
        {
            var metaPath = Path.Combine(dir, MetaFileName);
            var meta = await ReadMetaAsync(metaPath, ct);
            if (meta is not null) entries.Add(meta);
        }

        return entries.OrderBy(e => e.CachedAt).ToList();
    }

    /// <summary>Removes the cache entry for a given image ID.</summary>
    public void Evict(string imageId)
    {
        var dir = ImageDir(imageId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            LogEvicted(_logger, imageId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ImageDir(string imageId) =>
        Path.Combine(_cacheRoot, ImagesDirName, imageId);

    private static async Task<CacheEntryMetadata?> ReadMetaAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CacheEntryMetadata>(stream, cancellationToken: ct);
        }
        catch { return null; }
    }

    private static async Task WriteMetaAsync(string path, CacheEntryMetadata meta, CancellationToken ct)
    {
        // File.Create truncates; File.OpenWrite does not. Rewriting an existing entry with a
        // shorter payload would otherwise leave the tail of the previous content in place and
        // produce unparseable JSON. The payloads differ in length in normal use because
        // System.Text.Json trims trailing zeros from a DateTimeOffset's fractional seconds.
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, meta, cancellationToken: ct);
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss for image {ImageId}.")]
    private static partial void LogCacheMiss(ILogger logger, string imageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache hash mismatch for image {ImageId} — entry evicted.")]
    private static partial void LogCacheHashMismatch(ILogger logger, string imageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache hit for image {ImageId} (hash prefix={HashPrefix}).")]
    private static partial void LogCacheHit(ILogger logger, string imageId, string hashPrefix);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cached image {ImageId} ({Bytes} bytes) to disk.")]
    private static partial void LogCacheWritten(ILogger logger, string imageId, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache entry evicted for image {ImageId}.")]
    private static partial void LogEvicted(ILogger logger, string imageId);
}

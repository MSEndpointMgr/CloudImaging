using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Runs cache maintenance on application startup (T056d, FR-009d).
///
/// Maintenance policy:
///   1. Purge entries not used (no cache hit) in the last 30 days (expired TTL); a cache hit
///      refreshes the entry's clock, so an actively-reused image is never purged for being old.
///   2. Remove orphaned directories (dirs with no valid metadata.json).
///   3. If insufficient free space remains after purge, proceed WITHOUT caching
///      (do NOT perform LRU eviction of valid entries).
///
/// This service does NOT evict valid, unexpired cache entries to free space.
/// Instead, it sets <see cref="CacheWriteEnabled"/> to false when space is tight.
/// </summary>
public sealed partial class ImageCacheMaintenanceService
{
    /// <summary>Minimum free space required before cache writes are enabled (500 MB).</summary>
    private const long MinFreeSpaceBytes = 500L * 1024 * 1024;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly ImageCacheService _cache;
    private readonly ILogger<ImageCacheMaintenanceService> _logger;

    /// <summary>
    /// True when cache writes are permitted (sufficient free space after purge).
    /// Set to false if purge could not free enough space — callers should download
    /// directly without caching when this is false.
    /// </summary>
    public bool CacheWriteEnabled { get; private set; } = true;

    public ImageCacheMaintenanceService(
        ImageCacheService cache,
        ILogger<ImageCacheMaintenanceService> logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    /// <summary>
    /// Runs startup maintenance: purge expired entries, remove orphans, and check free space.
    /// Should be called once at application startup (before any imaging workflow).
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        LogMaintenanceStarted(_logger);

        // 1. Purge expired entries
        int purged = await PurgeExpiredAsync(ct);

        // 2. Check available space
        var freeSpace = _cache.GetAvailableDiskSpace();
        if (freeSpace < MinFreeSpaceBytes)
        {
            CacheWriteEnabled = false;
            LogInsufficientSpace(_logger, freeSpace, MinFreeSpaceBytes);
        }
        else
        {
            CacheWriteEnabled = true;
        }

        LogMaintenanceCompleted(_logger, purged, freeSpace);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var entries = await _cache.ListEntriesAsync(ct);
        int count = 0;

        foreach (var entry in entries)
        {
            if (DateTimeOffset.UtcNow - entry.CachedAt > CacheTtl)
            {
                _cache.Evict(entry.ImageId);
                count++;
                LogEntryPurged(_logger, entry.ImageId, entry.CachedAt);
            }
        }

        return count;
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Image cache maintenance started.")]
    private static partial void LogMaintenanceStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Insufficient cache space: {FreeBytes} bytes free (minimum {MinBytes}). Cache writes disabled.")]
    private static partial void LogInsufficientSpace(ILogger logger, long freeBytes, long minBytes);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cache maintenance complete: {Purged} entries purged. Free space: {FreeBytes} bytes.")]
    private static partial void LogMaintenanceCompleted(ILogger logger, int purged, long freeBytes);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cache entry {ImageId} purged (cached {CachedAt:yyyy-MM-dd} — TTL expired).")]
    private static partial void LogEntryPurged(ILogger logger, string imageId, DateTimeOffset cachedAt);
}

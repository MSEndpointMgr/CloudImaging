using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Single-slot local cache for the boot image WIM downloaded by the "Prepare USB Storage
/// Device" workflow (inspired by the legacy OSDLiteDeploy module's local-cache pattern, but
/// simplified: this cache only ever keeps the single most recently used boot image).
///
/// Before downloading, the caller checks the cache by the boot image's SHA-256 hash (the
/// same hash the portal reports for the currently published image). A match means the
/// cached WIM is still current and the download can be skipped entirely; a mismatch means a
/// newer image was published since the cache was written, so the caller downloads fresh and
/// replaces the cached entry — there is only ever one cached version.
/// </summary>
public sealed partial class BootImageCacheService
{
    private const string WimFileName  = "image.wim";
    private const string MetaFileName = "metadata.json";

    private readonly string _cacheDir;
    private readonly ILogger<BootImageCacheService> _logger;

    public BootImageCacheService(ILogger<BootImageCacheService> logger)
        : this(DefaultCacheDir(), logger)
    {
    }

    /// <summary>Test seam — allows pointing the cache at an isolated directory.</summary>
    public BootImageCacheService(string cacheDir, ILogger<BootImageCacheService> logger)
    {
        _cacheDir = cacheDir;
        _logger   = logger;
    }

    private static string DefaultCacheDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cloud Imaging Media Builder", "Cache");

    private sealed record CacheMetadata(
        Guid BootImageId, string Version, string Sha256Hash, long SizeBytes, DateTimeOffset CachedAt);

    /// <summary>
    /// Returns the cached WIM's full path when a cached entry exists and its hash matches
    /// <paramref name="expectedHash"/>; otherwise <c>null</c> (empty cache, unreadable
    /// metadata, or a cached entry for a different/older boot image version).
    /// </summary>
    public async Task<string?> TryGetCachedWimAsync(string expectedHash, CancellationToken ct = default)
    {
        var wimPath  = Path.Combine(_cacheDir, WimFileName);
        var metaPath = Path.Combine(_cacheDir, MetaFileName);

        if (!File.Exists(wimPath) || !File.Exists(metaPath))
        {
            LogCacheMiss(_logger);
            return null;
        }

        CacheMetadata? meta;
        try
        {
            await using var stream = File.OpenRead(metaPath);
            meta = await JsonSerializer.DeserializeAsync<CacheMetadata>(stream, cancellationToken: ct);
        }
        catch
        {
            meta = null;
        }

        if (meta is null || !string.Equals(meta.Sha256Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogCacheStale(_logger);
            return null;
        }

        LogCacheHit(_logger, expectedHash[..Math.Min(8, expectedHash.Length)]);
        return wimPath;
    }

    /// <summary>
    /// Replaces the cached boot image with the file at <paramref name="sourceWimPath"/>. Only
    /// this single entry is ever kept — any previously cached version is overwritten.
    /// </summary>
    public async Task SaveAsync(
        string sourceWimPath, Guid bootImageId, string version, string sha256Hash, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cacheDir);

        var info = new FileInfo(sourceWimPath);
        DiskSpaceGuard.EnsureFreeSpace(_cacheDir, info.Length, "cache the downloaded boot image");

        var wimPath  = Path.Combine(_cacheDir, WimFileName);
        var metaPath = Path.Combine(_cacheDir, MetaFileName);

        File.Copy(sourceWimPath, wimPath, overwrite: true);

        var meta = new CacheMetadata(bootImageId, version, sha256Hash, info.Length, DateTimeOffset.UtcNow);
        await using (var stream = File.Create(metaPath))
            await JsonSerializer.SerializeAsync(stream, meta, cancellationToken: ct);

        LogCacheWritten(_logger, info.Length);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image cache: no cached entry found.")]
    private static partial void LogCacheMiss(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image cache: cached entry does not match the requested boot image — will re-download.")]
    private static partial void LogCacheStale(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image cache hit (hash {HashPrefix}…).")]
    private static partial void LogCacheHit(ILogger logger, string hashPrefix);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image cached ({Bytes} bytes).")]
    private static partial void LogCacheWritten(ILogger logger, long bytes);
}

using CloudImaging.DeviceGatewayApi.Security;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>
/// Singleton in-memory cache for the active boot-media certificate thumbprint (FR-069).
///
/// The cache is refreshed at most once every 60 seconds to balance revocation
/// responsiveness against per-request Table Storage latency.  A per-request
/// Table Storage lookup is <b>not</b> performed on every call.
///
/// Thread-safety: a <see cref="SemaphoreSlim"/> guards the refresh path so only one
/// concurrent caller executes the Table Storage query; other callers return the
/// stale value immediately rather than blocking.
/// </summary>
public sealed partial class BootMediaCertificateThumbprintCache : IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<string?>> _loader;
    private readonly ILogger<BootMediaCertificateThumbprintCache> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;

    private string? _cachedThumbprint;
    private DateTimeOffset _lastRefreshed = DateTimeOffset.MinValue;

    // Expose for testing
    internal DateTimeOffset LastRefreshed => _lastRefreshed;

    /// <param name="loader">
    /// Async delegate that fetches the active thumbprint from the backing store
    /// (Table Storage in production).  Receives a <see cref="CancellationToken"/>.
    /// </param>
    public BootMediaCertificateThumbprintCache(
        Func<CancellationToken, Task<string?>> loader,
        ILogger<BootMediaCertificateThumbprintCache> logger)
    {
        _loader = loader;
        _logger = logger;
    }

    /// <summary>
    /// Returns the currently active thumbprint.  Refreshes from the backing store
    /// when the cache has expired (≥ 60 seconds since the last successful read).
    /// Returns <c>null</c> if no active certificate is found.
    /// </summary>
    public async Task<string?> GetThumbprintAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedThumbprint is not null && (now - _lastRefreshed) < CacheTtl)
        {
            return _cachedThumbprint;
        }

        // Only one concurrent refresh; others fall through with the stale value
        if (!await _refreshLock.WaitAsync(0, ct))
        {
            LogStaleReturn(_logger);
            return _cachedThumbprint;
        }

        try
        {
            // Double-checked locking after acquiring the semaphore
            now = DateTimeOffset.UtcNow;
            if (_cachedThumbprint is not null && (now - _lastRefreshed) < CacheTtl)
            {
                return _cachedThumbprint;
            }

            LogRefreshing(_logger);
            string? thumbprint = await _loader(ct);
            _cachedThumbprint = thumbprint;
            _lastRefreshed    = DateTimeOffset.UtcNow;
            return _cachedThumbprint;
        }
        catch (Exception ex)
        {
            LogRefreshFailed(_logger, ex);
            return _cachedThumbprint;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Forces the next call to <see cref="GetThumbprintAsync"/> to re-read from the
    /// backing store, regardless of the TTL.  Used after a certificate rotation is
    /// committed to Table Storage so the new thumbprint is picked up immediately.
    /// </summary>
    public void Invalidate()
    {
        _lastRefreshed    = DateTimeOffset.MinValue;
        _cachedThumbprint = null;
        LogInvalidated(_logger);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _refreshLock.Dispose();
            _disposed = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache refresh already in progress; returning stale thumbprint.")]
    private static partial void LogStaleReturn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refreshing BootMediaCertificateThumbprintCache from backing store.")]
    private static partial void LogRefreshing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to refresh thumbprint cache; returning stale value.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "BootMediaCertificateThumbprintCache invalidated.")]
    private static partial void LogInvalidated(ILogger logger);
}

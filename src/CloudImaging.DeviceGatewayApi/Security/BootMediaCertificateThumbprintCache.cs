using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>
/// Singleton in-memory cache of the boot-media certificates the gateway accepts (FR-069): the
/// active one, plus the one a rotation replaced while its overlap lasts.
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
    /// <summary>Minimum interval between backing-store refreshes.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<IReadOnlyList<AcceptedBootMediaCertificate>>> _loader;
    private readonly ILogger<BootMediaCertificateThumbprintCache> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;

    private IReadOnlyList<AcceptedBootMediaCertificate> _cached = [];
    private DateTimeOffset _lastRefreshed = DateTimeOffset.MinValue;

    // Expose for testing
    internal DateTimeOffset LastRefreshed => _lastRefreshed;

    /// <param name="loader">
    /// Async delegate that fetches the accepted certificates, active first, from the backing store
    /// (Imaging Core in production). Returns an empty list when none is configured.
    /// </param>
    /// <param name="logger">Logger for refresh/invalidate events.</param>
    public BootMediaCertificateThumbprintCache(
        Func<CancellationToken, Task<IReadOnlyList<AcceptedBootMediaCertificate>>> loader,
        ILogger<BootMediaCertificateThumbprintCache> logger)
    {
        _loader = loader;
        _logger = logger;
    }

    /// <summary>The active certificate's thumbprint, or <c>null</c> if none is configured.</summary>
    public async Task<string?> GetThumbprintAsync(CancellationToken ct = default) =>
        (await GetAcceptedAsync(ct)).FirstOrDefault(c => c.IsActive)?.Thumbprint;

    /// <summary>
    /// The accepted certificates, active first. Refreshes from the backing store when the cache
    /// has expired (≥ 60 seconds since the last successful read). Empty when none is configured.
    /// </summary>
    public async Task<IReadOnlyList<AcceptedBootMediaCertificate>> GetAcceptedAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached.Count > 0 && (now - _lastRefreshed) < CacheTtl)
        {
            return _cached;
        }

        // Only one concurrent refresh; others fall through with the stale value
        if (!await _refreshLock.WaitAsync(0, ct))
        {
            LogStaleReturn(_logger);
            return _cached;
        }

        try
        {
            // Double-checked locking after acquiring the semaphore
            now = DateTimeOffset.UtcNow;
            if (_cached.Count > 0 && (now - _lastRefreshed) < CacheTtl)
            {
                return _cached;
            }

            LogRefreshing(_logger);
            _cached = await _loader(ct);
            _lastRefreshed = DateTimeOffset.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            LogRefreshFailed(_logger, ex);
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Forces the next call to re-read from the backing store, regardless of the TTL.
    /// </summary>
    public void Invalidate()
    {
        _lastRefreshed = DateTimeOffset.MinValue;
        _cached = [];
        LogInvalidated(_logger);
    }

    /// <summary>Releases the refresh lock. Safe to call multiple times.</summary>
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

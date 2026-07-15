using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="BootMediaCertificateThumbprintCache"/> (T163a).
/// These tests use a controllable loader stub to validate caching semantics.
/// </summary>
public sealed class BootMediaCertificateThumbprintCacheTests : IDisposable
{
    private const string Thumbprint1 = "AABBCCDDEEFF0011223344556677889900112233";
    private const string Thumbprint2 = "1122334455667788990011223344556677889900";

    private int _loaderCallCount;
    private string? _storedThumbprint;

    private BootMediaCertificateThumbprintCache BuildCache()
    {
        Task<string?> Loader(CancellationToken ct)
        {
            _loaderCallCount++;
            return Task.FromResult(_storedThumbprint);
        }

        return new BootMediaCertificateThumbprintCache(
            Loader,
            NullLogger<BootMediaCertificateThumbprintCache>.Instance);
    }

    // ── Test 1: First call with empty cache reads from backing store ──────────

    [Fact]
    public async Task GetThumbprintAsync_FirstCallWithEmptyCache_ReadsFromBackingStoreAndReturnsNull()
    {
        // Arrange — no thumbprint stored yet
        _storedThumbprint = null;
        using var cache = BuildCache();

        // Act
        var result = await cache.GetThumbprintAsync();

        // Assert
        result.Should().BeNull();
        _loaderCallCount.Should().Be(1, "loader should be called exactly once on first access");
    }

    [Fact]
    public async Task GetThumbprintAsync_FirstCallWithThumbprintInStore_ReturnsThumbprintAndCallsLoaderOnce()
    {
        // Arrange
        _storedThumbprint = Thumbprint1;
        using var cache = BuildCache();

        // Act
        var result = await cache.GetThumbprintAsync();

        // Assert
        result.Should().Be(Thumbprint1);
        _loaderCallCount.Should().Be(1);
    }

    // ── Test 2: Second call within TTL returns cached value ───────────────────

    [Fact]
    public async Task GetThumbprintAsync_SecondCallWithinTtl_ReturnsCachedValueWithoutBackingStoreRead()
    {
        // Arrange
        _storedThumbprint = Thumbprint1;
        using var cache = BuildCache();

        // Prime the cache
        await cache.GetThumbprintAsync();
        _loaderCallCount = 0; // Reset counter after first call

        // Act — second call immediately after (well within 60s TTL)
        var result = await cache.GetThumbprintAsync();

        // Assert — loader must NOT be called again
        result.Should().Be(Thumbprint1);
        _loaderCallCount.Should().Be(0, "cached value should be returned without a backing-store read");
    }

    // ── Test 3: Call after TTL triggers refresh ───────────────────────────────

    [Fact]
    public async Task GetThumbprintAsync_AfterTtlExpiry_TriggersRefreshFromBackingStore()
    {
        // Arrange — manipulate internal LastRefreshed to simulate TTL expiry
        _storedThumbprint = Thumbprint1;
        using var cache = BuildCache();
        await cache.GetThumbprintAsync(); // prime cache

        // Force TTL expiry by setting LastRefreshed far in the past via Invalidate()
        // (Invalidate() resets the timestamp to DateTimeOffset.MinValue)
        cache.Invalidate();
        _loaderCallCount = 0;
        _storedThumbprint = Thumbprint2; // Backing store now returns a different thumbprint

        // Act
        var result = await cache.GetThumbprintAsync();

        // Assert
        result.Should().Be(Thumbprint2);
        _loaderCallCount.Should().Be(1, "cache should refresh after TTL expiry");
    }

    // ── Test 4: Cache returns updated thumbprint after rotation ───────────────

    [Fact]
    public async Task GetThumbprintAsync_AfterRotationAndInvalidate_ReturnsNewThumbprint()
    {
        // Arrange
        _storedThumbprint = Thumbprint1;
        using var cache = BuildCache();
        var firstResult = await cache.GetThumbprintAsync();
        firstResult.Should().Be(Thumbprint1);

        // Simulate certificate rotation committing to Table Storage
        _storedThumbprint = Thumbprint2;

        // Without invalidation, the old value is still returned (TTL has not expired)
        var cachedStale = await cache.GetThumbprintAsync();
        cachedStale.Should().Be(Thumbprint1, "cached value should be stale before invalidation");

        // Act — invalidate so next read picks up the new thumbprint
        cache.Invalidate();
        _loaderCallCount = 0;

        var rotatedResult = await cache.GetThumbprintAsync();

        // Assert
        rotatedResult.Should().Be(Thumbprint2);
        _loaderCallCount.Should().Be(1, "exactly one backing-store read after invalidation");
    }

    // ── Test 5: TTL constant is ≤ 60 seconds (FR-069 requirement) ────────────

    [Fact]
    public void CacheTtl_MustNotExceed60Seconds()
    {
        BootMediaCertificateThumbprintCache.CacheTtl.TotalSeconds
            .Should().BeLessOrEqualTo(60,
                "FR-069 requires certificate revocation to take effect within 60 seconds");
    }

    public void Dispose() { /* nothing additional */ }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Drivers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests.Drivers;

public sealed class DriverFileCacheTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-drvcache-" + Guid.NewGuid().ToString("N"));

    public DriverFileCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CacheRoot => Path.Combine(_root, "CloudImagingCache");

    private DriverFileCache Cache(long free = long.MaxValue) => new(CacheRoot, NullLogger.Instance, _ => free);

    private (string Sha, string Path) Source(string name = "sp200.exe", string content = "softpaq")
    {
        var path = System.IO.Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), path);
    }

    private string Entry(string sha) => Path.Combine(CacheRoot, "drivers", sha);

    [Fact]
    public async Task AVerifiedDownload_IsWrittenUnderItsHash_WithMetadataLast()
    {
        var (sha, path) = Source();

        await Cache().AddAsync(sha, path);

        File.ReadAllText(Path.Combine(Entry(sha), "sp200.exe")).Should().Be("softpaq");
        var metadata = Path.Combine(Entry(sha), "metadata.json");
        File.Exists(metadata).Should().BeTrue();
        File.GetLastWriteTimeUtc(metadata).Should().BeOnOrAfter(File.GetLastWriteTimeUtc(Path.Combine(Entry(sha), "sp200.exe")));
        JsonSerializer.Deserialize<DriverCacheMetadata>(File.ReadAllText(metadata), Json)!.FileName.Should().Be("sp200.exe");
    }

    [Fact]
    public async Task AHit_IsCopiedToTheComponentFolder_AndHashedThere()
    {
        var (sha, path) = Source();
        var cache = Cache();
        await cache.AddAsync(sha, path);
        var destination = Path.Combine(_root, "W", "000-sp200", "sp200.exe");

        (await cache.TryCopyAsync(sha, destination)).Should().BeTrue();

        File.ReadAllText(destination).Should().Be("softpaq");
    }

    [Fact]
    public async Task ACopyThatFailsTheCheck_DeletesTheEntry_AndIsAMiss()
    {
        var (sha, path) = Source();
        var cache = Cache();
        await cache.AddAsync(sha, path);
        File.WriteAllText(Path.Combine(Entry(sha), "sp200.exe"), "tampered");
        var destination = Path.Combine(_root, "W", "sp200.exe");

        (await cache.TryCopyAsync(sha, destination)).Should().BeFalse();

        Directory.Exists(Entry(sha)).Should().BeFalse();
        File.Exists(destination).Should().BeFalse();
    }

    [Fact]
    public async Task BelowFiveHundredMegabytes_NothingIsWritten_ButLookupsStillWork()
    {
        var (sha, path) = Source();
        await Cache().AddAsync(sha, path);
        var (other, otherPath) = Source("sp201.exe", "other");
        var low = Cache(DriverFileCache.MinFreeBytes - 1);

        await low.AddAsync(other, otherPath);

        Directory.Exists(Entry(other)).Should().BeFalse();
        (await low.TryCopyAsync(sha, Path.Combine(_root, "W", "sp200.exe"))).Should().BeTrue();
    }

    [Fact]
    public async Task EntriesWithoutMetadata_AreRemovedWhenTheStepStarts_AndNothingElseIsEvicted()
    {
        var (sha, path) = Source();
        var cache = Cache();
        await cache.AddAsync(sha, path);
        var partial = Entry(new string('a', 64));
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "half.exe"), "x");

        cache.Prepare();

        Directory.Exists(partial).Should().BeFalse();
        Directory.Exists(Entry(sha)).Should().BeTrue("valid entries are never evicted during a step");
    }

    [Fact]
    public async Task WithoutACacheVolume_NothingIsReadOrWritten()
    {
        var (sha, path) = Source();
        var cache = new DriverFileCache(null, NullLogger.Instance);

        await cache.AddAsync(sha, path);

        cache.IsAvailable.Should().BeFalse();
        (await cache.TryCopyAsync(sha, Path.Combine(_root, "W", "x.exe"))).Should().BeFalse();
        Directory.Exists(CacheRoot).Should().BeFalse();
    }

    [Fact]
    public async Task AHashThatIsNotHex_NeverMapsToAFolder()
    {
        var (_, path) = Source();

        await Cache().AddAsync(@"..\..\escape", path);

        Directory.Exists(Path.Combine(CacheRoot, "drivers")).Should().BeFalse();
    }

    [Fact]
    public async Task MaintenancePurgesDriverEntries_IdleForThirtyDays()
    {
        var (sha, path) = Source();
        var (fresh, freshPath) = Source("sp201.exe", "fresh");
        await Cache().AddAsync(sha, path);
        await Cache().AddAsync(fresh, freshPath);
        var stale = new DriverCacheMetadata(sha, "sp200.exe", 7, DateTimeOffset.UtcNow.AddDays(-31));
        File.WriteAllText(Path.Combine(Entry(sha), "metadata.json"), JsonSerializer.Serialize(stale, Json));
        var maintenance = new ImageCacheMaintenanceService(new ImageCacheService(CacheRoot, NullLogger<ImageCacheService>.Instance), NullLogger<ImageCacheMaintenanceService>.Instance);

        await maintenance.RunAsync();

        Directory.Exists(Entry(sha)).Should().BeFalse();
        Directory.Exists(Entry(fresh)).Should().BeTrue();
    }
}

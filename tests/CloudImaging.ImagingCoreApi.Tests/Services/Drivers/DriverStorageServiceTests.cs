using System.Net;
using System.Security.Cryptography;
using System.Web;
using Azure.Storage.Blobs;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverStorageServiceTests
{
    private const string HpUrl = "https://ftp.hp.com/pub/softpaq/sp150001-150500/sp150123.exe";
    private const string DellUrl = "https://downloads.dell.com/FOLDER1/1/Network_Driver.EXE";
    private static readonly byte[] Payload = Enumerable.Range(0, 5 * 1024 * 1024 + 123).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));

    private readonly DriverTestBed _bed = new();
    private readonly DriverStorageService _storage;

    public DriverStorageServiceTests()
    {
        _storage = new DriverStorageService(new BlobServiceClient("UseDevelopmentStorage=true"), _bed.Blobs, _bed.Http, _bed.Time);
    }

    [Theory]
    [InlineData(HpUrl)]
    [InlineData(DellUrl)]
    public async Task DownloadAsync_AllowedHostAndMatchingHash_CommitsTheFile(string url)
    {
        _bed.Vendor.ServeBytes(url, Payload);

        var outcome = await _storage.DownloadAsync(url, "files/hp/sp150123/1.0/sp150123.exe", PayloadSha, Payload.Length);

        outcome.Succeeded.Should().BeTrue();
        outcome.Sha256.Should().Be(PayloadSha);
        _bed.Blobs.Blobs["files/hp/sp150123/1.0/sp150123.exe"].ToArray().Should().Equal(Payload);
        _bed.Blobs.Metadata["files/hp/sp150123/1.0/sp150123.exe"]["sha256"].Should().Be(PayloadSha);
    }

    [Theory]
    [InlineData("http://ftp.hp.com/pub/sp1.exe", "*not https*")]
    [InlineData("https://user:secret@ftp.hp.com/pub/sp1.exe", "*credentials*")]
    [InlineData("https://downloads.example.com/sp1.exe", "*downloads.example.com is not an allowed vendor host*")]
    public async Task DownloadAsync_DisallowedUrl_IsRefusedWithoutARequest(string url, string reason)
    {
        var outcome = await _storage.DownloadAsync(url, "files/x", PayloadSha, null);

        outcome.Succeeded.Should().BeFalse();
        outcome.Reason.Should().Match(reason);
        _bed.Vendor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_RedirectToAnotherHost_IsRefused()
    {
        _bed.Vendor.ServeStatus(HpUrl, HttpStatusCode.Found, "https://mirror.example.net/sp150123.exe");

        var outcome = await _storage.DownloadAsync(HpUrl, "files/x", PayloadSha, null);

        outcome.Succeeded.Should().BeFalse();
        outcome.Reason.Should().Contain("mirror.example.net");
        _bed.Blobs.Blobs.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_RedirectWithinAllowlist_IsFollowed()
    {
        _bed.Vendor.ServeStatus(HpUrl, HttpStatusCode.MovedPermanently, "https://ftp.ext.hp.com/pub/sp150123.exe");
        _bed.Vendor.ServeBytes("https://ftp.ext.hp.com/pub/sp150123.exe", Payload);

        var outcome = await _storage.DownloadAsync(HpUrl, "files/x", PayloadSha, Payload.Length);

        outcome.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_LeavesNothingVisibleAndGivesTheReason()
    {
        _bed.Vendor.ServeBytes(HpUrl, Payload);

        var outcome = await _storage.DownloadAsync(HpUrl, "files/x", new string('0', 64), Payload.Length);

        outcome.Succeeded.Should().BeFalse();
        outcome.Reason.Should().Contain("SHA-256");
        outcome.Sha256.Should().Be(PayloadSha);
        _bed.Blobs.Blobs.Should().NotContainKey("files/x");
    }

    [Fact]
    public async Task DownloadAsync_LengthMismatch_LeavesNothingVisible()
    {
        _bed.Vendor.ServeBytes(HpUrl, Payload);

        var outcome = await _storage.DownloadAsync(HpUrl, "files/x", PayloadSha, Payload.Length + 10);

        outcome.Succeeded.Should().BeFalse();
        outcome.Reason.Should().Contain("bytes");
        _bed.Blobs.Blobs.Should().NotContainKey("files/x");
    }

    [Fact]
    public async Task DownloadAsync_VendorError_GivesTheStatus()
    {
        _bed.Vendor.ServeStatus(DellUrl, HttpStatusCode.ServiceUnavailable);

        var outcome = await _storage.DownloadAsync(DellUrl, "files/x", PayloadSha, null);

        outcome.Reason.Should().Be("downloads.dell.com answered 503.");
    }

    [Fact]
    public async Task GetReadLinkAsync_OneBlob_ExpiresAfterSixtyMinutes()
    {
        var (url, expiresAt) = await _storage.GetReadLinkAsync("files/hp/sp150123/1.0/sp150123.exe");

        expiresAt.Should().Be(DriverTestBed.Start + TimeSpan.FromMinutes(60));
        var uri = new Uri(url);
        uri.AbsolutePath.Should().EndWith($"/{DriverTables.Container}/files/hp/sp150123/1.0/sp150123.exe");
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["sr"].Should().Be("b");
        query["sp"].Should().Be("r");
        DateTimeOffset.Parse(query["se"]!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task EnsureContainerAsync_Missing_CreatesTheDriversContainer()
    {
        await _storage.EnsureContainerAsync();

        _bed.Blobs.Created.Should().BeTrue();
        _bed.Blobs.Name.Should().Be("drivers");
    }

    [Fact]
    public async Task DownloadCatalogAsync_Fixture_ReturnsTheBytes()
    {
        _bed.Vendor.ServeFixture("https://hpia.hpcloud.hp.com/ref/platformList.cab", "platformList.cab");

        var (content, failure) = await _storage.DownloadCatalogAsync("https://hpia.hpcloud.hp.com/ref/platformList.cab");

        failure.Should().BeNull();
        content.Should().Equal(CabinetReaderTests.ReadFixture("platformList.cab"));
    }
}

using System.Security.Cryptography;
using Azure.Identity;
using Azure.Storage.Blobs;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>Runs only when the CI staging job provides a Core storage account; skipped otherwise.</summary>
internal sealed class StagingStorageFactAttribute : FactAttribute
{
    public const string AccountVariable = "CI_STAGING_CORE_STORAGE_ACCOUNT";

    public StagingStorageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccountVariable)))
        {
            Skip = $"Set {AccountVariable} to run against the staging Core storage account.";
        }
    }
}

/// <summary>
/// <see cref="DriverStorageService"/> against real Blob Storage with the CI's short-lived identity.
/// Vendor HTTP stays served from fixtures; every blob written is deleted afterwards.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DriverStorageLiveTests : IAsyncLifetime
{
    private const string VendorUrl = "https://hpia.hpcloud.hp.com/ref/8c26/8c26_64_11.0.24h2.cab";
    private readonly string _prefix = $"ci-tests/{Guid.NewGuid():N}/";
    private BlobServiceClient? _service;
    private BlobContainerClient? _container;

    public Task InitializeAsync()
    {
        var account = Environment.GetEnvironmentVariable(StagingStorageFactAttribute.AccountVariable);
        if (!string.IsNullOrWhiteSpace(account))
        {
            _service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), new DefaultAzureCredential());
            _container = _service.GetBlobContainerClient(DriverTables.Container);
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_container is null)
        {
            return;
        }

        await foreach (var blob in _container.GetBlobsAsync(prefix: _prefix))
        {
            await _container.DeleteBlobIfExistsAsync(blob.Name);
        }
    }

    [StagingStorageFact]
    public async Task DownloadAsync_MatchingHash_IsCommittedAndReadableThroughItsReadLink()
    {
        var (storage, fixture) = CreateStorage();
        await storage.EnsureContainerAsync();
        var blobName = $"{_prefix}match.cab";

        var outcome = await storage.DownloadAsync(VendorUrl, blobName, Convert.ToHexStringLower(SHA256.HashData(fixture)), fixture.Length);

        outcome.Succeeded.Should().BeTrue();
        var (url, _) = await storage.GetReadLinkAsync(blobName);
        using var http = new HttpClient();
        (await http.GetByteArrayAsync(new Uri(url))).Should().Equal(fixture);
    }

    [StagingStorageFact]
    public async Task DownloadAsync_MismatchingHash_LeavesNoCommittedBlob()
    {
        var (storage, fixture) = CreateStorage();
        await storage.EnsureContainerAsync();
        var blobName = $"{_prefix}mismatch.cab";

        var outcome = await storage.DownloadAsync(VendorUrl, blobName, new string('0', 64), fixture.Length);

        outcome.Succeeded.Should().BeFalse();
        (await _container!.GetBlobClient(blobName).ExistsAsync()).Value.Should().BeFalse();
    }

    private (DriverStorageService Storage, byte[] Fixture) CreateStorage()
    {
        var fixture = CabinetReaderTests.ReadFixture("8c26_64_11.0.24h2.cab");
        var vendor = new FakeVendorHandler().ServeBytes(VendorUrl, fixture);
        var storage = new DriverStorageService(_service!, _container!, new HttpClient(vendor), TimeProvider.System);
        return (storage, fixture);
    }
}

using System.Text;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>Uploaded texts are stored once per SHA-256 with their upload time and kind.</summary>
public sealed class SetupProfileContentRepositoryTests
{
    private readonly InMemoryBlobContainerClient _blobs = new();
    private readonly SetupProfileContentRepository _content;

    public SetupProfileContentRepositoryTests() => _content = new SetupProfileContentRepository(_blobs);

    [Fact]
    public async Task Store_WritesContentSlashHash_WithUploadTimeAndKind()
    {
        var at = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

        var stored = await _content.StoreAsync(Encoding.UTF8.GetBytes("exit 0"), "script", at);

        stored.Sha256.Should().Be(SetupProfileTestBed.Sha("exit 0"));
        stored.SizeBytes.Should().Be(6);
        _blobs.Blobs.Keys.Should().Equal($"content/{stored.Sha256}");
        _blobs.Metadata[$"content/{stored.Sha256}"].Should().Contain("kind", "script");
        (await _content.GetInfoAsync(stored.Sha256))!.UploadedAt.Should().Be(at);
    }

    [Fact]
    public async Task Store_AnExistingHash_IsSuccess_AndRefreshesTheUploadTime()
    {
        var first = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        await _content.StoreAsync(Encoding.UTF8.GetBytes("exit 0"), "script", first);

        var again = await _content.StoreAsync(Encoding.UTF8.GetBytes("exit 0"), "script", first.AddDays(4));

        _blobs.Blobs.Should().ContainSingle();
        (await _content.GetInfoAsync(again.Sha256))!.UploadedAt.Should().Be(first.AddDays(4));
    }

    [Fact]
    public async Task ReadsListAndDelete_WorkByHash()
    {
        var stored = await _content.StoreAsync(Encoding.UTF8.GetBytes("Write-Output hi"), "script", DateTimeOffset.UnixEpoch);

        (await _content.DownloadAsync(stored.Sha256))!.ToString().Should().Be("Write-Output hi");
        var listed = new List<SetupProfileContentInfo>();
        await foreach (var item in _content.ListAsync())
        {
            listed.Add(item);
        }
        listed.Should().ContainSingle().Which.Should().Be(new SetupProfileContentInfo(stored.Sha256, 15, DateTimeOffset.UnixEpoch));

        await _content.DeleteAsync(stored.Sha256);
        (await _content.GetInfoAsync(stored.Sha256)).Should().BeNull();
        (await _content.DownloadAsync(stored.Sha256)).Should().BeNull();
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("../../000001.json", false)]
    [InlineData(null, false)]
    public void IsSha256_AcceptsOnly64LowercaseHexCharacters(string? value, bool expected)
    {
        SetupProfileContentRepository.IsSha256(value).Should().Be(expected);
    }
}

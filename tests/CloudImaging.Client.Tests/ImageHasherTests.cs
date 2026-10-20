using System.IO;
using System.Security.Cryptography;
using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The buffered hasher replaced <c>SHA256.HashDataAsync(File.OpenRead(path))</c>, which read a
/// multi-gigabyte image through a 4 KB buffer and took over five minutes on USB media. These
/// tests pin the digest against the framework's own, and pin the progress contract the apply
/// step relies on to show that verification is moving.
/// </summary>
public sealed class ImageHasherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ci-hash-{Guid.NewGuid():N}");

    public ImageHasherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string ExpectedHash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    [Fact]
    public async Task ComputeAsync_MatchesTheFrameworkDigest_ForAFileSpanningManyBuffers()
    {
        // Deliberately not a multiple of the 1 MiB buffer, so the final short read is covered.
        var content = new byte[(3 * 1024 * 1024) + 12_345];
        Random.Shared.NextBytes(content);
        var path = WriteFile("multi-buffer.wim", content);

        var actual = await ImageHasher.ComputeAsync(path);

        actual.Should().Be(ExpectedHash(content));
    }

    [Fact]
    public async Task ComputeAsync_MatchesTheFrameworkDigest_ForAnEmptyFile()
    {
        var path = WriteFile("empty.wim", []);

        var actual = await ImageHasher.ComputeAsync(path);

        actual.Should().Be(ExpectedHash([]));
    }

    [Fact]
    public async Task ComputeAsync_ReportsZeroFirstThenRisesToOneHundred()
    {
        var content = new byte[(5 * 1024 * 1024) + 7];
        Random.Shared.NextBytes(content);
        var path = WriteFile("progress.wim", content);
        var reported = new List<int>();

        await ImageHasher.ComputeAsync(path, reported.Add);

        // Zero up front matters: the operator sees the bar appear immediately rather than after
        // the first megabyte, which is what made verification look like a stall.
        reported.Should().StartWith([0]);
        reported.Should().EndWith([100]);
        reported.Should().BeInAscendingOrder();
        reported.Should().OnlyHaveUniqueItems("a repeated percentage is pointless UI churn");
    }

    [Fact]
    public async Task ComputeAsync_IsCancellable()
    {
        var content = new byte[8 * 1024 * 1024];
        var path = WriteFile("cancel.wim", content);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await ImageHasher.ComputeAsync(path, onProgress: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ComputeAsync_ReadsAFileAnotherHandleHasOpen()
    {
        // The cached WIM can still be held open by the downloader that just wrote it.
        var content = new byte[1024];
        var path = WriteFile("shared.wim", content);
        await using var concurrent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var actual = await ImageHasher.ComputeAsync(path);

        actual.Should().Be(ExpectedHash(content));
    }
}

using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Boot image download retry-with-backoff tests (T069, FR-056, FR-058).
///
/// A hash mismatch (or other transient failure) must retry the whole download+verify attempt
/// up to 3 times, discarding the corrupted file between attempts, and on final exhaustion
/// abort with a BID-stage support reference code (T160).
/// </summary>
public sealed class BootImageDownloadRetryTests
{
    [Fact]
    public async Task DownloadAsync_RetriesOnHashMismatch_ThenSucceeds()
    {
        var goodBytes = "boot-image-content"u8.ToArray();
        var badBytes  = "corrupted"u8.ToArray();
        var goodHash  = Convert.ToHexString(SHA256.HashData(goodBytes)).ToLowerInvariant();

        var handler  = new SequencedHttpMessageHandler(badBytes, goodBytes);
        var svc      = new BootImageDownloadService(new HttpClient(handler), NullLogger<BootImageDownloadService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-dl-test-{Guid.NewGuid():N}.wim");

        try
        {
            await svc.DownloadAsync("https://example.com/fake-sas", goodHash, destPath, CancellationToken.None);

            File.ReadAllBytes(destPath).Should().BeEquivalentTo(goodBytes,
                "the retried attempt must overwrite the corrupted first attempt with the correct content");
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }

    [Fact]
    public async Task DownloadAsync_EmbedsSupportReferenceCode_AfterExhaustingRetries()
    {
        var badBytes = "always-corrupted"u8.ToArray();
        var handler  = new SequencedHttpMessageHandler(badBytes);
        var svc      = new BootImageDownloadService(new HttpClient(handler), NullLogger<BootImageDownloadService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-dl-test-{Guid.NewGuid():N}.wim");

        Func<Task> act = () => svc.DownloadAsync(
            "https://example.com/fake-sas",
            expectedHash: new string('0', 64),
            destinationPath: destPath,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CMB-PREPUSB-BID-*",
                "every download failure must surface a BID-stage support reference code (T160, FR-058)");

        File.Exists(destPath).Should().BeFalse("the corrupted file must be discarded, not left on disk");
    }

    /// <summary>Returns the Nth (clamped) response body from a fixed sequence, one per call — simulates a corrupted attempt followed by a good one.</summary>
    private sealed class SequencedHttpMessageHandler(params byte[][] responses) : HttpMessageHandler
    {
        private int _callIndex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = responses[Math.Min(_callIndex, responses.Length - 1)];
            _callIndex++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}

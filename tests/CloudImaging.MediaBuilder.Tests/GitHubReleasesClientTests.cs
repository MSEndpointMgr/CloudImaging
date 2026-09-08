using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for <see cref="GitHubReleasesClient"/>'s resolution of the "mse-ci-client-latest" alias
/// release (release-client.yml keeps this pointed at the newest stable Client release, so
/// Media Builder's "Automatic download" source option never needs GitHub's repo-wide
/// /releases/latest, which would be unsafe once the iac/mediabuilder streams interleave).
/// </summary>
public sealed class GitHubReleasesClientTests
{
    private const string ClientLatestApiUrl =
        "https://api.github.com/repos/MSEndpointMgr/CloudImaging/releases/tags/mse-ci-client-latest";

    [Fact]
    public async Task DownloadLatestClientAsync_ResolvesViaClientLatestAlias_AndExtractsAsset()
    {
        var requestedUrls = new List<string>();
        var zipBytes = CreateFakeClientZip();
        var validSums = $"{Convert.ToHexStringLower(SHA256.HashData(zipBytes))}  CloudImaging.Client.zip\n";

        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.AbsoluteUri);

            if (req.RequestUri!.AbsoluteUri == ClientLatestApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent(
                        """
                        {
                          "tag_name": "mse-ci-client-latest",
                          "name": "Cloud Imaging Client (latest — mse-ci-client-v1.2.3)",
                          "assets": [
                            { "name": "CloudImaging.Client.zip", "browser_download_url": "https://example.com/download/CloudImaging.Client.zip" },
                            { "name": "SHA256SUMS", "browser_download_url": "https://example.com/download/SHA256SUMS" }
                          ]
                        }
                        """)
                };
            }

            if (req.RequestUri!.AbsoluteUri == "https://example.com/download/CloudImaging.Client.zip")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            }

            if (req.RequestUri!.AbsoluteUri == "https://example.com/download/SHA256SUMS")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(validSums) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var svc = new GitHubReleasesClient(new HttpClient(handler), NullLogger<GitHubReleasesClient>.Instance);

        var extractDir = await svc.DownloadLatestClientAsync();

        try
        {
            requestedUrls.Should().Contain(ClientLatestApiUrl,
                "the client must resolve the moving 'mse-ci-client-latest' alias release, never GitHub's repo-wide /releases/latest");
            requestedUrls.Should().Contain("https://example.com/download/SHA256SUMS",
                "the download must be verified against the release's published checksum before use");
            Directory.Exists(extractDir).Should().BeTrue();
            File.Exists(Path.Combine(extractDir, "CloudImaging.Client.exe")).Should().BeTrue(
                "the downloaded CloudImaging.Client.zip asset must be extracted into the returned directory");
        }
        finally
        {
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadLatestClientAsync_ThrowsAndCleansUpTempDir_WhenChecksumDoesNotMatch()
    {
        var zipBytes = CreateFakeClientZip();
        const string wrongSums = "0000000000000000000000000000000000000000000000000000000000000000  CloudImaging.Client.zip\n";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == ClientLatestApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent(
                        """
                        {
                          "tag_name": "mse-ci-client-latest",
                          "name": "Cloud Imaging Client (latest — mse-ci-client-v1.2.3)",
                          "assets": [
                            { "name": "CloudImaging.Client.zip", "browser_download_url": "https://example.com/download/CloudImaging.Client.zip" },
                            { "name": "SHA256SUMS", "browser_download_url": "https://example.com/download/SHA256SUMS" }
                          ]
                        }
                        """)
                };
            }

            if (req.RequestUri!.AbsoluteUri == "https://example.com/download/CloudImaging.Client.zip")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            }

            if (req.RequestUri!.AbsoluteUri == "https://example.com/download/SHA256SUMS")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wrongSums) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var tempDirsBefore = Directory.GetDirectories(Path.GetTempPath(), "ci-client-*");

        var svc = new GitHubReleasesClient(new HttpClient(handler), NullLogger<GitHubReleasesClient>.Instance);

        Func<Task> act = async () => await svc.DownloadLatestClientAsync();

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*after 3 attempts*");
        assertion.Which.InnerException.Should().NotBeNull();
        assertion.Which.InnerException!.Message.Should().Contain("SHA-256",
            "a mismatched checksum must never be silently ignored and extracted into a boot image");

        Directory.GetDirectories(Path.GetTempPath(), "ci-client-*").Should().BeEquivalentTo(tempDirsBefore,
            "every failed attempt's extraction directory must be cleaned up, not leaked into %TEMP%");
    }

    [Fact]
    public async Task DownloadLatestClientAsync_ThrowsActionableError_WhenClientLatestAliasDoesNotExist()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = new GitHubReleasesClient(new HttpClient(handler), NullLogger<GitHubReleasesClient>.Instance);

        Func<Task> act = async () => await svc.DownloadLatestClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mse-ci-client-latest*",
                "a missing alias release (no stable Client version ever published) must surface a clear, actionable error instead of a generic HTTP failure");
    }

    private static byte[] CreateFakeClientZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("CloudImaging.Client.exe");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("fake client binary");
        }
        return ms.ToArray();
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

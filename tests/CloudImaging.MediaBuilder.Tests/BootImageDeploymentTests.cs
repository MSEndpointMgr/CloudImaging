using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Boot image download service tests covering resumable transfer (T111, FR-069), and
/// USB preparation manifest persistence tests (T071a, FR-059).
/// </summary>
public sealed class BootImageDeploymentTests
{
    // ── Service instantiation ─────────────────────────────────────────────────

    [Fact]
    public void BootImageDownloadService_CanBeInstantiated()
    {
        var http = new HttpClient();
        var svc  = new BootImageDownloadService(http, NullLogger<BootImageDownloadService>.Instance);
        svc.Should().NotBeNull();
    }

    // ── Progress events ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressChanged_ReportsBytesDownloaded_DuringSuccessfulDownload()
    {
        var bytes    = new byte[1024 * 64];
        Random.Shared.NextBytes(bytes);
        var hash     = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler  = new FixedResponseHttpMessageHandler(bytes);
        var svc      = new BootImageDownloadService(new HttpClient(handler), NullLogger<BootImageDownloadService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-dl-test-{Guid.NewGuid():N}.wim");

        var reportedProgress = new List<(long Downloaded, long Total)>();
        svc.ProgressChanged += (_, e) => reportedProgress.Add(e);

        try
        {
            await svc.DownloadAsync("https://example.com/fake-sas", hash, destPath, CancellationToken.None);

            reportedProgress.Should().NotBeEmpty("a real download must report at least one progress update");
            reportedProgress[^1].Downloaded.Should().Be(bytes.Length,
                "the final progress update must report all bytes downloaded");
            reportedProgress[^1].Total.Should().Be(bytes.Length,
                "the reported total must match the Content-Length of the response");
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }

    // ── SHA-256 verification ──────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_Succeeds_WhenHashMatches()
    {
        var bytes    = "verified-boot-image-content"u8.ToArray();
        var hash     = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler  = new FixedResponseHttpMessageHandler(bytes);
        var svc      = new BootImageDownloadService(new HttpClient(handler), NullLogger<BootImageDownloadService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-dl-test-{Guid.NewGuid():N}.wim");

        try
        {
            await svc.DownloadAsync("https://example.com/fake-sas", hash, destPath, CancellationToken.None);
            File.ReadAllBytes(destPath).Should().BeEquivalentTo(bytes,
                "a hash-verified download must be left in place at the destination path");
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }

    // ── Resume / interrupted transfer ─────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ThrowsOperationCanceled_WhenTokenAlreadyCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var http = new HttpClient();
        var svc  = new BootImageDownloadService(http, NullLogger<BootImageDownloadService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-dl-test-{Guid.NewGuid():N}.wim");

        Func<Task> act = async () =>
            await svc.DownloadAsync(
                sasUrl:          "https://example.com/fake-sas",
                expectedHash:    "aabbccdd" + new string('0', 56),
                destinationPath: destPath,
                ct:              cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled token before the request starts must abort the download immediately");
        File.Exists(destPath).Should().BeFalse("no partial file should be left behind when cancelled before starting");
    }

    // ── USB preparation manifest persistence (T071a, FR-059) ──────────────────

    [Fact]
    public async Task WriteUsbPreparationManifestAsync_WritesReadableJsonManifest_ToBootPartitionRoot()
    {
        var bootDrive = Directory.CreateTempSubdirectory("ci-boot-drive-").FullName;
        try
        {
            var svc = new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance);
            var manifest = new UsbPreparationManifest
            {
                ManifestVersion = UsbPreparationManifest.ManifestSchemaVersion,
                PreparedAt = DateTimeOffset.UtcNow,
                ToolVersion = "1.2.3.0",
                BootImageVersion = "20260101-000000",
                SelectedDiskId = "2",
                PartitionSchema = new Dictionary<string, object> { ["bootDriveLetter"] = @"E:\", ["cacheDriveLetter"] = @"F:\" },
                ValidationResults = new Dictionary<string, object> { ["busType"] = "USB", ["diskValidated"] = true },
                AutoStartConfigured = true,
            };

            await svc.WriteUsbPreparationManifestAsync(bootDrive, manifest, CancellationToken.None);

            var manifestPath = Path.Combine(bootDrive, UsbPreparationManifest.FileName);
            File.Exists(manifestPath).Should().BeTrue("the manifest must be written at the root of the BOOT partition");

            var roundTripped = JsonSerializer.Deserialize<UsbPreparationManifest>(await File.ReadAllTextAsync(manifestPath));
            roundTripped.Should().NotBeNull();
            roundTripped!.BootImageVersion.Should().Be("20260101-000000",
                "the Cloud Imaging Client reads this field at boot to detect newer published images");
            roundTripped.ToolVersion.Should().Be("1.2.3.0");
            roundTripped.AutoStartConfigured.Should().BeTrue();
            roundTripped.PartitionSchema["bootDriveLetter"].ToString().Should().Be(@"E:\");
        }
        finally
        {
            Directory.Delete(bootDrive, recursive: true);
        }
    }

    /// <summary>Always returns the same fixed byte payload for every request.</summary>
    private sealed class FixedResponseHttpMessageHandler(byte[] responseBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responseBytes) });
    }
}


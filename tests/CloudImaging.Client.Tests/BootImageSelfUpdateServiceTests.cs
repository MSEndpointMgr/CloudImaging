using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="BootImageSelfUpdateService"/> (T071b, FR-059a).
///
/// <see cref="BootImageSelfUpdateService.CheckAndUpdateAsync"/>'s first step locates the
/// BOOT-labelled USB volume via WMI, which has no seam for faking in a unit test and never
/// resolves to anything on a dev/CI machine (no such volume exists). Its full happy path
/// (version-mismatch triggers download+verify+overwrite) therefore has no automated coverage,
/// and no documented manual validation procedure either —
/// specs/001-cloud-windows-imaging/validation-usb-autostart-matrix.md only checks that the
/// manifest exists after USB preparation, not that self-update ever runs correctly. These tests
/// instead cover what IS safely verifiable in-process: (1) the "never throws" contract that
/// makes it safe to fire-and-forget at Client startup, and (2) the download/hash/manifest-merge/
/// architecture-comparison helper methods in isolation via reflection, since they contain the
/// actual decision and I/O logic.
/// </summary>
public sealed class BootImageSelfUpdateServiceTests
{
    [Fact]
    public async Task CheckAndUpdateAsync_CompletesWithoutThrowing_WhenNoBootVolumeIsPresent()
    {
        // On a dev/CI machine there is no BOOT-labelled volume, so this exercises the real
        // early-return path — the important regression guard is that NOTHING inside
        // CheckAndUpdateAsync's try/catch escapes it, since it is invoked fire-and-forget
        // (never awaited) from App.xaml.cs and must never crash the Client at startup.
        var gateway = new DeviceGatewayApiClient(new HttpClient { BaseAddress = new Uri("https://gw.example.com") });
        var svc = new BootImageSelfUpdateService(gateway, new HttpClient(), NullLogger<BootImageSelfUpdateService>.Instance);

        var act = async () => await svc.CheckAndUpdateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DownloadAsync_WritesResponseBytes_ToDestinationFile()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var gateway = new DeviceGatewayApiClient(new HttpClient { BaseAddress = new Uri("https://gw.example.com") });
        var svc = new BootImageSelfUpdateService(gateway, new HttpClient(handler), NullLogger<BootImageSelfUpdateService>.Instance);
        var destPath = Path.Combine(Path.GetTempPath(), $"ci-selfupdate-test-{Guid.NewGuid():N}.wim");

        try
        {
            var method = typeof(BootImageSelfUpdateService).GetMethod(
                "DownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(svc, ["https://sas.example.com/x", destPath, CancellationToken.None])!;

            File.Exists(destPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload,
                "the downloaded response body must be written byte-for-byte to the destination file");
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }

    [Fact]
    public async Task ComputeSha256Async_ReturnsLowercaseHexDigest_MatchingTheFileContents()
    {
        var content = new byte[] { 10, 20, 30, 40, 50 };
        var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var filePath = Path.Combine(Path.GetTempPath(), $"ci-selfupdate-hash-test-{Guid.NewGuid():N}.wim");
        await File.WriteAllBytesAsync(filePath, content);

        try
        {
            var method = typeof(BootImageSelfUpdateService).GetMethod(
                "ComputeSha256Async", BindingFlags.Static | BindingFlags.NonPublic)!;
            var actualHash = await (Task<string>)method.Invoke(null, [filePath, CancellationToken.None])!;

            actualHash.Should().Be(expectedHash);
            actualHash.Should().MatchRegex("^[0-9a-f]{64}$", "SHA-256 hex digest must be lowercase and 64 characters");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public void BuildUpdatedManifest_PreservesEveryFieldExceptBootImageVersion()
    {
        // Regression guard: an earlier version of this method rebuilt the manifest field-by-field
        // and silently dropped LocationId/LocationName, wiping the technician-selected location
        // label off the USB stick the first time self-update ever ran on it.
        var current = new UsbPreparationManifest
        {
            ManifestVersion = "1.0",
            PreparedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ToolVersion = "1.2.3",
            BootImageVersion = "2026.01.01",
            SelectedDiskId = "disk-1",
            LocationId = Guid.NewGuid(),
            LocationName = "Seattle HQ",
            Architecture = "x64",
            PartitionSchema = new Dictionary<string, object> { ["bootDriveLetter"] = "X:" },
            ValidationResults = new Dictionary<string, object> { ["busType"] = "USB" },
            AutoStartConfigured = true,
        };

        var method = typeof(BootImageSelfUpdateService).GetMethod(
            "BuildUpdatedManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var updated = (UsbPreparationManifest)method.Invoke(null, [current, "2026.02.01"])!;

        updated.BootImageVersion.Should().Be("2026.02.01", "only the boot image version changes");
        updated.ManifestVersion.Should().Be(current.ManifestVersion);
        updated.PreparedAt.Should().Be(current.PreparedAt);
        updated.ToolVersion.Should().Be(current.ToolVersion);
        updated.SelectedDiskId.Should().Be(current.SelectedDiskId);
        updated.LocationId.Should().Be(current.LocationId, "the technician-selected location must survive a self-update");
        updated.LocationName.Should().Be(current.LocationName);
        updated.Architecture.Should().Be(current.Architecture);
        updated.PartitionSchema.Should().BeEquivalentTo(current.PartitionSchema);
        updated.ValidationResults.Should().BeEquivalentTo(current.ValidationResults);
        updated.AutoStartConfigured.Should().Be(current.AutoStartConfigured);
    }

    [Theory]
    [InlineData("x64", "x64", true)]
    [InlineData("arm64", "arm64", true)]
    [InlineData("x64", "arm64", false)]
    [InlineData(null, "x64", true)]
    [InlineData(null, null, true)]
    [InlineData(null, "arm64", false)]
    public void ArchitecturesMatch_TreatsNullAsX64_AndComparesCaseInsensitively(string? current, string? latest, bool expected)
    {
        var method = typeof(BootImageSelfUpdateService).GetMethod(
            "ArchitecturesMatch", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (bool)method.Invoke(null, [current, latest])!;

        result.Should().Be(expected);
    }
}

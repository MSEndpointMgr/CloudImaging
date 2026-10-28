using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="BootImageSelfUpdateService"/> (T071b, FR-059a). The BOOT volume is
/// replaced by a temporary folder through the <c>findBootDrive</c> seam, so the whole
/// check, download, verify and replace path runs without a USB drive.
/// </summary>
public sealed class BootImageSelfUpdateServiceTests
{
    private static readonly byte[] CurrentImage = [9, 9];
    private static readonly byte[] NewImage = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate = CreateCertificate();

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Cloud Imaging Boot Media Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    // The update check is signed with the boot media certificate, so every gateway in these tests holds one.
    private static DeviceGatewayApiClient Gateway(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, Certificate);

    [Fact]
    public async Task CheckAndApply_CompleteWithoutThrowing_WhenNoBootVolumeIsPresent()
    {
        // On a dev/CI machine there is no BOOT-labelled volume, so this exercises the real WMI lookup.
        var gateway = new DeviceGatewayApiClient(new HttpClient { BaseAddress = new Uri("https://gw.example.com") });
        var svc = new BootImageSelfUpdateService(gateway, new HttpClient(), NullLogger<BootImageSelfUpdateService>.Instance);

        (await svc.CheckAsync()).Status.Should().Be(BootImageUpdateStatus.NotUsbMedia);
        (await svc.ApplyAsync(null)).Should().BeFalse("there is nothing to apply");
    }

    [Fact]
    public async Task Apply_WritesTheNewImageAndManifest_AndReportsEachPhase()
    {
        var payload = NewImage;
        var drive = MediaWithManifest("2026.10.1", out var manifestPath);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(drive, "sources", "boot.wim"), CurrentImage);
            var svc = ServiceOffering(drive, "2026.10.2", Convert.ToHexString(SHA256.HashData(payload)), payload);
            var phases = new List<BootImageUpdatePhase>();

            (await svc.CheckAsync()).Should().Be(new BootImageUpdateCheck(BootImageUpdateStatus.UpdateAvailable, "2026.10.2"));
            (await svc.ApplyAsync(p => phases.Add(p.Phase))).Should().BeTrue();

            (await File.ReadAllBytesAsync(Path.Combine(drive, "sources", "boot.wim"))).Should().Equal(payload);
            File.Exists(Path.Combine(drive, "sources", "boot.wim.download")).Should().BeFalse();
            System.Text.Json.JsonSerializer.Deserialize<UsbPreparationManifest>(await File.ReadAllTextAsync(manifestPath))!.BootImageVersion.Should().Be("2026.10.2");
            phases.Distinct().Should().Equal(BootImageUpdatePhase.Downloading, BootImageUpdatePhase.Verifying, BootImageUpdatePhase.Applying);
            svc.HasPendingUpdate.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_KeepsTheCurrentImage_WhenTheHashDoesNotMatch()
    {
        var drive = MediaWithManifest("2026.10.1", out var manifestPath);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(drive, "sources", "boot.wim"), CurrentImage);
            var svc = ServiceOffering(drive, "2026.10.2", new string('0', 64), NewImage);

            await svc.CheckAsync();
            (await svc.ApplyAsync(null)).Should().BeFalse();

            (await File.ReadAllBytesAsync(Path.Combine(drive, "sources", "boot.wim"))).Should().Equal(CurrentImage);
            File.Exists(Path.Combine(drive, "sources", "boot.wim.download")).Should().BeFalse("a rejected download must not fill the media");
            System.Text.Json.JsonSerializer.Deserialize<UsbPreparationManifest>(await File.ReadAllTextAsync(manifestPath))!.BootImageVersion.Should().Be("2026.10.1");
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    private static string MediaWithManifest(string version, out string manifestPath)
    {
        var drive = Directory.CreateTempSubdirectory("ci-selfupdate-").FullName;
        Directory.CreateDirectory(Path.Combine(drive, "sources"));
        manifestPath = Path.Combine(drive, UsbPreparationManifest.FileName);
        var manifest = new UsbPreparationManifest { ManifestVersion = "1.0", ToolVersion = "1", BootImageVersion = version, SelectedDiskId = "1" };
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        return drive;
    }

    private static BootImageSelfUpdateService ServiceOffering(string drive, string version, string sha256, byte[] payload)
    {
        var gateway = Gateway(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"version":"{{version}}","sha256Hash":"{{sha256}}","sasTokenUrl":"https://sas.example.com/x"}""", System.Text.Encoding.UTF8, "application/json"),
        }));
        var download = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        return new BootImageSelfUpdateService(gateway, download, NullLogger<BootImageSelfUpdateService>.Instance, findBootDrive: () => drive, readSerialNumber: () => "SN-1");
    }

    [Theory]
    [InlineData("2026.10.1", BootImageUpdateStatus.UpToDate)]
    [InlineData(null, BootImageUpdateStatus.Unavailable)]
    public async Task Check_ReportsTheOutcome_FromTheMediaManifest(string? latestVersion, BootImageUpdateStatus expected)
    {
        var drive = Directory.CreateTempSubdirectory("ci-selfupdate-").FullName;
        try
        {
            var manifest = new UsbPreparationManifest { ManifestVersion = "1.0", ToolVersion = "1", BootImageVersion = "2026.10.1", SelectedDiskId = "1" };
            await File.WriteAllTextAsync(Path.Combine(drive, UsbPreparationManifest.FileName), System.Text.Json.JsonSerializer.Serialize(manifest));
            var gateway = Gateway(new FakeHttpMessageHandler(_ => latestVersion is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"version":"{{latestVersion}}","sha256Hash":"x","sasTokenUrl":"https://sas.example.com/x"}""", System.Text.Encoding.UTF8, "application/json") }));
            var svc = new BootImageSelfUpdateService(gateway, new HttpClient(), NullLogger<BootImageSelfUpdateService>.Instance, findBootDrive: () => drive, readSerialNumber: () => "SN-1");

            (await svc.CheckAsync()).Status.Should().Be(expected);
            svc.HasPendingUpdate.Should().BeFalse("nothing is downloaded unless a newer image exists");
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task Check_ReportsNotUsbMedia_AndFailed_WithoutThrowing()
    {
        var gateway = Gateway(new FakeHttpMessageHandler(_ => throw new HttpRequestException("No such host is known.")));

        (await new BootImageSelfUpdateService(gateway, new HttpClient(), NullLogger<BootImageSelfUpdateService>.Instance, findBootDrive: () => null, readSerialNumber: () => "SN-1").CheckAsync())
            .Status.Should().Be(BootImageUpdateStatus.NotUsbMedia);

        var drive = Directory.CreateTempSubdirectory("ci-selfupdate-").FullName;
        try
        {
            var manifest = new UsbPreparationManifest { ManifestVersion = "1.0", ToolVersion = "1", BootImageVersion = "2026.10.1", SelectedDiskId = "1" };
            await File.WriteAllTextAsync(Path.Combine(drive, UsbPreparationManifest.FileName), System.Text.Json.JsonSerializer.Serialize(manifest));
            (await new BootImageSelfUpdateService(gateway, new HttpClient(), NullLogger<BootImageSelfUpdateService>.Instance, findBootDrive: () => drive, readSerialNumber: () => "SN-1").CheckAsync())
                .Status.Should().Be(BootImageUpdateStatus.Failed);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
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
            await (Task)method.Invoke(svc, ["https://sas.example.com/x", destPath, null, CancellationToken.None])!;

            File.Exists(destPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(destPath)).Should().BeEquivalentTo(payload,
                "the downloaded response body must be written byte-for-byte to the destination file");
        }
        finally
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
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
            Architecture = MachineArchitecture.X64,
            PartitionSchema = new Dictionary<string, object> { ["bootDriveLetter"] = "X:" },
            ValidationResults = new Dictionary<string, object> { ["busType"] = "USB" },
            AutoStartConfigured = true,
        };

        var newId = Guid.NewGuid();
        var method = typeof(BootImageSelfUpdateService).GetMethod(
            "BuildUpdatedManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var updated = (UsbPreparationManifest)method.Invoke(null, [current, "2026.02.01", newId])!;

        updated.BootImageVersion.Should().Be("2026.02.01", "only the boot image version and id change");
        updated.BootImageId.Should().Be(newId, "the next self-update check must identify the image now on the stick");
        updated.PreparedForTesting.Should().BeFalse("a stick that self-updated now carries a production image");
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
    [InlineData(MachineArchitecture.X64, MachineArchitecture.X64, true)]
    [InlineData(MachineArchitecture.Arm64, MachineArchitecture.Arm64, true)]
    [InlineData(MachineArchitecture.X64, MachineArchitecture.Arm64, false)]
    [InlineData(null, MachineArchitecture.X64, true)]
    [InlineData(null, null, true)]
    [InlineData(null, MachineArchitecture.Arm64, false)]
    public void ArchitecturesMatch_TreatsNullAsX64(MachineArchitecture? current, MachineArchitecture? latest, bool expected)
    {
        var method = typeof(BootImageSelfUpdateService).GetMethod(
            "ArchitecturesMatch", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (bool)method.Invoke(null, [current, latest])!;

        result.Should().Be(expected);
    }
}

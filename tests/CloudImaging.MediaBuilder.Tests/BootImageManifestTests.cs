using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Boot image manifest embedding tests (T065/T058, FR-051). Replaces the removed
/// BootImageSigningTests.cs — the boot image manifest is a plain integrity/provenance record,
/// not a cryptographically signed artifact (Option B: no genuine threat model for signing OS
/// images the Media Builder itself generates and later downloads; FR-056's SHA256 verification
/// at download time is the real integrity boundary).
/// </summary>
public sealed class BootImageManifestTests
{
    [Fact]
    public void Build_HashesClientExecutable_AndRecordsFullProductVersion()
    {
        var dir = Directory.CreateTempSubdirectory("ci-manifest-test-").FullName;
        try
        {
            var clientExePath = Path.Combine(dir, "CloudImaging.Client.exe");
            var sourceAssembly = typeof(BootImageManifestService).Assembly.Location;
            File.Copy(sourceAssembly, clientExePath);
            var bytes = File.ReadAllBytes(clientExePath);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var expectedVersion = FileVersionInfo.GetVersionInfo(sourceAssembly).ProductVersion;

            var manifest = BootImageManifestService.Build(
                clientDestDir: dir,
                imageVersion: "20260101-000000",
                driversInjectedCount: 0,
                driverRootPath: null,
                logoBytes: null,
                pfxBytes: null);

            manifest.ComponentChecksums.Should().ContainKey("cloudImagingClient");
            manifest.ComponentChecksums["cloudImagingClient"].Should().Be(expectedHash,
                "the manifest must hash the exact bytes of the staged Client executable");
            manifest.ClientVersion.Should().Be(expectedVersion,
                "the full product version identifies the exact source commit embedded in boot media");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_OmitsClientChecksum_WhenExecutableIsAbsent()
    {
        var dir = Directory.CreateTempSubdirectory("ci-manifest-test-").FullName;
        try
        {
            var manifest = BootImageManifestService.Build(
                clientDestDir: dir,
                imageVersion: "20260101-000000",
                driversInjectedCount: 0,
                driverRootPath: null,
                logoBytes: null,
                pfxBytes: null);

            manifest.ComponentChecksums.Should().NotContainKey("cloudImagingClient");
            manifest.ClientVersion.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_HashesLogoAndPfxBytes_WhenProvided()
    {
        var dir = Directory.CreateTempSubdirectory("ci-manifest-test-").FullName;
        try
        {
            var logoBytes = "logo-bytes"u8.ToArray();
            var pfxBytes  = "pfx-bytes"u8.ToArray();

            var manifest = BootImageManifestService.Build(
                clientDestDir: dir,
                imageVersion: "20260101-000000",
                driversInjectedCount: 3,
                driverRootPath: @"C:\drivers",
                logoBytes: logoBytes,
                pfxBytes: pfxBytes);

            manifest.ComponentChecksums["brandingLogo"].Should().Be(
                Convert.ToHexString(SHA256.HashData(logoBytes)).ToLowerInvariant());
            manifest.ComponentChecksums["bootMediaCertificate"].Should().Be(
                Convert.ToHexString(SHA256.HashData(pfxBytes)).ToLowerInvariant());
            manifest.DeploymentMetadata["driverPackagesInjected"].Should().Be(3);
            manifest.DeploymentMetadata["driverRootPath"].Should().Be(@"C:\drivers");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_DoesNotSignOrCertifyAnything()
    {
        // Option B (rescoped FR-051): the manifest has no signature field at all.
        var manifest = BootImageManifestService.Build(
            clientDestDir: Directory.CreateTempSubdirectory("ci-manifest-test-").FullName,
            imageVersion: "20260101-000000",
            driversInjectedCount: 0,
            driverRootPath: null,
            logoBytes: null,
            pfxBytes: null);

        typeof(BootImageManifest).GetProperties().Should()
            .NotContain(p => p.Name.Contains("Signature", StringComparison.OrdinalIgnoreCase),
                "the manifest is a plain integrity/provenance record — signing was intentionally descoped (Option B)");
    }

    [Fact]
    public void Build_RecordsSupportToolsEnabled_FalseByDefault()
    {
        var manifest = BootImageManifestService.Build(
            clientDestDir: Directory.CreateTempSubdirectory("ci-manifest-test-").FullName,
            imageVersion: "20260101-000000",
            driversInjectedCount: 0,
            driverRootPath: null,
            logoBytes: null,
            pfxBytes: null);

        manifest.SupportToolsEnabled.Should().BeFalse(
            "the Media Builder's \"Enable command prompt access\" opt-in defaults to off (FR-051d)");
    }

    [Fact]
    public void Build_RecordsSupportToolsEnabled_WhenCommandPromptOptInIsChecked()
    {
        var manifest = BootImageManifestService.Build(
            clientDestDir: Directory.CreateTempSubdirectory("ci-manifest-test-").FullName,
            imageVersion: "20260101-000000",
            driversInjectedCount: 0,
            driverRootPath: null,
            logoBytes: null,
            pfxBytes: null,
            commandPromptEnabled: true);

        manifest.SupportToolsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task EmbedAsync_WritesManifestJson_ToMountDirRoot()
    {
        var mountDir = Directory.CreateTempSubdirectory("ci-manifest-mount-").FullName;
        try
        {
            var manifest = new BootImageManifest
            {
                ManifestVersion = BootImageManifest.ManifestSchemaVersion,
                ImageVersion = "20260101-000000",
                CreatedAt = DateTimeOffset.UtcNow,
                ComponentChecksums = new Dictionary<string, string> { ["cloudImagingClient"] = "abc123" },
                DeploymentMetadata = new Dictionary<string, object> { ["driverPackagesInjected"] = 0 },
            };

            await BootImageManifestService.EmbedAsync(mountDir, manifest, CancellationToken.None);

            var manifestPath = Path.Combine(mountDir, BootImageManifestService.ManifestFileName);
            File.Exists(manifestPath).Should().BeTrue();

            var roundTripped = JsonSerializer.Deserialize<BootImageManifest>(await File.ReadAllTextAsync(manifestPath));
            roundTripped.Should().NotBeNull();
            roundTripped!.ImageVersion.Should().Be("20260101-000000");
            roundTripped.ComponentChecksums["cloudImagingClient"].Should().Be("abc123");
        }
        finally
        {
            Directory.Delete(mountDir, recursive: true);
        }
    }
}

using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
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
    public void Build_HashesClientExecutable_AndReadsItsFileVersion()
    {
        var dir = Directory.CreateTempSubdirectory("ci-manifest-test-").FullName;
        try
        {
            var clientExePath = Path.Combine(dir, "CloudImaging.Client.exe");
            // A real PE file isn't needed for this test — FileVersionInfo simply comes back
            // empty for a non-PE file, which is fine; we only assert the checksum here.
            var bytes = "fake-client-exe-content"u8.ToArray();
            File.WriteAllBytes(clientExePath, bytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CloudImaging.Contracts.Models;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Builds and embeds the <see cref="BootImageManifest"/> integrity/provenance manifest into a
/// generated boot image WIM (T065, FR-051). Replaces the earlier <c>BootImageSigningService</c>,
/// which claimed to produce a "signed manifest" but never actually used its certificate
/// parameter — see <see cref="BootImageManifest"/>'s remarks for why real cryptographic
/// signing was intentionally descoped rather than implemented.
///
/// Fully static and side-effect-free apart from the final <see cref="EmbedAsync"/> file write,
/// so <see cref="Build"/> is trivially unit-testable without ADK/DISM: it only hashes the small
/// set of files actually staged for embedding (Client executable, branding logo, boot media
/// certificate), never the multi-hundred-MB WIM itself.
/// </summary>
public static class BootImageManifestService
{
    /// <summary>File name the manifest is embedded under, at the root of the mounted WIM.</summary>
    public const string ManifestFileName = "ci-manifest.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Builds the manifest describing this generation run's inputs. <paramref name="clientDestDir"/>
    /// is expected to contain <c>CloudImaging.Client.exe</c> (used both to hash and to read
    /// <see cref="BootImageManifest.ClientVersion"/> from its product version, which includes
    /// the source commit in normal .NET builds).
    /// </summary>
    public static BootImageManifest Build(
        string clientDestDir,
        string imageVersion,
        int driversInjectedCount,
        string? driverRootPath,
        byte[]? logoBytes,
        byte[]? pfxBytes,
        bool commandPromptEnabled = false)
    {
        var componentChecksums = new Dictionary<string, string>();
        string? clientVersion = null;

        var clientExePath = Path.Combine(clientDestDir, "CloudImaging.Client.exe");
        if (File.Exists(clientExePath))
        {
            componentChecksums["cloudImagingClient"] = Sha256Hex(File.ReadAllBytes(clientExePath));
            clientVersion = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(clientExePath).ProductVersion;
        }

        if (logoBytes is { Length: > 0 })
            componentChecksums["brandingLogo"] = Sha256Hex(logoBytes);

        if (pfxBytes is { Length: > 0 })
            componentChecksums["bootMediaCertificate"] = Sha256Hex(pfxBytes);

        return new BootImageManifest
        {
            ManifestVersion = BootImageManifest.ManifestSchemaVersion,
            ImageVersion = imageVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            ClientVersion = clientVersion,
            ComponentChecksums = componentChecksums,
            DeploymentMetadata = new Dictionary<string, object>
            {
                ["driverPackagesInjected"] = driversInjectedCount,
                ["driverRootPath"] = driverRootPath ?? string.Empty,
            },
            SupportToolsEnabled = commandPromptEnabled,
        };
    }

    /// <summary>Serializes <paramref name="manifest"/> to <c>{mountDir}\ci-manifest.json</c>.</summary>
    public static async Task EmbedAsync(string mountDir, BootImageManifest manifest, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(mountDir, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, WriteOptions);
        await File.WriteAllTextAsync(manifestPath, json, ct);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

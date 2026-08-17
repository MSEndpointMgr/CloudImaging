using System.IO;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Detects whether a newer boot image has been published than the one currently deployed to
/// the USB media the Cloud Imaging Client is running from, and — if so — downloads and
/// replaces <c>\sources\boot.wim</c> on that same media in place (T071b, FR-059a).
///
/// Safe to run while WinPE is booted from that same media: WinPE loads the entire WIM into RAM
/// via the RAMDISK boot option at boot time, so the on-disk file is not locked once the Client
/// is running, and overwriting it has zero effect on the current session — it only takes effect
/// the next time the device is booted from this USB stick.
///
/// <see cref="CheckAndUpdateAsync"/> never throws: every failure (no BOOT volume found, no
/// manifest, network error, hash mismatch, disk write error) is logged and swallowed, since this
/// is a best-effort background convenience and must never block or fail Client startup.
/// </summary>
public sealed partial class BootImageSelfUpdateService
{
    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };

    private readonly DeviceGatewayApiClient _gateway;
    private readonly HttpClient _downloadHttp;
    private readonly ILogger<BootImageSelfUpdateService> _logger;

    public BootImageSelfUpdateService(
        DeviceGatewayApiClient gateway,
        HttpClient downloadHttp,
        ILogger<BootImageSelfUpdateService> logger)
    {
        _gateway = gateway;
        _downloadHttp = downloadHttp;
        _logger = logger;
    }

    public async Task CheckAndUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var bootDrive = FindBootVolumeDriveLetter();
            if (bootDrive is null)
            {
                LogBootVolumeNotFound(_logger);
                return;
            }

            var manifestPath = Path.Combine(bootDrive, UsbPreparationManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                LogManifestNotFound(_logger, manifestPath);
                return;
            }

            var currentManifest = JsonSerializer.Deserialize<UsbPreparationManifest>(
                await File.ReadAllTextAsync(manifestPath, ct));
            if (currentManifest is null)
            {
                LogManifestNotFound(_logger, manifestPath);
                return;
            }

            var latest = await _gateway.GetLatestBootImageAsync(ct);
            if (latest is null)
            {
                LogNoLatestBootImageAvailable(_logger);
                return;
            }

            if (string.Equals(latest.Version, currentManifest.BootImageVersion, StringComparison.Ordinal))
            {
                LogAlreadyUpToDate(_logger, latest.Version);
                return;
            }

            LogNewerVersionFound(_logger, currentManifest.BootImageVersion, latest.Version);

            var tempWimPath = Path.Combine(Path.GetTempPath(), $"ci-boot-update-{Guid.NewGuid():N}.wim");
            try
            {
                await DownloadAsync(latest.SasTokenUrl, tempWimPath, ct);

                var actualHash = await ComputeSha256Async(tempWimPath, ct);
                if (!string.Equals(actualHash, latest.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    LogHashMismatch(_logger, latest.Version);
                    return;
                }

                var destWimPath = Path.Combine(bootDrive, "sources", "boot.wim");
                File.Copy(tempWimPath, destWimPath, overwrite: true);

                var updatedManifest = new UsbPreparationManifest
                {
                    ManifestVersion = currentManifest.ManifestVersion,
                    PreparedAt = currentManifest.PreparedAt,
                    ToolVersion = currentManifest.ToolVersion,
                    BootImageVersion = latest.Version,
                    SelectedDiskId = currentManifest.SelectedDiskId,
                    PartitionSchema = currentManifest.PartitionSchema,
                    ValidationResults = currentManifest.ValidationResults,
                    AutoStartConfigured = currentManifest.AutoStartConfigured,
                };
                await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(updatedManifest, ManifestWriteOptions),
                    ct);

                LogUpdateComplete(_logger, latest.Version);
            }
            finally
            {
                try { File.Delete(tempWimPath); } catch { /* best-effort cleanup */ }
            }
        }
        catch (Exception ex)
        {
            LogSelfUpdateFailed(_logger, ex);
        }
    }

    private async Task DownloadAsync(string sasTokenUrl, string destinationPath, CancellationToken ct)
    {
        await using var responseStream = await _downloadHttp.GetStreamAsync(sasTokenUrl, ct);
        await using var fileStream = File.Create(destinationPath);
        await responseStream.CopyToAsync(fileStream, ct);
    }

    /// <summary>
    /// Locates the BOOT-labelled FAT32 volume this Client is running from — mirrors
    /// <c>UsbPartitionProvisioningService.FindBootVolumeDriveLetter</c> in the Media Builder,
    /// which creates and labels this same volume during USB preparation.
    /// </summary>
    private static string? FindBootVolumeDriveLetter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, Label FROM Win32_Volume WHERE Label='BOOT'");
            using var results = searcher.Get();
            foreach (ManagementObject volume in results)
            {
                var letter = volume["DriveLetter"]?.ToString();
                if (!string.IsNullOrWhiteSpace(letter))
                    return letter;
            }
        }
        catch (ManagementException)
        {
            // Treated the same as "not found" by the caller.
        }
        return null;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Boot image self-update: could not locate the BOOT volume.")]
    private static partial void LogBootVolumeNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Boot image self-update: no preparation manifest found at {ManifestPath}.")]
    private static partial void LogManifestNotFound(ILogger logger, string manifestPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Boot image self-update: no latest boot image info available from Device Gateway API.")]
    private static partial void LogNoLatestBootImageAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: already running the latest version {Version}.")]
    private static partial void LogAlreadyUpToDate(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: newer version available ({CurrentVersion} -> {LatestVersion}). Downloading.")]
    private static partial void LogNewerVersionFound(ILogger logger, string currentVersion, string latestVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: downloaded WIM hash did not match the expected hash for version {Version} — aborting, keeping the current boot.wim.")]
    private static partial void LogHashMismatch(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: boot.wim replaced with version {Version}. Effective on next boot.")]
    private static partial void LogUpdateComplete(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update check failed — continuing with the current boot.wim.")]
    private static partial void LogSelfUpdateFailed(ILogger logger, Exception ex);
}

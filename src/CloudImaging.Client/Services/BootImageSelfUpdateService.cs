using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using CloudImaging.Client.Logging;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>How the boot media update check ended.</summary>
public enum BootImageUpdateStatus
{
    /// <summary>Not running from prepared USB media (no BOOT volume or manifest), so there is nothing to update.</summary>
    NotUsbMedia,

    /// <summary>The media already has the latest boot image.</summary>
    UpToDate,

    /// <summary>A newer boot image is published; <see cref="BootImageSelfUpdateService.ApplyAsync"/> writes it to the media.</summary>
    UpdateAvailable,

    /// <summary>The service offered no image for this media, e.g. a pre-production test stick.</summary>
    Unavailable,

    /// <summary>The check failed; the media keeps its current boot image.</summary>
    Failed,
}

/// <summary>Outcome of <see cref="BootImageSelfUpdateService.CheckAsync"/>.</summary>
/// <param name="Status">How the check ended.</param>
/// <param name="Version">The current version, or the newer one on offer.</param>
public sealed record BootImageUpdateCheck(BootImageUpdateStatus Status, string? Version = null);

/// <summary>Which part of writing a new boot image to the media is running.</summary>
public enum BootImageUpdatePhase
{
    /// <summary>Downloading onto the media, next to the current boot.wim.</summary>
    Downloading,

    /// <summary>Checking the download against the published hash.</summary>
    Verifying,

    /// <summary>Replacing boot.wim and the preparation manifest.</summary>
    Applying,
}

/// <summary>Progress of <see cref="BootImageSelfUpdateService.ApplyAsync"/>.</summary>
/// <param name="Phase">What is running.</param>
/// <param name="Percent">0 to 100, or null when the size is unknown.</param>
/// <param name="BytesDone">Bytes downloaded so far.</param>
/// <param name="BytesTotal">Download size, or null when the server did not send it.</param>
public sealed record BootImageUpdateProgress(BootImageUpdatePhase Phase, int? Percent, long BytesDone = 0, long? BytesTotal = null);

/// <summary>
/// Detects whether a newer boot image has been published than the one currently deployed to
/// the USB media the Cloud Imaging Client is running from, and writes it to that same media
/// in place of <c>\sources\boot.wim</c> (T071b, FR-059a).
///
/// Safe to run while WinPE is booted from that same media: WinPE loads the entire WIM into RAM
/// via the RAMDISK boot option at boot time, so the on-disk file is not locked once the Client
/// is running. The new image takes effect the next time the device boots from this USB stick.
///
/// <see cref="CheckAsync"/> and <see cref="ApplyAsync"/> never throw: every failure (no BOOT
/// volume, no manifest, network error, hash mismatch, disk write error) is logged and the media
/// keeps its current boot image.
/// </summary>
public sealed partial class BootImageSelfUpdateService
{
    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };

    // The download has no overall limit, so a connection that stops sending must not hang startup.
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    private const int DownloadBufferSize = 1024 * 1024;

    private readonly DeviceGatewayApiClient _gateway;
    private readonly HttpClient _downloadHttp;
    private readonly ILogger<BootImageSelfUpdateService> _logger;
    private readonly Func<string?> _findBootDrive;
    private readonly Func<string> _readSerialNumber;
    private PendingUpdate? _pending;

    private sealed record PendingUpdate(string BootDrive, string ManifestPath, UsbPreparationManifest Manifest, LatestBootImageInfo Latest);

    /// <summary>Builds the service over the Device Gateway API client and a plain (non-mTLS) HTTP client for SAS downloads.</summary>
    /// <param name="gateway">Device Gateway API client.</param>
    /// <param name="downloadHttp">Plain HTTP client for the SAS download.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="findBootDrive">Test seam; defaults to the BOOT volume this Client runs from.</param>
    /// <param name="readSerialNumber">Test seam; defaults to the BIOS serial number.</param>
    public BootImageSelfUpdateService(
        DeviceGatewayApiClient gateway,
        HttpClient downloadHttp,
        ILogger<BootImageSelfUpdateService> logger,
        Func<string?>? findBootDrive = null,
        Func<string>? readSerialNumber = null)
    {
        _gateway = gateway;
        _downloadHttp = downloadHttp;
        _logger = logger;
        _findBootDrive = findBootDrive ?? FindBootVolumeDriveLetter;
        _readSerialNumber = readSerialNumber ?? DeviceSerialNumber.Read;
    }

    /// <summary>True when the last <see cref="CheckAsync"/> found a newer image that <see cref="ApplyAsync"/> has not written yet.</summary>
    internal bool HasPendingUpdate => _pending is not null;

    /// <summary>
    /// Looks up the latest boot image for this media. Returns <see cref="BootImageUpdateStatus.UpdateAvailable"/>
    /// when it is newer; <see cref="ApplyAsync"/> then writes it. Never throws.
    /// </summary>
    public async Task<BootImageUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        _pending = null;
        try
        {
            var bootDrive = _findBootDrive();
            if (bootDrive is null)
            {
                LogBootVolumeNotFound(_logger);
                return new BootImageUpdateCheck(BootImageUpdateStatus.NotUsbMedia);
            }

            var manifestPath = Path.Combine(bootDrive.TrimEnd('\\') + "\\", UsbPreparationManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                LogManifestNotFound(_logger, manifestPath);
                return new BootImageUpdateCheck(BootImageUpdateStatus.NotUsbMedia);
            }

            var currentManifest = JsonSerializer.Deserialize<UsbPreparationManifest>(
                await File.ReadAllTextAsync(manifestPath, ct));
            if (currentManifest is null)
            {
                LogManifestNotFound(_logger, manifestPath);
                return new BootImageUpdateCheck(BootImageUpdateStatus.NotUsbMedia);
            }

            // Only test sticks identify their image: the gateway then holds them on it while it is pre-production.
            // The base keeps a Windows RE (Wi-Fi) stick on Windows RE images.
            var latest = await _gateway.GetLatestBootImageAsync(
                currentManifest.Architecture ?? MachineArchitecture.X64,
                currentManifest.PreparedForTesting ? currentManifest.BootImageId : null,
                currentManifest.BootImageBase ?? (BootImageCapabilities.SupportsWifi() ? BootImageBase.WindowsRE : BootImageBase.WindowsPE),
                _readSerialNumber(),
                ct);
            if (latest is null)
            {
                LogNoLatestBootImageAvailable(_logger);
                return new BootImageUpdateCheck(BootImageUpdateStatus.Unavailable, currentManifest.BootImageVersion);
            }

            if (string.Equals(latest.Version, currentManifest.BootImageVersion, StringComparison.Ordinal))
            {
                LogAlreadyUpToDate(_logger, latest.Version);
                return new BootImageUpdateCheck(BootImageUpdateStatus.UpToDate, latest.Version);
            }

            // ARM64 milestone groundwork (todo/arm64-support.md #8): reject a mismatched
            // architecture before downloading anything. Absent values default to X64, the
            // only architecture that exists today, so older manifests/responses are unaffected.
            if (!ArchitecturesMatch(currentManifest.Architecture, latest.Architecture))
            {
                LogArchitectureMismatch(_logger, currentManifest.Architecture ?? MachineArchitecture.X64, latest.Architecture ?? MachineArchitecture.X64);
                return new BootImageUpdateCheck(BootImageUpdateStatus.Unavailable, currentManifest.BootImageVersion);
            }

            LogNewerVersionFound(_logger, currentManifest.BootImageVersion, latest.Version);
            _pending = new PendingUpdate(bootDrive, manifestPath, currentManifest, latest);
            return new BootImageUpdateCheck(BootImageUpdateStatus.UpdateAvailable, latest.Version);
        }
        catch (Exception ex)
        {
            LogSelfUpdateFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            return new BootImageUpdateCheck(BootImageUpdateStatus.Failed);
        }
    }

    /// <summary>
    /// Downloads the image <see cref="CheckAsync"/> found, verifies it, and replaces the media's
    /// boot.wim and manifest. Returns true once the media holds the new image. Never throws.
    /// </summary>
    public async Task<bool> ApplyAsync(Action<BootImageUpdateProgress>? onProgress, CancellationToken ct = default)
    {
        if (_pending is not { } pending)
        {
            return false;
        }

        // Downloaded onto the media itself: the WinPE RAM disk has far less free space than a boot image,
        // and a rename on the same volume never leaves a half-written boot.wim behind.
        var sourcesDir = Path.Combine(pending.BootDrive.TrimEnd('\\') + "\\", "sources");
        var downloadPath = Path.Combine(sourcesDir, "boot.wim.download");
        try
        {
            await DownloadAsync(pending.Latest.SasTokenUrl, downloadPath, onProgress, ct);

            var actualHash = await ImageHasher.ComputeAsync(downloadPath, percent => onProgress?.Invoke(new BootImageUpdateProgress(BootImageUpdatePhase.Verifying, percent)), ct);
            if (!string.Equals(actualHash, pending.Latest.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                LogHashMismatch(_logger, pending.Latest.Version);
                return false;
            }

            onProgress?.Invoke(new BootImageUpdateProgress(BootImageUpdatePhase.Applying, null));
            File.Move(downloadPath, Path.Combine(sourcesDir, "boot.wim"), overwrite: true);
            var updatedManifest = BuildUpdatedManifest(pending.Manifest, pending.Latest.Version, pending.Latest.BootImageId);
            await File.WriteAllTextAsync(pending.ManifestPath, JsonSerializer.Serialize(updatedManifest, ManifestWriteOptions), ct);

            _pending = null;
            LogUpdateComplete(_logger, pending.Latest.Version);
            return true;
        }
        catch (Exception ex)
        {
            LogSelfUpdateFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            return false;
        }
        finally
        {
            try { File.Delete(downloadPath); } catch { /* best-effort cleanup */ }
        }
    }

    private async Task DownloadAsync(string sasTokenUrl, string destinationPath, Action<BootImageUpdateProgress>? onProgress, CancellationToken ct)
    {
        using var response = await _downloadHttp.GetAsync(sasTokenUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength is > 0 and var length ? length : null;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destinationPath);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[DownloadBufferSize];
        long done = 0;
        long lastStep = -1;
        while (true)
        {
            // Reported once per percent, or once per MB when the size is unknown.
            var step = total is { } size ? done * 100 / size : done / 1_000_000;
            if (step != lastStep)
            {
                lastStep = step;
                onProgress?.Invoke(new BootImageUpdateProgress(BootImageUpdatePhase.Downloading, total is null ? null : (int)step, done, total));
            }

            stall.CancelAfter(DownloadStallTimeout);
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
        }
    }

    /// <summary>Copies every field from <paramref name="current"/> except <c>BootImageVersion</c>, which becomes <paramref name="newVersion"/>. Keeping this as its own method is what makes field preservation (e.g. LocationId/LocationName) unit-testable without a real BOOT volume.</summary>
    private static UsbPreparationManifest BuildUpdatedManifest(UsbPreparationManifest current, string newVersion, Guid? newBootImageId) =>
        new()
        {
            ManifestVersion = current.ManifestVersion,
            PreparedAt = current.PreparedAt,
            ToolVersion = current.ToolVersion,
            BootImageVersion = newVersion,
            BootImageId = newBootImageId,
            PreparedForTesting = false,
            SelectedDiskId = current.SelectedDiskId,
            LocationId = current.LocationId,
            LocationName = current.LocationName,
            Architecture = current.Architecture,
            BootImageBase = current.BootImageBase,
            ManagedWifiIncluded = current.ManagedWifiIncluded,
            PartitionSchema = current.PartitionSchema,
            ValidationResults = current.ValidationResults,
            AutoStartConfigured = current.AutoStartConfigured,
        };

    /// <summary>Null on either side means <see cref="MachineArchitecture.X64"/> (pre-architecture-tracking manifests/responses).</summary>
    private static bool ArchitecturesMatch(MachineArchitecture? currentArchitecture, MachineArchitecture? latestArchitecture) =>
        (currentArchitecture ?? MachineArchitecture.X64) == (latestArchitecture ?? MachineArchitecture.X64);

    /// <summary>
    /// Locates the BOOT-labelled FAT32 volume this Client is running from — mirrors
    /// <c>UsbPartitionProvisioningService.FindBootVolumeDriveLetter</c> in the Media Builder,
    /// which creates and labels this same volume during USB preparation.
    /// </summary>
    internal static string? FindBootVolumeDriveLetter()
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
                {
                    return letter;
                }
            }
        }
        catch (ManagementException)
        {
            // Treated the same as "not found" by the caller.
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: could not locate the BOOT volume.")]
    private static partial void LogBootVolumeNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: no preparation manifest found at {ManifestPath}.")]
    private static partial void LogManifestNotFound(ILogger logger, string manifestPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: no latest boot image info available from Device Gateway API.")]
    private static partial void LogNoLatestBootImageAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: architecture mismatch (current {CurrentArchitecture}, latest {LatestArchitecture}). Aborting and keeping the current boot.wim.")]
    private static partial void LogArchitectureMismatch(ILogger logger, MachineArchitecture currentArchitecture, MachineArchitecture latestArchitecture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: already running the latest version {Version}.")]
    private static partial void LogAlreadyUpToDate(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: newer version available ({CurrentVersion} -> {LatestVersion}).")]
    private static partial void LogNewerVersionFound(ILogger logger, string currentVersion, string latestVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update: downloaded WIM hash did not match the expected hash for version {Version}. Aborting and keeping the current boot.wim.")]
    private static partial void LogHashMismatch(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image self-update: boot.wim replaced with version {Version}. A restart is needed to use it.")]
    private static partial void LogUpdateComplete(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image self-update check failed, continuing with the current boot.wim: {Reason}")]
    private static partial void LogSelfUpdateFailed(ILogger logger, string reason, Exception? ex);
}

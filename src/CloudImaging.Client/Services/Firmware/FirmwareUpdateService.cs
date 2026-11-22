using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Firmware;

/// <summary>How the Update firmware step ended. It never fails the deployment: Windows is already applied.</summary>
/// <param name="Staged">True when the BIOS flashes at the restart.</param>
/// <param name="WarningCount">1 when the BIOS was skipped or could not be staged, shown as Completed with warnings.</param>
/// <param name="StatusNote">One line for the device log and the deployment timeline.</param>
public sealed record FirmwareUpdateResult(bool Staged, int WarningCount, string StatusNote);

/// <summary>What the device can tell about itself just before the BIOS is staged.</summary>
/// <param name="PowerSource">AC adapter connected or not.</param>
/// <param name="SmbiosBiosVersion">SMBIOS BIOS version, for example <c>X84 Ver. 01.06.01</c>.</param>
/// <param name="SupportsPowerShell">Windows PowerShell is in the boot image.</param>
/// <param name="HpToolsFolder">The HP.Firmware module folder Media Builder copies in, or null when it is missing.</param>
public sealed record FirmwareDeviceState(PowerSource PowerSource, string? SmbiosBiosVersion, bool SupportsPowerShell, string? HpToolsFolder);

/// <summary>
/// The Update firmware step: downloads the pinned HP BIOS, verifies it, and stages it with HP's
/// Client Management Script Library (<c>Update-HPFirmware -Offline</c>) so it flashes at the
/// restart that ends imaging. CMSL runs in its own restricted PowerShell process, so neither a
/// script error nor HP's native library can take down the Client, and it cannot restart the device.
/// </summary>
public sealed partial class FirmwareUpdateService
{
    /// <summary>How long staging may take before the process is stopped.</summary>
    public static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Environment variable that carries the BIOS file to the script, so no path is quoted into a command line.</summary>
    public const string FileVariable = "CI_BIOS_FILE";

    /// <summary>Step note when the BIOS the session was given is no longer stored.</summary>
    public const string UnavailableNote = "Not updated: the BIOS this deployment was given is no longer stored in Cloud Imaging.";

    private const string ResultPrefix = "CI-RESULT:";
    private const int BufferSize = 4 * 1024 * 1024;

    // Imports only the modules the flash path needs: HPCMSL also loads modules that need WinRT,
    // which WinPE does not have. HP.Firmware needs HP.ClientManagement without declaring it.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            Import-Module HP.Private, HP.Utility, HP.ClientManagement, HP.Firmware
            Update-HPFirmware -File $env:CI_BIOS_FILE -Offline -BitLocker ignore -Quiet *>&1 | ForEach-Object { "$_" }
            'CI-RESULT: STAGED'
        }
        catch {
            "CI-RESULT: ERROR $($_.Exception.Message)"
            exit 1
        }
        """;

    private readonly IRestrictedProcessRunner _processes;
    private readonly HttpClient _download;
    private readonly ILogger _logger;

    /// <param name="processes">Runs PowerShell without the right to restart the device.</param>
    /// <param name="download">Downloads the BIOS outside the session-authenticated gateway client.</param>
    /// <param name="logger">Logger.</param>
    public FirmwareUpdateService(IRestrictedProcessRunner processes, HttpClient download, ILogger logger)
    {
        _processes = processes;
        _download = download;
        _logger = logger;
    }

    /// <summary>Where Media Builder puts HP's modules in the boot image.</summary>
    public static string HpToolsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules", "HP.Firmware");

    /// <summary>The SMBIOS BIOS version, for example <c>X84 Ver. 01.06.01</c>, or null when WMI cannot read it.</summary>
    public static string? ReadSmbiosBiosVersion()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject bios in results)
            {
                return bios["SMBIOSBIOSVersion"]?.ToString()?.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or PlatformNotSupportedException or TypeInitializationException)
        {
            // Unknown: HP's tool compares the versions itself and reports a BIOS that is already current.
        }

        return null;
    }

    /// <summary>
    /// Why the BIOS is not staged before anything is downloaded, or null when it can be: on battery,
    /// already at or above the version, or the boot image lacks PowerShell or HP's tools.
    /// </summary>
    public static FirmwareUpdateResult? Precheck(SessionFirmwareDescriptor descriptor, FirmwareDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(device);
        var installed = BiosVersion.Installed(device.SmbiosBiosVersion);
        if (installed is not null && BiosVersion.Compare(installed, descriptor.Version) >= 0)
        {
            return new FirmwareUpdateResult(false, 0, $"Up to date: the device has BIOS {installed}.");
        }

        if (device.PowerSource == PowerSource.Battery)
        {
            return Skipped("the device runs on battery. Connect the AC adapter and image the device again to update the BIOS.");
        }

        if (!device.SupportsPowerShell)
        {
            return Skipped("this boot image has no PowerShell. Generate a new boot image with Media Builder.");
        }

        return device.HpToolsFolder is null
            ? Skipped("HP firmware tools are not in the boot media. Generate a new boot image with Media Builder and include them.")
            : null;
    }

    /// <summary>
    /// Maps what Update-HPFirmware wrote to the step's outcome. HP's messages are matched by their
    /// start, since CMSL appends details to some of them.
    /// </summary>
    public static FirmwareUpdateResult MapOutcome(IReadOnlyList<string> output, string fromVersion, string toVersion)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = output.LastOrDefault(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal))?[ResultPrefix.Length..].Trim();

        // The same version is not an error to CMSL: it only writes a line and returns.
        if (output.Any(line => line.Contains("already running BIOS version", StringComparison.OrdinalIgnoreCase)))
        {
            return new FirmwareUpdateResult(false, 0, $"Up to date: the device already has BIOS {toVersion}.");
        }

        if (result == "STAGED")
        {
            return new FirmwareUpdateResult(true, 0, $"Staged {fromVersion} to {toVersion}. It flashes at the restart into Windows setup.");
        }

        var message = result?.StartsWith("ERROR", StringComparison.Ordinal) == true ? result[5..].Trim() : output.Count > 0 ? output[^1] : string.Empty;
        return message switch
        {
            _ when message.StartsWith("This cmdlet only supported Windows 10 UEFI mode", StringComparison.OrdinalIgnoreCase) => Skipped("the device does not boot in UEFI mode."),
            _ when message.StartsWith("You cannot flash while 'Lock BIOS Version' is enabled", StringComparison.OrdinalIgnoreCase) => Skipped("the BIOS version is locked in the BIOS settings."),
            _ when message.StartsWith("HP Sure Admin is enabled, and authentication is required", StringComparison.OrdinalIgnoreCase) => Skipped("the BIOS requires HP Sure Admin authorization for updates."),
            _ when message.StartsWith("BIOS Setup Password is set, and authentication is required", StringComparison.OrdinalIgnoreCase) => Skipped("a BIOS password is set."),
            _ when message.StartsWith("Could not locate EFI partition, too many candidates", StringComparison.OrdinalIgnoreCase) => Skipped("the device has more than one EFI system partition."),
            _ when message.StartsWith("Could not locate EFI partition", StringComparison.OrdinalIgnoreCase) => Failed("the EFI system partition was not found."),
            _ when message.StartsWith("Could not copy the file to the system partition", StringComparison.OrdinalIgnoreCase) => Failed("the EFI system partition has no room for the update."),
            _ when message.Contains("does not mach the target platform", StringComparison.OrdinalIgnoreCase) || message.Contains("does not match the target platform", StringComparison.OrdinalIgnoreCase) => Failed("the BIOS file does not match this model."),
            _ when message.StartsWith("Current platform does not support this operation", StringComparison.OrdinalIgnoreCase) || message.Contains("supported HP system", StringComparison.OrdinalIgnoreCase) => Skipped("the device does not support BIOS updates from HP's tools."),
            _ when message.StartsWith("Flash not allowed", StringComparison.OrdinalIgnoreCase) || message.Contains("not accepted by the BIOS", StringComparison.OrdinalIgnoreCase) => Failed("the BIOS refused the update."),
            _ when message.Contains("dfmbios", StringComparison.OrdinalIgnoreCase) || message.Contains("support library", StringComparison.OrdinalIgnoreCase) => Failed("HP firmware tools are incomplete in the boot media. Generate a new boot image with Media Builder."),
            _ => Failed(message.Length == 0 ? "HP's tools gave no reason." : message),
        };
    }

    /// <summary>Runs the step for the pinned BIOS. Never throws for a device problem; the result says what happened.</summary>
    public async Task<FirmwareUpdateResult> RunAsync(SessionFirmwareDescriptor descriptor, FirmwareDeviceState device, string stagingFolder, Action<string> onStatus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(onStatus);
        if (Precheck(descriptor, device) is { } early)
        {
            LogOutcome(_logger, early.StatusNote);
            return early;
        }

        if (string.IsNullOrWhiteSpace(descriptor.FileName) || string.IsNullOrWhiteSpace(descriptor.Sha256) || !Uri.TryCreate(descriptor.DownloadUrl, UriKind.Absolute, out _))
        {
            return Logged(Failed("the BIOS details from Cloud Imaging are incomplete."));
        }

        var installed = BiosVersion.Installed(device.SmbiosBiosVersion) ?? descriptor.InstalledVersion ?? "the current version";

        // HP's flash tool reads the target version from the file name, so the name is kept as HP publishes it.
        var binPath = Path.Combine(stagingFolder, Path.GetFileName(descriptor.FileName));
        var scriptPath = Path.Combine(stagingFolder, "Stage-Bios.ps1");
        try
        {
            Directory.CreateDirectory(stagingFolder);
            onStatus($"Downloading BIOS {descriptor.Version}...");
            if (!await DownloadAsync(descriptor, binPath, ct))
            {
                return Logged(Failed("the BIOS could not be downloaded or did not match its checksum."));
            }

            await File.WriteAllTextAsync(scriptPath, Script, ct);
            onStatus($"Staging BIOS {descriptor.Version}...");
            var run = await Task.Run(() => _processes.Run(new RestrictedProcessRequest(
                Path.Combine(Environment.SystemDirectory, BootImageCapabilities.PowerShellRelativePath),
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                stagingFolder,
                new Dictionary<string, string> { [FileVariable] = binPath },
                StageTimeout), ct), ct);

            // The runner stops the script on cancellation and reports it as a time-out.
            ct.ThrowIfCancellationRequested();
            foreach (var line in run.Output)
            {
                LogToolOutput(_logger, line);
            }

            return Logged(run.Outcome switch
            {
                RestrictedProcessOutcome.TimedOut => Failed("HP's tools did not finish within 10 minutes."),
                RestrictedProcessOutcome.GuardUnavailable => Failed("HP's tools could not be started safely."),
                _ => MapOutcome(run.Output, installed, descriptor.Version),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Logged(Failed(ex.Message));
        }
        finally
        {
            // HP's tool copied what it needs onto the EFI system partition; nothing here is used again.
            TryDelete(binPath);
            TryDelete(scriptPath);
            TryDeleteEmptyFolder(stagingFolder);
        }
    }

    private static FirmwareUpdateResult Skipped(string reason) => new(false, 1, $"Skipped: {reason}");

    private static FirmwareUpdateResult Failed(string reason) => new(false, 1, $"Not updated: {reason}");

    private FirmwareUpdateResult Logged(FirmwareUpdateResult result)
    {
        LogOutcome(_logger, result.StatusNote);
        return result;
    }

    private async Task<bool> DownloadAsync(SessionFirmwareDescriptor descriptor, string destinationPath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await _download.GetAsync(descriptor.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[BufferSize];
                long total = 0;
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;

                        // A file longer than the descriptor lists can never match, so reading stops there.
                        if (descriptor.SizeBytes > 0 && total > descriptor.SizeBytes)
                        {
                            break;
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                if ((descriptor.SizeBytes == 0 || total == descriptor.SizeBytes) && string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                LogChecksumMismatch(_logger, descriptor.Version, attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogDownloadFailed(_logger, descriptor.Version, attempt, ex.Message);
            }
        }

        return false;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanUpFailed(_logger, path, ex.Message);
        }
    }

    private static void TryDeleteEmptyFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder in Windows\Temp is harmless.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update firmware: {Note}")]
    private static partial void LogOutcome(ILogger logger, string note);

    [LoggerMessage(Level = LogLevel.Information, Message = "HP firmware tools: {Line}")]
    private static partial void LogToolOutput(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BIOS {Version} did not match its checksum (attempt {Attempt}).")]
    private static partial void LogChecksumMismatch(ILogger logger, string version, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BIOS {Version} could not be downloaded (attempt {Attempt}): {Reason}")]
    private static partial void LogDownloadFailed(ILogger logger, string version, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} could not be deleted: {Reason}")]
    private static partial void LogCleanUpFailed(ILogger logger, string path, string reason);
}

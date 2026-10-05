using System.IO;
using System.Text;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Deploys a WinPE boot image to the FAT32 boot partition of a prepared USB drive (T071, FR-055).
/// Copies boot.wim to the partition, then makes it bootable via bcdboot (BIOS + UEFI for x64,
/// UEFI only for ARM64).
/// </summary>
public sealed partial class BootImageDeploymentService
{
    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };

    private readonly ILogger<BootImageDeploymentService> _logger;

    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    public BootImageDeploymentService(ILogger<BootImageDeploymentService> logger) => _logger = logger;

    /// <summary>
    /// Deploys the WIM at <paramref name="wimPath"/> to the boot partition at
    /// <paramref name="bootDriveLetter"/> and configures it to be bootable via bcdboot.
    /// </summary>
    public async Task DeployAsync(
        string wimPath,
        string bootDriveLetter,
        MachineArchitecture architecture = MachineArchitecture.X64,
        CancellationToken ct = default)
    {
        LogStarting(_logger, wimPath, bootDriveLetter);

        ReportProgress("Copying WinPE boot files to USB…", 10);
        var mediaRoot = bootDriveLetter.TrimEnd('\\', '/');

        // Step 1: Copy WIM to the boot partition
        var destWim = Path.Combine(mediaRoot, "sources", "boot.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(destWim)!);
        // The FAT32 boot partition is typically small (a few GB) — check it has room for the
        // WIM before copying instead of failing partway through with a disk-full I/O error.
        DiskSpaceGuard.EnsureFreeSpace(mediaRoot, new FileInfo(wimPath).Length, "copy the boot image to the USB boot partition");
        File.Copy(wimPath, destWim, overwrite: true);
        ReportProgress("Boot WIM copied.", 50);

        // Step 2: Make the partition actually bootable (BIOS + UEFI) via bcdboot, sourced
        // directly from the boot.wim we just copied — see ConfigureBootFilesAsync for details.
        ReportProgress("Configuring boot files (bcdboot)…", 70);
        await ConfigureBootFilesAsync(destWim, mediaRoot, architecture, ct);
        ReportProgress("USB boot partition activated.", 100);

        LogComplete(_logger, bootDriveLetter);
    }

    /// <summary>
    /// Writes the <see cref="UsbPreparationManifest"/> to the root of the BOOT partition
    /// (alongside <c>\sources\boot.wim</c>), so both the Cloud Imaging Client (self-update
    /// version check, T071b/FR-059a) and future diagnostics can read the currently-deployed
    /// boot image version without mounting/inspecting the WIM itself (T071a, FR-059).
    /// </summary>
    public async Task WriteUsbPreparationManifestAsync(
        string bootDriveLetter,
        UsbPreparationManifest manifest,
        CancellationToken ct = default)
    {
        var mediaRoot = bootDriveLetter.TrimEnd('\\', '/');
        var manifestPath = Path.Combine(mediaRoot, UsbPreparationManifest.FileName);
        var json = JsonSerializer.Serialize(manifest, ManifestWriteOptions);
        await File.WriteAllTextAsync(manifestPath, json, ct);
        LogManifestWritten(_logger, manifestPath, manifest.BootImageVersion);
    }

    /// <summary>
    /// Makes the FAT32 boot partition actually bootable for both BIOS and UEFI firmware by
    /// running <c>bcdboot</c> against the boot.wim just copied to <paramref name="mediaRoot"/>
    /// — the same mechanism Windows Setup itself uses to build bootable media.
    /// <para/>
    /// WinPE auto-start (winpeshl.ini/startnet.cmd launching CloudImaging.Client.exe) is
    /// configured inside boot.wim itself, while it's mounted during boot image generation
    /// (see <c>BootImageGenerationService.ConfigureWinPeAutoStartAsync</c>) — not here.
    /// <para/>
    /// This method only needs to place <c>bootmgr</c>, <c>\Boot\BCD</c>, <c>\efi\boot\bootx64.efi</c>
    /// and <c>\efi\microsoft\boot\BCD</c> on the boot partition and point the BCD's ramdisk
    /// entry at <c>\sources\boot.wim</c>. Rather than hand-copying those files from a separately
    /// packaged bundle (which would require repackaging the published artifact, and re-plumbing
    /// upload/download/hash verification for it), <c>bcdboot</c> derives everything it needs
    /// directly from the boot.wim's own embedded <c>\Windows\Boot\PCAT</c> and
    /// <c>\Windows\Boot\EFI</c> folders — every WinPE image already contains these since it's
    /// itself a small Windows OS. That keeps the published artifact a single hash-verified
    /// boot.wim (FR-056) with nothing extra to package, cache, or verify — the true "bare
    /// minimum" is zero additional files.
    /// <para/>
    /// Both <c>dism.exe</c> and <c>bcdboot.exe</c> ship with every Windows 10/11 installation
    /// (<c>%windir%\System32</c>) — unlike <c>bootsect.exe</c>, no Windows ADK is required on
    /// the machine running "Prepare USB Storage Device".
    /// </summary>
    private static async Task ConfigureBootFilesAsync(string wimPath, string mediaRoot, MachineArchitecture architecture, CancellationToken ct)
    {
        var mountDir = Path.Combine(Path.GetTempPath(), $"ci-deploy-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountDir);
        var mounted = false;
        try
        {
            await RunExternalAsync(
                "dism.exe",
                $"/Mount-Image /ImageFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\" /ReadOnly",
                ct);
            mounted = true;

            var windowsDir = Path.Combine(mountDir, "Windows");
            if (!Directory.Exists(windowsDir))
                throw new InvalidOperationException(
                    $"The boot image does not contain a \\Windows directory at \"{windowsDir}\" — cannot configure boot files with bcdboot.");

            // x64: /f ALL writes BIOS (bootmgr, \Boot\BCD) and UEFI (\efi\boot\bootx64.efi,
            // \efi\microsoft\boot\BCD) files. ARM64 is UEFI-only and its WinPE has no PCAT
            // files, so requesting BIOS support there would fail.
            var firmware = architecture == MachineArchitecture.Arm64 ? "UEFI" : "ALL";
            await RunExternalAsync("bcdboot.exe", $"\"{windowsDir}\" /s {mediaRoot} /f {firmware}", ct);

            if (architecture == MachineArchitecture.Arm64)
                EnsureUefiFallbackLoader(windowsDir, mediaRoot, architecture);
        }
        finally
        {
            if (mounted)
            {
                try
                {
                    await RunExternalAsync("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", CancellationToken.None);
                }
                catch
                {
                    // Best effort — the mount was read-only, so there's nothing to lose by
                    // leaving it mounted; ElevationHelper cleanup below still removes the folder.
                }
            }
            ElevationHelper.TryDeleteDirectoryRecursive(mountDir);
        }
    }

    /// <summary>
    /// Ensures removable-media UEFI firmware can find the architecture's fallback loader
    /// (\efi\boot\bootaa64.efi for ARM64). bcdboot running on an x64 host may name it after the
    /// host, so it is copied from the image's own bootmgfw.efi when missing, then verified.
    /// </summary>
    private static void EnsureUefiFallbackLoader(string windowsDir, string mediaRoot, MachineArchitecture architecture)
    {
        var loaderName = MachineArchitecturePlatform.UefiBootLoaderFileName(architecture);
        var loaderPath = Path.Combine(mediaRoot + "\\", "efi", "boot", loaderName);
        if (!File.Exists(loaderPath))
        {
            var bootManager = Path.Combine(windowsDir, "Boot", "EFI", "bootmgfw.efi");
            if (!File.Exists(bootManager))
                throw new InvalidOperationException($"The boot image has no UEFI boot manager at \"{bootManager}\", so {loaderName} could not be created.");

            Directory.CreateDirectory(Path.GetDirectoryName(loaderPath)!);
            File.Copy(bootManager, loaderPath, overwrite: true);
        }

        var bcdPath = Path.Combine(mediaRoot + "\\", "efi", "microsoft", "boot", "BCD");
        if (!File.Exists(bcdPath))
            throw new InvalidOperationException($"bcdboot did not create the UEFI BCD store at \"{bcdPath}\". The USB device will not boot.");
    }

    /// <summary>
    /// Runs an external process to completion, capturing combined stdout/stderr for inclusion
    /// in the thrown exception on a non-zero exit code (FR-058 clear failure diagnostics).
    /// </summary>
    private static async Task RunExternalAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var ctReg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* best effort */ } });

        var exitCode = await tcs.Task;
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited with code {exitCode}.{(output.Length > 0 ? $" Output: {output}" : string.Empty)}");
    }

    private void ReportProgress(string message, int percent)
    {
        ProgressChanged?.Invoke(this, (message, percent));
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Deploying {WimPath} to USB {Drive}.")]
    private static partial void LogStarting(ILogger logger, string wimPath, string drive);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image deployment complete to {Drive}.")]
    private static partial void LogComplete(ILogger logger, string drive);

    [LoggerMessage(Level = LogLevel.Information, Message = "USB preparation manifest written to {ManifestPath} (bootImageVersion={BootImageVersion}).")]
    private static partial void LogManifestWritten(ILogger logger, string manifestPath, string bootImageVersion);
}

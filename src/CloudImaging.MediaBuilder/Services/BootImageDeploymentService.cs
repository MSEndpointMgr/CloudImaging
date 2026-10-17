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

    /// <summary>Raised as deployment progresses, with a status message and overall percent.</summary>
    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <summary>Builds the service over the given logger.</summary>
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

        // Step 2: Make the partition bootable from the boot.wim just copied (see ConfigureBootFilesAsync).
        ReportProgress("Configuring boot files…", 70);
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
    /// Makes the FAT32 boot partition bootable (BIOS + UEFI for x64, UEFI only for ARM64) from the
    /// boot.wim itself, so no ADK or extra published files are needed on this machine.
    /// <para/>
    /// <c>bcdboot</c> lays down the boot manager files, but the BCD it writes describes the
    /// Windows directory it was pointed at: the temporary mount on THIS machine. Booting that
    /// fails with 0xc000000e on <c>\Users\...\ci-deploy-mount-*\Windows\system32\winload.efi</c>.
    /// Both BCD stores are therefore replaced with the WinPE media templates
    /// (<c>\Windows\Boot\DVD\{EFI,PCAT}\BCD</c>), which RAM-disk boot <c>[boot]\sources\boot.wim</c>
    /// via <c>\boot\boot.sdi</c>, exactly like the ADK's MakeWinPEMedia output.
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
                    $"The boot image does not contain a \\Windows directory at \"{windowsDir}\". Cannot configure boot files.");

            // x64: /f ALL writes BIOS (bootmgr, \Boot\BCD) and UEFI (\efi\boot\bootx64.efi,
            // \efi\microsoft\boot\BCD) files. ARM64 is UEFI-only and its WinPE has no PCAT
            // files, so requesting BIOS support there would fail.
            var firmware = architecture == MachineArchitecture.Arm64 ? "UEFI" : "ALL";
            await RunExternalAsync("bcdboot.exe", $"\"{windowsDir}\" /s {mediaRoot} /f {firmware}", ct);

            if (architecture == MachineArchitecture.Arm64)
                EnsureUefiFallbackLoader(windowsDir, mediaRoot, architecture);

            var includeBios = architecture != MachineArchitecture.Arm64;
            InstallRamdiskBcdTemplates(windowsDir, mediaRoot, includeBios);
            await VerifyRamdiskBcdAsync(Path.Combine(mediaRoot + "\\", "EFI", "Microsoft", "Boot", "BCD"), ct);
            if (includeBios)
                await VerifyRamdiskBcdAsync(Path.Combine(mediaRoot + "\\", "Boot", "BCD"), ct);
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
    /// Overwrites the BCD stores with the WinPE ramdisk templates and adds <c>\Boot\boot.sdi</c>.
    /// The image's own templates are preferred; this machine's are an equivalent fallback, since
    /// BCD hives and boot.sdi are architecture-neutral data.
    /// </summary>
    private static void InstallRamdiskBcdTemplates(string windowsDir, string mediaRoot, bool includeBios)
    {
        var root = mediaRoot + "\\";
        CopyBootFile(FindBootTemplate(windowsDir, "EFI", "BCD"), Path.Combine(root, "EFI", "Microsoft", "Boot", "BCD"));
        CopyBootFile(FindBootTemplate(windowsDir, "EFI", "boot.sdi"), Path.Combine(root, "Boot", "boot.sdi"));
        if (includeBios)
            CopyBootFile(FindBootTemplate(windowsDir, "PCAT", "BCD"), Path.Combine(root, "Boot", "BCD"));
    }

    private static string FindBootTemplate(string windowsDir, string firmware, string fileName)
    {
        string[] candidates =
        [
            Path.Combine(windowsDir, "Boot", "DVD", firmware, fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "DVD", firmware, fileName),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"Could not find the WinPE boot template \\Windows\\Boot\\DVD\\{firmware}\\{fileName} in the boot image or on this computer. The USB device would not boot.");
    }

    /// <summary>Copies over files bcdboot may have left read-only/hidden/system, and leaves the copy writable for bootmgr.</summary>
    private static void CopyBootFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
            File.SetAttributes(destination, FileAttributes.Normal);
        File.Copy(source, destination, overwrite: true);
        File.SetAttributes(destination, FileAttributes.Normal);
    }

    /// <summary>Fails preparation unless the store RAM-disk boots \sources\boot.wim from the USB device itself.</summary>
    private static async Task VerifyRamdiskBcdAsync(string bcdPath, CancellationToken ct)
    {
        var output = await RunExternalAsync("bcdedit.exe", $"/store \"{bcdPath}\" /enum all", ct);
        if (!output.Contains(@"\sources\boot.wim", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ci-deploy-mount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The boot configuration at \"{bcdPath}\" does not boot \\sources\\boot.wim from the USB device. The USB device would not boot.");
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
    private static async Task<string> RunExternalAsync(string fileName, string arguments, CancellationToken ct)
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

        // Exited can fire before the redirected streams are drained; wait for EOF so callers see all output.
        await process.WaitForExitAsync(CancellationToken.None);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited with code {exitCode}.{(output.Length > 0 ? $" Output: {output}" : string.Empty)}");
        return output.ToString();
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

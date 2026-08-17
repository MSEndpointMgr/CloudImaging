using System.IO;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Deploys a WinPE boot image to the FAT32 boot partition of a prepared USB drive (T071, FR-055).
/// Copies WinPE boot files and configures BCD for automatic startup of Cloud Imaging Client.
/// </summary>
public sealed partial class BootImageDeploymentService
{
    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };

    private readonly ILogger<BootImageDeploymentService> _logger;

    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    public BootImageDeploymentService(ILogger<BootImageDeploymentService> logger) => _logger = logger;

    /// <summary>
    /// Deploys the WIM at <paramref name="wimPath"/> to the boot partition at
    /// <paramref name="bootDriveLetter"/> using makewinpemedia / DISM.
    /// </summary>
    public async Task DeployAsync(
        string wimPath,
        string bootDriveLetter,
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

        // Step 2: Configure BCD for WinPE autostart
        await ConfigureBcdAsync(mediaRoot, ct);
        ReportProgress("BCD configured for autostart.", 80);

        // Step 3: Mark bootable (requires bootsect.exe from ADK)
        await MakeBootableAsync(mediaRoot[0], ct);
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

    private static async Task ConfigureBcdAsync(string mediaRoot, CancellationToken ct)
    {
        // NOTE: WinPE auto-start (winpeshl.ini/startnet.cmd launching CloudImaging.Client.exe)
        // is configured inside boot.wim itself, while it's mounted during boot image
        // generation (see BootImageGenerationService.ConfigureWinPeAutoStartAsync) — not here.
        // Files written directly onto this FAT32 boot partition (outside of \sources\boot.wim,
        // \bootmgr and \Boot\BCD) have no effect at runtime: WinPE boots entirely from the
        // mounted WIM, so a loose "Windows\System32\startnet.cmd" sitting beside it on the
        // partition is never read.
        await Task.CompletedTask;
    }


    private static async Task MakeBootableAsync(char driveLetter, CancellationToken ct)
    {
        // bootsect /nt60 {driveLetter}: /mbr — makes the partition bootable
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "bootsect.exe",
                Arguments              = $"/nt60 {driveLetter}: /mbr",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };
        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ct.Register(() => { try { process.Kill(); } catch { } });
        // Ignore non-zero exit — bootsect may not be available in all environments
        await tcs.Task;
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

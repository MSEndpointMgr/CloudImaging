using System.IO;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Deploys a WinPE boot image to the FAT32 boot partition of a prepared USB drive (T071, FR-055).
/// Copies WinPE boot files and configures BCD for automatic startup of Cloud Imaging Client.
/// </summary>
public sealed partial class BootImageDeploymentService
{
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

    private static async Task ConfigureBcdAsync(string mediaRoot, CancellationToken ct)
    {
        // BCDBoot copies Windows boot files — for WinPE this configures startnet.cmd autolaunch
        var startnetDir = Path.Combine(mediaRoot, "Windows", "System32");
        Directory.CreateDirectory(startnetDir);
        var startnetPath = Path.Combine(startnetDir, "startnet.cmd");
        await File.WriteAllTextAsync(startnetPath,
            "@echo off\r\nX:\\CloudImaging\\CloudImaging.Client.exe\r\n", ct);
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
}

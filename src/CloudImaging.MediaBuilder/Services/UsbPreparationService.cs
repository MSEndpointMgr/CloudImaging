using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Orchestrates the destructive half of the "Prepare USB Storage Device" workflow (T070, T071,
/// FR-055, FR-057) — partitioning the disk (<see cref="UsbPartitionProvisioningService"/>,
/// diskpart.exe) and deploying/activating the boot image
/// (<see cref="BootImageDeploymentService"/>, bootsect.exe) — transparently elevating when
/// required, exactly like <see cref="BootImageGenerationService.GenerateElevatedAsync"/> does
/// for DISM image mounting.
///
/// diskpart.exe (clean/convert/format/assign) and bootsect.exe (MBR write) both require
/// Administrator privileges. The main Media Builder process must stay non-elevated because
/// Entra ID sign-in relies on MSAL's Windows broker (WAM), which does not work reliably from an
/// elevated process. To reconcile this, when the current process is NOT elevated,
/// <see cref="PrepareElevatedAsync"/> relaunches this same executable elevated (one UAC prompt)
/// as a short-lived worker that performs only the partition/deploy/manifest work, streaming
/// progress back via small IPC files — the same file-based handshake used by
/// <see cref="BootImageGenerationService.RunElevatedChildProcessAsync"/>. The boot image itself
/// must already be downloaded to a local path before calling this (network/Operator API calls
/// must happen in the non-elevated parent, same reasoning as the WAM broker constraint above).
///
/// UI code should call <see cref="PrepareElevatedAsync"/> instead of <see cref="PrepareAsync"/>.
/// </summary>
public sealed partial class UsbPreparationService
{
    /// <summary>
    /// Prefix for the per-run IPC directory created under <c>%TEMP%</c> by
    /// <see cref="RunElevatedChildProcessAsync"/>. Shared with <see cref="CleanupOrphanedIpcDirs"/>
    /// so both stay in sync — mirrors <c>BootImageGenerationService.ElevatedIpcDirPrefix</c>.
    /// </summary>
    private const string ElevatedIpcDirPrefix = "ci-usbprep-elevated-";

    /// <summary>Hidden CLI switch used to relaunch this same executable elevated (see <see cref="PrepareElevatedAsync"/>).</summary>
    public const string ElevatedWorkerArg = "--elevated-prepare-usb";

    private readonly ILogger<UsbPreparationService> _logger;
    private readonly UsbPartitionProvisioningService _provisioner;
    private readonly BootImageDeploymentService _deployer;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, string, Process> _startElevatedProcess;

    /// <summary>
    /// Raised with the overall workflow progress (partitioning + deployment + manifest write
    /// occupy the 60-100% band of the wider Prepare workflow, matching what
    /// <c>PrepareStorageDeviceViewModel</c> previously computed itself for these steps).
    /// </summary>
    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <param name="isElevatedOverride">Test seam. Defaults to a real check of the current process token.</param>
    /// <param name="startElevatedProcessOverride">Test seam. Defaults to a real "runas"-elevated <see cref="Process"/> launch.</param>
    public UsbPreparationService(
        ILogger<UsbPreparationService> logger,
        UsbPartitionProvisioningService provisioner,
        BootImageDeploymentService deployer,
        Func<bool>? isElevatedOverride = null,
        Func<string, string, Process>? startElevatedProcessOverride = null)
    {
        _logger                = logger;
        _provisioner           = provisioner;
        _deployer              = deployer;
        _isElevated            = isElevatedOverride ?? ElevationHelper.IsElevated;
        _startElevatedProcess  = startElevatedProcessOverride ?? ElevationHelper.StartElevatedProcess;
    }

    /// <summary>Everything the (possibly elevated, separate-process) worker needs — no Operator API/network calls of its own.</summary>
    public sealed record PreparationParams(
        uint DiskNumber,
        long DiskSizeBytes,
        string WimPath,
        string BootImageVersion,
        string SelectedDiskId,
        string BusType,
        bool DiskValidated,
        string ToolVersion);

    public sealed record PreparationResult(string BootDriveLetter, string? CacheDriveLetter);

    private sealed record ElevatedPreparationResult(bool Success, string? BootDriveLetter, string? CacheDriveLetter, string? Error);

    /// <summary>
    /// Partitions the USB disk, deploys the boot image, and writes the preparation manifest —
    /// transparently elevating (one UAC prompt) when the current process isn't already
    /// Administrator. When already elevated, calls <see cref="PrepareAsync"/> directly with no
    /// relaunch.
    /// </summary>
    public async Task<PreparationResult> PrepareElevatedAsync(PreparationParams p, CancellationToken ct = default)
    {
        if (_isElevated())
            return await PrepareAsync(p, ct);

        return await RunElevatedChildProcessAsync(p, ct);
    }

    /// <summary>
    /// Performs the actual partition + deploy + manifest work. Always runs elevated (either the
    /// whole process is already elevated, or this IS the elevated worker relaunched by
    /// <see cref="PrepareElevatedAsync"/>).
    /// </summary>
    public async Task<PreparationResult> PrepareAsync(PreparationParams p, CancellationToken ct)
    {
        ReportProgress("Partitioning USB device…", 60);
        await _provisioner.ProvisionAsync(p.DiskNumber, p.DiskSizeBytes, msg => ReportProgress(msg, 60), ct);

        var bootDrive = _provisioner.FindBootVolumeDriveLetter()
            ?? throw new InvalidOperationException(
                "Could not locate the BOOT partition after provisioning the USB device.");
        var cacheDrive = _provisioner.FindCacheVolumeDriveLetter();

        ReportProgress("Deploying boot image to USB…", 70);
        void OnDeployProgress(object? _, (string Message, int Percent) e) =>
            ReportProgress(e.Message, 70 + (int)(0.28 * e.Percent));
        _deployer.ProgressChanged += OnDeployProgress;
        try
        {
            await _deployer.DeployAsync(p.WimPath, bootDrive, ct);
        }
        finally
        {
            _deployer.ProgressChanged -= OnDeployProgress;
        }

        // T071a/FR-059: persist the preparation manifest to the BOOT partition so the Client
        // can later detect a newer published boot image (T071b/FR-059a).
        ReportProgress("Writing USB preparation manifest…", 99);
        var manifest = new UsbPreparationManifest
        {
            ManifestVersion  = UsbPreparationManifest.ManifestSchemaVersion,
            PreparedAt       = DateTimeOffset.UtcNow,
            ToolVersion      = p.ToolVersion,
            BootImageVersion = p.BootImageVersion,
            SelectedDiskId   = p.SelectedDiskId,
            PartitionSchema  = new Dictionary<string, object>
            {
                ["bootDriveLetter"]  = bootDrive,
                ["cacheDriveLetter"] = cacheDrive ?? string.Empty,
                ["diskSizeBytes"]    = p.DiskSizeBytes,
            },
            ValidationResults = new Dictionary<string, object>
            {
                ["busType"]        = p.BusType,
                ["diskValidated"]  = p.DiskValidated,
            },
            AutoStartConfigured = true,
        };
        await _deployer.WriteUsbPreparationManifestAsync(bootDrive, manifest, ct);

        ReportProgress("USB device prepared successfully.", 100);
        LogComplete(_logger, bootDrive);
        return new PreparationResult(bootDrive, cacheDrive);
    }

    private async Task<PreparationResult> RunElevatedChildProcessAsync(PreparationParams p, CancellationToken ct)
    {
        // A previous run's IPC folder is only ever left behind when this (non-elevated) parent
        // process itself was killed/crashed before its own finally block ran. These folders
        // hold nothing but small IPC files, so sweeping them up needs no elevation and is
        // always safe to do up front (mirrors BootImageGenerationService.CleanupOrphanedIpcDirs).
        CleanupOrphanedIpcDirs();

        var ipcDir = Path.Combine(Path.GetTempPath(), $"{ElevatedIpcDirPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDir);
        var paramsFile   = Path.Combine(ipcDir, "params.json");
        var progressFile = Path.Combine(ipcDir, "progress.txt");
        var resultFile   = Path.Combine(ipcDir, "result.json");
        var cancelFile   = Path.Combine(ipcDir, "cancel.flag");

        try
        {
            await File.WriteAllTextAsync(paramsFile, JsonSerializer.Serialize(p), ct);
            File.WriteAllText(progressFile, string.Empty);

            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
            var args = $"{ElevatedWorkerArg} \"{paramsFile}\" \"{progressFile}\" \"{resultFile}\" \"{cancelFile}\"";

            ReportProgress("Requesting Administrator privileges", 58);

            Process elevatedProcess;
            try
            {
                elevatedProcess = _startElevatedProcess(exePath, args);
            }
            catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — the user declined the UAC prompt.
                throw new InvalidOperationException(
                    "Administrator elevation was cancelled. Preparing a USB device requires " +
                    "Administrator privileges to partition the disk and configure it as bootable.");
            }

            // Cancellation is signalled to the elevated worker via an IPC file rather than
            // killing the process outright, giving it a chance to finish (or safely abandon)
            // whatever diskpart/deploy step is in flight — mirrors
            // BootImageGenerationService.RunElevatedChildProcessAsync.
            using var ctReg = ct.Register(() =>
            {
                try { File.WriteAllText(cancelFile, string.Empty); } catch { /* best effort */ }
            });

            var linesRead = 0;
            DateTime? cancelSignalledAt = null;
            using (elevatedProcess)
            {
                // Treat the worker's result file (always written on success AND failure) as
                // the authoritative completion signal — the process handle from "runas" is
                // frequently a short-lived launcher rather than the real elevated worker (UAC's
                // consent.exe, privilege brokers, etc.), so HasExited is unreliable.
                while (!File.Exists(resultFile))
                {
                    await Task.Delay(300, CancellationToken.None);
                    linesRead = TailProgress(progressFile, linesRead);

                    if (ct.IsCancellationRequested)
                    {
                        cancelSignalledAt ??= DateTime.UtcNow;
                        // Give the worker a grace period to finish its current step and delete
                        // its own IPC/temp files before falling back to a hard kill.
                        if (DateTime.UtcNow - cancelSignalledAt > TimeSpan.FromSeconds(30))
                        {
                            try { elevatedProcess.Kill(); } catch { /* best effort */ }
                            break;
                        }
                    }
                }
                linesRead = TailProgress(progressFile, linesRead);
            }

            if (!File.Exists(resultFile))
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException("USB device preparation was cancelled.", ct);
                throw new InvalidOperationException(
                    "The elevated USB preparation process exited without reporting a result.");
            }

            var resultJson = await File.ReadAllTextAsync(resultFile, CancellationToken.None);
            var result = JsonSerializer.Deserialize<ElevatedPreparationResult>(resultJson)
                ?? throw new InvalidOperationException("Could not parse the elevated process result.");

            if (!result.Success)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(result.Error ?? "USB device preparation was cancelled.", ct);
                throw new InvalidOperationException(result.Error ?? "USB device preparation failed in the elevated process.");
            }

            return new PreparationResult(result.BootDriveLetter!, result.CacheDriveLetter);
        }
        finally
        {
            ElevationHelper.TryDeleteDirectoryRecursive(ipcDir);
        }
    }

    private int TailProgress(string progressFile, int fromLine)
    {
        string[] lines;
        try { lines = File.ReadAllLines(progressFile); }
        catch (IOException) { return fromLine; } // file briefly locked by the writer — try again next tick

        for (var i = fromLine; i < lines.Length; i++)
        {
            var line = lines[i];
            // Typed IPC line written by the elevated worker: "P\t{percent}\t{message}".
            if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length == 3 && int.TryParse(parts[1], out var percent))
                    ReportProgress(parts[2], percent);
            }
        }
        return lines.Length;
    }

    /// <summary>
    /// Entry point for the elevated worker process. Invoked from App.OnStartup when this
    /// executable is relaunched with <see cref="ElevatedWorkerArg"/> by
    /// <see cref="PrepareElevatedAsync"/>. Runs entirely non-interactively: reads preparation
    /// parameters from <paramref name="paramsFile"/>, performs the partition/deploy/manifest
    /// work (this process IS elevated), and writes progress/result to the given IPC files.
    /// </summary>
    public static async Task RunElevatedWorkerAsync(
        string paramsFile, string progressFile, string resultFile, string cancelFile, ILoggerFactory loggerFactory)
    {
        using var cts = new CancellationTokenSource();
        var cancelWatcherTask = WatchForCancelSignalAsync(cancelFile, cts);

        try
        {
            var json = await File.ReadAllTextAsync(paramsFile);
            var p = JsonSerializer.Deserialize<PreparationParams>(json)
                ?? throw new InvalidOperationException("Could not parse USB preparation parameters.");

            var provisioner = new UsbPartitionProvisioningService(loggerFactory.CreateLogger<UsbPartitionProvisioningService>());
            var deployer    = new BootImageDeploymentService(loggerFactory.CreateLogger<BootImageDeploymentService>());
            var svc         = new UsbPreparationService(loggerFactory.CreateLogger<UsbPreparationService>(), provisioner, deployer);

            var ipcLock = new object();
            void Append(string typedLine)
            {
                lock (ipcLock)
                {
                    try { File.AppendAllText(progressFile, typedLine + Environment.NewLine); }
                    catch { /* best effort — parent will just miss this tick */ }
                }
            }
            svc.ProgressChanged += (_, e) => Append($"P\t{e.Percent}\t{e.Message}");

            var result = await svc.PrepareAsync(p, cts.Token);

            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedPreparationResult(true, result.BootDriveLetter, result.CacheDriveLetter, null)));
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedPreparationResult(false, null, null, "USB device preparation was cancelled.")));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedPreparationResult(false, null, null, ex.Message)));
        }
        finally
        {
            cts.Cancel();
            try { await cancelWatcherTask; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Polls for the parent-created <paramref name="cancelFile"/> and cancels
    /// <paramref name="cts"/> once it appears — the cooperative half of the elevated-worker
    /// cancellation handshake described on <see cref="RunElevatedWorkerAsync"/>.
    /// </summary>
    private static async Task WatchForCancelSignalAsync(string cancelFile, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(cancelFile))
                {
                    cts.Cancel();
                    return;
                }
                await Task.Delay(300, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once preparation finishes and the caller cancels cts in its finally.
        }
    }

    /// <summary>
    /// Sweeps up leftover <c>%TEMP%\ci-usbprep-elevated-*</c> IPC folders from a previous
    /// (non-elevated) parent process that was itself killed/crashed before
    /// <see cref="RunElevatedChildProcessAsync"/>'s own "finally" ran. Best-effort: must never
    /// block starting a new elevated run.
    /// </summary>
    private void CleanupOrphanedIpcDirs()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), $"{ElevatedIpcDirPrefix}*"))
                ElevationHelper.TryDeleteDirectoryRecursive(dir);
        }
        catch (Exception ex)
        {
            LogOrphanedIpcDirScanFailed(_logger, ex);
        }
    }

    private void ReportProgress(string message, int percent) => ProgressChanged?.Invoke(this, (message, percent));

    [LoggerMessage(Level = LogLevel.Information, Message = "USB device preparation complete (boot partition {BootDrive}).")]
    private static partial void LogComplete(ILogger logger, string bootDrive);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan for orphaned elevated USB-preparation IPC directories.")]
    private static partial void LogOrphanedIpcDirScanFailed(ILogger logger, Exception ex);
}

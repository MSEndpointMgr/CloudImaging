using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Packages an already-downloaded WinPE boot WIM into a bootable ISO file, so a technician can
/// attach it directly to a Hyper-V VM's DVD drive (or burn it to physical media) instead of
/// writing it to a physical USB device.
///
/// Reuses the same Windows ADK (Deployment Tools + WinPE add-on) already required by
/// <see cref="BootImageGenerationService"/> — <see cref="BootImageGenerationService.FindAdkPath"/>
/// locates it, and <c>copype.cmd</c>/<c>MakeWinPEMedia.cmd</c> (both part of the WinPE add-on)
/// do the actual staging/ISO packaging. A full bootable ISO needs the WinPE boot manager files
/// (bootmgr, BCD, efi\microsoft\boot\bootmgfw.efi) that ship with the add-on's media template —
/// not just Oscdimg's boot-sector files — so copype.cmd is used to stage a fresh media tree,
/// the selected WIM is swapped in for its default boot.wim, and MakeWinPEMedia.cmd wraps that
/// tree into a hybrid BIOS+UEFI ISO.
///
/// copype.cmd mounts its template boot.wim via DISM while staging the media tree, and DISM
/// mounting requires Administrator privileges — exactly like the DISM mount
/// <see cref="BootImageGenerationService"/> performs directly. This service therefore
/// transparently elevates (one UAC prompt) the same way <see cref="BootImageGenerationService.GenerateElevatedAsync"/>
/// and <see cref="UsbPreparationService.PrepareElevatedAsync"/> do: the main Media Builder
/// process must stay non-elevated (Entra ID sign-in relies on MSAL's Windows broker (WAM),
/// which does not work reliably from an elevated process), so <see cref="GenerateElevatedAsync"/>
/// relaunches this same executable elevated as a short-lived worker when needed, streaming
/// progress back via small IPC files.
///
/// UI code should call <see cref="GenerateElevatedAsync"/> instead of <see cref="GenerateAsync"/>.
/// </summary>
public sealed partial class IsoGenerationService
{
    private const string WinPeArch = "amd64";

    /// <summary>
    /// Prefix for the per-run staging directory created under <c>%TEMP%</c>. Not swept up by
    /// any orphan-cleanup pass (unlike <see cref="BootImageGenerationService"/>'s work
    /// directories) since no DISM mount point is ever created under one — the worst a crash
    /// leaves behind is a small, harmless leftover folder of plain files.
    /// </summary>
    private const string WorkDirPrefix = "ci-iso-";

    /// <summary>
    /// Prefix for the per-run IPC directory created under <c>%TEMP%</c> by
    /// <see cref="RunElevatedChildProcessAsync"/>. Shared with <see cref="CleanupOrphanedIpcDirs"/>
    /// so both stay in sync — mirrors <c>UsbPreparationService.ElevatedIpcDirPrefix</c>.
    /// </summary>
    private const string ElevatedIpcDirPrefix = "ci-iso-elevated-";

    /// <summary>Hidden CLI switch used to relaunch this same executable elevated (see <see cref="GenerateElevatedAsync"/>).</summary>
    public const string ElevatedWorkerArg = "--elevated-generate-iso";

    private readonly ILogger<IsoGenerationService> _logger;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, string, Process> _startElevatedProcess;

    private sealed record IsoGenerationParams(string WimPath, string OutputIsoPath);
    private sealed record ElevatedIsoGenerationResult(bool Success, string? Error);

    /// <param name="isElevatedOverride">Test seam. Defaults to a real check of the current process token.</param>
    /// <param name="startElevatedProcessOverride">Test seam. Defaults to a real "runas"-elevated <see cref="Process"/> launch.</param>
    public IsoGenerationService(
        ILogger<IsoGenerationService> logger,
        Func<bool>? isElevatedOverride = null,
        Func<string, string, Process>? startElevatedProcessOverride = null)
    {
        _logger               = logger;
        _isElevated           = isElevatedOverride ?? ElevationHelper.IsElevated;
        _startElevatedProcess = startElevatedProcessOverride ?? ElevationHelper.StartElevatedProcess;
    }

    /// <summary>
    /// True when the Windows ADK (with the WinPE add-on) needed to build an ISO is installed.
    /// Shares detection with <see cref="BootImageGenerationService.IsAdkInstalled"/> since both
    /// features rely on the exact same copype.cmd/MakeWinPEMedia.cmd tooling.
    /// </summary>
    public static bool IsAdkInstalled() => BootImageGenerationService.IsAdkInstalled();

    /// <summary>
    /// Packages <paramref name="wimPath"/> into a bootable ISO at <paramref name="outputIsoPath"/>,
    /// transparently elevating (one UAC prompt) when the current process isn't already
    /// Administrator — copype.cmd's DISM mount step requires it. When already elevated, calls
    /// <see cref="GenerateAsync"/> directly with no relaunch.
    /// </summary>
    /// <param name="wimPath">Path to the already-downloaded/hash-verified boot WIM.</param>
    /// <param name="outputIsoPath">Full path (including file name) the finished ISO is written to. Overwritten if it already exists.</param>
    /// <param name="onProgress">Optional callback: (message, percent 0-100).</param>
    public async Task GenerateElevatedAsync(
        string wimPath,
        string outputIsoPath,
        Action<string, int>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_isElevated())
        {
            await GenerateAsync(wimPath, outputIsoPath, onProgress, ct);
            return;
        }

        await RunElevatedChildProcessAsync(wimPath, outputIsoPath, onProgress, ct);
    }

    /// <param name="wimPath">Path to the already-downloaded/hash-verified boot WIM.</param>
    /// <param name="outputIsoPath">Full path (including file name) the finished ISO is written to. Overwritten if it already exists.</param>
    /// <param name="onProgress">Optional callback: (message, percent 0-100).</param>
    public async Task GenerateAsync(
        string wimPath,
        string outputIsoPath,
        Action<string, int>? onProgress = null,
        CancellationToken ct = default)
    {
        var adkPath = BootImageGenerationService.FindAdkPath()
            ?? throw new InvalidOperationException(
                "Windows ADK with WinPE add-on is not installed. " +
                "Download both from https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install");

        var workDir = Path.Combine(Path.GetTempPath(), $"{WorkDirPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            onProgress?.Invoke("Staging WinPE media files…", 10);
            var winPeRoot = Path.Combine(workDir, "WinPE");
            await CopyWinPeFilesAsync(adkPath, winPeRoot, ct);

            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("Inserting selected boot image into WinPE media…", 45);
            var mediaWimPath = Path.Combine(winPeRoot, "media", "sources", "boot.wim");
            DiskSpaceGuard.EnsureFreeSpace(winPeRoot, new FileInfo(wimPath).Length, "stage the boot image for ISO packaging");
            File.Copy(wimPath, mediaWimPath, overwrite: true);

            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke("Building bootable ISO…", 55);
            var outputDir = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            if (File.Exists(outputIsoPath))
                File.Delete(outputIsoPath);

            await RunMakeWinPeMediaAsync(adkPath, winPeRoot, outputIsoPath, ct);

            onProgress?.Invoke("ISO file created successfully.", 100);
            LogIsoGenerated(_logger, outputIsoPath);
        }
        finally
        {
            TryDeleteDirectoryRecursive(workDir);
        }
    }

    /// <summary>Stages a fresh WinPE media tree via copype.cmd — identical env-var setup to <see cref="BootImageGenerationService"/>'s own copype invocation, since both shell out to the same ADK tools.</summary>
    private static async Task CopyWinPeFilesAsync(string adkPath, string winPeRoot, CancellationToken ct)
    {
        var copype = Path.Combine(adkPath, "Windows Preinstallation Environment", "copype.cmd");
        var env = new Dictionary<string, string>
        {
            ["WinPERoot"]   = Path.Combine(adkPath, "Windows Preinstallation Environment"),
            ["OSCDImgRoot"] = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "Oscdimg"),
            ["DISMRoot"]    = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "DISM"),
        };

        // See BootImageGenerationService.CopyWinPeFilesAsync for why the whole /c argument is
        // wrapped in one extra outer pair of quotes (cmd.exe's quote-stripping fallback with
        // more than two embedded quotes, which both copype's own path and winPeRoot can contain
        // via spaces).
        await RunExternalAsync("cmd.exe", $"/c \"\"{copype}\" {WinPeArch} \"{winPeRoot}\"\"", env, ct);
    }

    private static async Task RunMakeWinPeMediaAsync(string adkPath, string winPeRoot, string outputIsoPath, CancellationToken ct)
    {
        var makeMedia   = Path.Combine(adkPath, "Windows Preinstallation Environment", "MakeWinPEMedia.cmd");
        var oscdimgRoot = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "Oscdimg");

        // Unlike copype.cmd (which calls Dism.exe via the fully-qualified "%DISMRoot%\Dism.exe"),
        // MakeWinPEMedia.cmd's ISO-packaging path invokes "oscdimg" by bare name and relies on
        // it being resolvable via PATH — normally true only inside the ADK's own "Deployment
        // and Imaging Tools Environment" prompt (Deployment Tools\DandISetEnv.bat), which this
        // app never launches from. Without prepending OSCDImgRoot onto PATH here, the child
        // cmd.exe fails with "'oscdimg' is not recognized as an internal or external command"
        // even though the ADK/WinPE add-on are correctly installed.
        var env = new Dictionary<string, string>
        {
            ["WinPERoot"]   = Path.Combine(adkPath, "Windows Preinstallation Environment"),
            ["OSCDImgRoot"] = oscdimgRoot,
            ["PATH"]        = oscdimgRoot + ";" + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
        };

        await RunExternalAsync(
            "cmd.exe",
            $"/c \"\"{makeMedia}\" /ISO \"{winPeRoot}\" \"{outputIsoPath}\"\"",
            env, ct);
    }

    private static async Task RunExternalAsync(string fileName, string arguments, Dictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}.{Environment.NewLine}{stdOut}{Environment.NewLine}{stdErr}");
    }

    private static void TryDeleteDirectoryRecursive(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private async Task RunElevatedChildProcessAsync(
        string wimPath, string outputIsoPath, Action<string, int>? onProgress, CancellationToken ct)
    {
        // A previous run's IPC folder is only ever left behind when this (non-elevated) parent
        // process itself was killed/crashed before its own finally block ran. These folders
        // hold nothing but small IPC files, so sweeping them up needs no elevation and is
        // always safe to do up front (mirrors UsbPreparationService.CleanupOrphanedIpcDirs).
        CleanupOrphanedIpcDirs();

        var ipcDir = Path.Combine(Path.GetTempPath(), $"{ElevatedIpcDirPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDir);
        var paramsFile   = Path.Combine(ipcDir, "params.json");
        var progressFile = Path.Combine(ipcDir, "progress.txt");
        var resultFile   = Path.Combine(ipcDir, "result.json");
        var cancelFile   = Path.Combine(ipcDir, "cancel.flag");

        try
        {
            var request = new IsoGenerationParams(wimPath, outputIsoPath);
            await File.WriteAllTextAsync(paramsFile, JsonSerializer.Serialize(request), ct);
            File.WriteAllText(progressFile, string.Empty);

            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
            var args = $"{ElevatedWorkerArg} \"{paramsFile}\" \"{progressFile}\" \"{resultFile}\" \"{cancelFile}\"";

            onProgress?.Invoke("Requesting Administrator privileges…", 2);

            Process elevatedProcess;
            try
            {
                elevatedProcess = _startElevatedProcess(exePath, args);
            }
            catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — the user declined the UAC prompt.
                throw new InvalidOperationException(
                    "Administrator elevation was cancelled. Generating an ISO requires " +
                    "Administrator privileges to stage the WinPE media (copype.cmd mounts its " +
                    "template image via DISM).");
            }

            // Cancellation is signalled to the elevated worker via an IPC file rather than
            // killing the process outright, giving it a chance to finish (or safely abandon)
            // whatever staging/packaging step is in flight — mirrors
            // UsbPreparationService.RunElevatedChildProcessAsync.
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
                // frequently a short-lived launcher rather than the real elevated worker.
                while (!File.Exists(resultFile))
                {
                    await Task.Delay(300, CancellationToken.None);
                    linesRead = TailProgress(progressFile, linesRead, onProgress);

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
                linesRead = TailProgress(progressFile, linesRead, onProgress);
            }

            if (!File.Exists(resultFile))
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException("ISO generation was cancelled.", ct);
                throw new InvalidOperationException(
                    "The elevated ISO generation process exited without reporting a result.");
            }

            var resultJson = await File.ReadAllTextAsync(resultFile, CancellationToken.None);
            var result = JsonSerializer.Deserialize<ElevatedIsoGenerationResult>(resultJson)
                ?? throw new InvalidOperationException("Could not parse the elevated process result.");

            if (!result.Success)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(result.Error ?? "ISO generation was cancelled.", ct);
                throw new InvalidOperationException(result.Error ?? "ISO generation failed in the elevated process.");
            }
        }
        finally
        {
            ElevationHelper.TryDeleteDirectoryRecursive(ipcDir);
        }
    }

    private static int TailProgress(string progressFile, int fromLine, Action<string, int>? onProgress)
    {
        string[] lines;
        try { lines = File.ReadAllLines(progressFile); }
        catch (IOException) { return fromLine; } // file briefly locked by the writer — try again next tick

        for (var i = fromLine; i < lines.Length; i++)
        {
            // Typed IPC line written by the elevated worker: "P\t{percent}\t{message}".
            var line = lines[i];
            if (!line.StartsWith("P\t", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], out var percent))
                onProgress?.Invoke(parts[2], percent);
        }
        return lines.Length;
    }

    /// <summary>
    /// Entry point for the elevated worker process. Invoked from App.OnStartup when this
    /// executable is relaunched with <see cref="ElevatedWorkerArg"/> by
    /// <see cref="GenerateElevatedAsync"/>. Runs entirely non-interactively: reads the wim/output
    /// paths from <paramref name="paramsFile"/>, performs the staging/packaging work (this
    /// process IS elevated), and writes progress/result to the given IPC files.
    /// </summary>
    public static async Task RunElevatedWorkerAsync(
        string paramsFile, string progressFile, string resultFile, string cancelFile, ILoggerFactory loggerFactory)
    {
        using var cts = new CancellationTokenSource();
        var cancelWatcherTask = WatchForCancelSignalAsync(cancelFile, cts);

        try
        {
            var json = await File.ReadAllTextAsync(paramsFile);
            var p = JsonSerializer.Deserialize<IsoGenerationParams>(json)
                ?? throw new InvalidOperationException("Could not parse ISO generation parameters.");

            var svc = new IsoGenerationService(loggerFactory.CreateLogger<IsoGenerationService>());

            var ipcLock = new object();
            void OnProgress(string message, int percent)
            {
                lock (ipcLock)
                {
                    try { File.AppendAllText(progressFile, $"P\t{percent}\t{message}" + Environment.NewLine); }
                    catch { /* best effort — parent will just miss this tick */ }
                }
            }

            await svc.GenerateAsync(p.WimPath, p.OutputIsoPath, OnProgress, cts.Token);

            await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new ElevatedIsoGenerationResult(true, null)));
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedIsoGenerationResult(false, "ISO generation was cancelled.")));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new ElevatedIsoGenerationResult(false, ex.Message)));
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
            // Expected once generation finishes and the caller cancels cts in its finally.
        }
    }

    /// <summary>
    /// Sweeps up leftover <c>%TEMP%\ci-iso-elevated-*</c> IPC folders from a previous
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

    [LoggerMessage(Level = LogLevel.Information, Message = "ISO file generated at {OutputIsoPath}.")]
    private static partial void LogIsoGenerated(ILogger logger, string outputIsoPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan for orphaned elevated ISO-generation IPC directories.")]
    private static partial void LogOrphanedIpcDirScanFailed(ILogger logger, Exception ex);
}

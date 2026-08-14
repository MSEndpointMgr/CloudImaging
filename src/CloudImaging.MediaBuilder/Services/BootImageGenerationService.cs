using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Orchestrates the WinPE boot image generation workflow (T064/T173, FR-051, FR-067, FR-070).
///
/// Workflow steps:
///   1. Verify ADK / WinPE add-on is installed.
///   2. Optionally retrieve the active boot media certificate PFX from the Operator API.
///   3. Locate or download Cloud Imaging Client binaries.
///   4. Copy WinPE base files to working directory.
///   5. Mount WIM, inject Client binaries + cert + branding.
///   6. Optionally inject pre-staged storage/network drivers (FR-051c).
///   7. Unmount and commit WIM.
///   8. Output WIM to the specified output directory.
/// </summary>
public sealed partial class BootImageGenerationService
{
    private const string WinPeArch = "amd64";

    /// <summary>Hidden CLI switch used to relaunch this same executable elevated (see <see cref="GenerateElevatedAsync"/>).</summary>
    public const string ElevatedWorkerArg = "--elevated-generate";

    private readonly ILogger<BootImageGenerationService> _logger;
    private readonly OperatorApiClient? _operatorApiClient;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, string, System.Diagnostics.Process> _startElevatedProcess;

    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <summary>
    /// Raised for every command executed and every line of tool output, so the UI can show
    /// a live command/activity log (e.g. the copype.cmd and dism.exe invocations and their
    /// output). Independent of <see cref="ProgressChanged"/>, which drives the step/percent UI.
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <param name="operatorApiClient">
    /// Optional. When provided, the service will attempt to retrieve the active boot
    /// media certificate PFX from the Operator API and embed it in the WIM (T173, FR-070).
    /// When null, pfxBytes must be supplied by the caller or cert embedding is skipped.
    /// </param>
    /// <param name="isElevatedOverride">Test seam. Defaults to a real check of the current process token.</param>
    /// <param name="startElevatedProcessOverride">Test seam. Defaults to a real "runas"-elevated <see cref="System.Diagnostics.Process"/> launch.</param>
    public BootImageGenerationService(
        ILogger<BootImageGenerationService> logger,
        OperatorApiClient? operatorApiClient = null,
        Func<bool>? isElevatedOverride = null,
        Func<string, string, System.Diagnostics.Process>? startElevatedProcessOverride = null)
    {
        _logger                = logger;
        _operatorApiClient     = operatorApiClient;
        _isElevated            = isElevatedOverride ?? IsElevated;
        _startElevatedProcess  = startElevatedProcessOverride ?? StartElevatedProcess;
    }

    public sealed record GenerationResult(string WimPath, string Sha256Hash);

    private sealed record ElevatedGenerationParams(
        string ClientBinariesPath, string OutputDirectory, string? DriverRootPath, string? PfxFilePath);

    private sealed record ElevatedGenerationResult(
        bool Success, string? WimPath, string? Sha256Hash, string? Error);

    /// <summary>
    /// Generates a WinPE boot image, transparently elevating when required.
    ///
    /// DISM image mounting (used to customize the WinPE WIM) requires Administrator
    /// privileges, but the main Media Builder process must stay non-elevated because Entra
    /// ID sign-in relies on MSAL's Windows broker (WAM), which does not work reliably from
    /// an elevated process. To reconcile this, when the current process is NOT elevated,
    /// this method relaunches this same executable elevated (triggering one UAC prompt) as a
    /// short-lived worker that performs only the DISM/copype work, and streams its progress
    /// and result back via small IPC files. When already elevated (e.g. launched via "Run as
    /// Administrator"), it just calls <see cref="GenerateAsync"/> directly with no relaunch.
    ///
    /// UI code should call this method instead of <see cref="GenerateAsync"/>.
    /// </summary>
    public async Task<GenerationResult> GenerateElevatedAsync(
        string clientBinariesPath,
        byte[]? pfxBytes,
        string outputDirectory,
        string? driverRootPath = null,
        CancellationToken ct = default)
    {
        if (_isElevated())
            return await GenerateAsync(clientBinariesPath, pfxBytes, outputDirectory, driverRootPath, ct);

        return await RunElevatedChildProcessAsync(clientBinariesPath, pfxBytes, outputDirectory, driverRootPath, ct);
    }

    private async Task<GenerationResult> RunElevatedChildProcessAsync(
        string clientBinariesPath, byte[]? pfxBytes, string outputDirectory, string? driverRootPath, CancellationToken ct)
    {
        var ipcDir = Path.Combine(Path.GetTempPath(), $"ci-elevated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDir);
        var paramsFile   = Path.Combine(ipcDir, "params.json");
        var progressFile = Path.Combine(ipcDir, "progress.txt");
        var resultFile   = Path.Combine(ipcDir, "result.json");

        try
        {
            string? pfxFilePath = null;
            if (pfxBytes is { Length: > 0 })
            {
                pfxFilePath = Path.Combine(ipcDir, "cert.pfx");
                await File.WriteAllBytesAsync(pfxFilePath, pfxBytes, ct);
            }

            var request = new ElevatedGenerationParams(clientBinariesPath, outputDirectory, driverRootPath, pfxFilePath);
            await File.WriteAllTextAsync(paramsFile, JsonSerializer.Serialize(request), ct);
            File.WriteAllText(progressFile, string.Empty);

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
            var args = $"{ElevatedWorkerArg} \"{paramsFile}\" \"{progressFile}\" \"{resultFile}\"";

            ReportProgress("Requesting Administrator privileges (DISM requires elevation to mount the WinPE image)…", 3);

            System.Diagnostics.Process elevatedProcess;
            try
            {
                elevatedProcess = _startElevatedProcess(exePath, args);
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — the user declined the UAC prompt.
                throw new InvalidOperationException(
                    "Administrator elevation was cancelled. Boot image generation requires " +
                    "Administrator privileges to mount the WinPE image via DISM.");
            }

            // Best-effort: a non-elevated parent generally cannot terminate an elevated
            // child, but we still try on cancellation.
            using var ctReg = ct.Register(() => { try { elevatedProcess.Kill(); } catch { /* ignore */ } });

            var linesRead = 0;
            using (elevatedProcess)
            {
                // With privilege brokers (UAC's consent.exe, CyberArk EPM, etc.) the process
                // handle returned by "runas" is frequently a short-lived launcher, NOT the
                // real elevated worker — so HasExited is unreliable and can never fire (or
                // fires immediately) while the actual worker keeps running. Treat the worker's
                // result file (always written on success AND failure) as the authoritative
                // completion signal instead, tailing progress/log lines while we wait.
                while (!File.Exists(resultFile))
                {
                    await Task.Delay(300, ct);
                    linesRead = TailProgress(progressFile, linesRead);
                }
                linesRead = TailProgress(progressFile, linesRead);
            }

            if (!File.Exists(resultFile))
                throw new InvalidOperationException(
                    "The elevated boot image generation process exited without reporting a result.");

            var resultJson = await File.ReadAllTextAsync(resultFile, ct);
            var result = JsonSerializer.Deserialize<ElevatedGenerationResult>(resultJson)
                ?? throw new InvalidOperationException("Could not parse the elevated process result.");

            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Boot image generation failed in the elevated process.");

            return new GenerationResult(result.WimPath!, result.Sha256Hash!);
        }
        finally
        {
            try { Directory.Delete(ipcDir, recursive: true); } catch { /* best effort */ }
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
            // Typed IPC lines written by the elevated worker:
            //   "P\t{percent}\t{message}"  → progress/step update
            //   "L\t{text}"                → command/output log line
            if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length == 3 && int.TryParse(parts[1], out var percent))
                    ReportProgress(parts[2], percent);
            }
            else if (line.StartsWith("L\t", StringComparison.Ordinal))
            {
                RaiseLog(line[2..]);
            }
        }
        return lines.Length;
    }

    /// <summary>
    /// Entry point for the elevated worker process. Invoked from App.OnStartup when this
    /// executable is relaunched with <see cref="ElevatedWorkerArg"/> by
    /// <see cref="GenerateElevatedAsync"/>. Runs entirely non-interactively: reads generation
    /// parameters from <paramref name="paramsFile"/>, performs the actual generation (this
    /// process IS elevated), and writes progress/result to the given IPC files.
    /// </summary>
    public static async Task RunElevatedWorkerAsync(
        string paramsFile, string progressFile, string resultFile, ILoggerFactory loggerFactory)
    {
        try
        {
            var json = await File.ReadAllTextAsync(paramsFile);
            var p = JsonSerializer.Deserialize<ElevatedGenerationParams>(json)
                ?? throw new InvalidOperationException("Could not parse generation parameters.");

            byte[]? pfxBytes = p.PfxFilePath is not null ? await File.ReadAllBytesAsync(p.PfxFilePath) : null;

            var svc = new BootImageGenerationService(loggerFactory.CreateLogger<BootImageGenerationService>());

            // Stream both progress/step updates and the command/output log back to the parent
            // over the same append-only file, one typed line at a time (see TailProgress).
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
            svc.LogMessage      += (_, line) => Append($"L\t{line}");

            var result = await svc.GenerateAsync(p.ClientBinariesPath, pfxBytes, p.OutputDirectory, p.DriverRootPath);

            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedGenerationResult(true, result.WimPath, result.Sha256Hash, null)));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedGenerationResult(false, null, null, ex.Message)));
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static System.Diagnostics.Process StartElevatedProcess(string exePath, string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = exePath,
            Arguments       = args,
            UseShellExecute = true,
            Verb            = "runas",
        };
        return System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the elevated boot image generation process.");
    }

    /// <summary>
    /// Generates a WinPE boot image.
    /// When <paramref name="pfxBytes"/> is null and <see cref="_operatorApiClient"/> is set,
    /// the service fetches the active cert PFX from the Operator API (T173, FR-070).
    /// </summary>
    /// <param name="clientBinariesPath">Folder containing the Cloud Imaging Client binaries.</param>
    /// <param name="pfxBytes">PFX bytes to embed as <c>certificates\bootmedia.pfx</c> (FR-070).</param>
    /// <param name="outputDirectory">Directory where the generated WIM will be placed.</param>
    /// <param name="driverRootPath">
    /// Optional. Root folder of pre-staged driver packages. When provided, every
    /// <c>.inf</c> package beneath it is recursively injected into the WIM (FR-051c).
    /// When null/empty, no driver injection is performed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GenerationResult> GenerateAsync(
        string clientBinariesPath,
        byte[]? pfxBytes,
        string outputDirectory,
        string? driverRootPath = null,
        CancellationToken ct = default)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ci-bootimage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            ReportProgress("Verifying ADK installation…", 5);
            var adkPath = FindAdkPath();
            if (adkPath is null)
                throw new InvalidOperationException(
                    "Windows ADK with WinPE add-on is not installed. " +
                    "Download both from https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install");

            // Retrieve the active boot media certificate PFX from Operator API (T173, FR-070)
            if (pfxBytes is null && _operatorApiClient is not null)
            {
                ReportProgress("Retrieving active boot media certificate PFX…", 8);
                try
                {
                    pfxBytes = await _operatorApiClient.GetBootMediaCertPfxAsync(ct);
                    LogCertRetrieved(_logger);
                }
                catch (Exception ex)
                {
                    LogCertRetrieveFailed(_logger, ex);
                    // Non-fatal — generation continues without cert embedding
                }
            }

            ReportProgress("Copying WinPE base files…", 15);
            EnsureOscdimgBootFilesPresent(adkPath);
            var winPeRoot = Path.Combine(workDir, "WinPE");
            await CopyWinPeFilesAsync(adkPath, winPeRoot, ct);

            ReportProgress("Mounting WIM for customization…", 30);
            var mountDir = Path.Combine(workDir, "Mount");
            Directory.CreateDirectory(mountDir);
            var wimPath  = Path.Combine(winPeRoot, "media", "sources", "boot.wim");
            await RunDismAsync($"/Mount-Image /ImageFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\"", ct);
            var mounted = true;

            try
            {
                ReportProgress("Injecting Cloud Imaging Client…", 50);
                var clientDestDir = Path.Combine(mountDir, "CloudImaging");
                Directory.CreateDirectory(clientDestDir);
                CopyDirectory(clientBinariesPath, clientDestDir);

                // Resolve the live Device Gateway URL from Operator API and stamp it into the
                // Client's appsettings.json — every boot image build picks up the current URL,
                // regardless of what shipped in the source binaries (FR-062 config bootstrap).
                if (_operatorApiClient is not null)
                {
                    ReportProgress("Resolving Device Gateway endpoint…", 53);
                    try
                    {
                        var endpoints = await _operatorApiClient.GetEndpointConfigurationAsync(ct);
                        await StampDeviceGatewayBaseUrlAsync(clientDestDir, endpoints.DeviceGatewayApiBaseUrl, ct);
                        LogDeviceGatewayUrlStamped(_logger, endpoints.DeviceGatewayApiBaseUrl);
                    }
                    catch (Exception ex)
                    {
                        LogDeviceGatewayUrlStampFailed(_logger, ex);
                        // Non-fatal — generation continues with whatever BaseUrl shipped in the source binaries
                    }
                }

                // Embed boot media certificate PFX if provided (FR-070)
                if (pfxBytes is { Length: > 0 })
                {
                    var certDir = Path.Combine(clientDestDir, "certificates");
                    Directory.CreateDirectory(certDir);
                    var pfxDest = Path.Combine(certDir, "bootmedia.pfx");
                    await File.WriteAllBytesAsync(pfxDest, pfxBytes, ct);
                    ReportProgress("Boot media certificate embedded…", 60);
                }

                // Inject pre-staged storage/network drivers into the mounted WIM (FR-051c)
                await InjectDriversAsync(mountDir, driverRootPath, ct);

                ReportProgress("Unmounting and committing WIM…", 75);
                await RunDismAsync($"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", ct);
                mounted = false;
            }
            finally
            {
                // Whatever went wrong between /Mount-Image succeeding and /Unmount-Image
                // /Commit succeeding above (a thrown exception, driver injection failure,
                // even cancellation) — never leave the WIM mounted. Discard rather than
                // commit, since a partially-customized mount shouldn't be treated as good.
                if (mounted)
                    await TryDiscardMountAsync(mountDir, ct);
            }

            ReportProgress("Copying output WIM…", 88);
            Directory.CreateDirectory(outputDirectory);
            var outputWim  = Path.Combine(outputDirectory, "cloud-imaging-boot.wim");
            File.Copy(wimPath, outputWim, overwrite: true);

            ReportProgress("Computing SHA-256 hash…", 95);
            var hash = await ComputeSha256Async(outputWim, ct);

            ReportProgress("Boot image generated successfully.", 100);
            LogGenerated(_logger, outputWim, hash);

            return new GenerationResult(outputWim, hash);
        }
        finally
        {
            // Best-effort cleanup
            try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Best-effort rollback for a WIM left mounted by a failure between <c>/Mount-Image</c>
    /// and a successful <c>/Unmount-Image /Commit</c> (an exception, a driver-injection
    /// failure, or cancellation). Always attempts <c>/Unmount-Image /Discard</c> — even when
    /// <paramref name="ct"/> is already cancelled — so a cancelled run doesn't leave an
    /// orphaned DISM mount point behind (which otherwise requires a manual
    /// <c>dism /Cleanup-Mountpoints</c> to clear). Never throws: a rollback failure is logged
    /// and surfaced in the live log, but must not mask the original failure.
    /// </summary>
    private async Task TryDiscardMountAsync(string mountDir, CancellationToken ct)
    {
        RaiseLog($"Rolling back: unmounting and discarding \"{mountDir}\" after a failure…");
        try
        {
            await RunDismAsync($"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogUnmountRollbackFailed(_logger, mountDir, ex);
            RaiseLog(
                $"✗ Failed to unmount \"{mountDir}\" during rollback — the WIM may still be mounted. " +
                $"Run 'dism /Cleanup-Mountpoints' to clear orphaned mounts. {ex.Message}");
        }
    }

    // ── ADK discovery ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the Windows ADK (with the WinPE add-on) is installed on this
    /// workstation. Used by the OperationSelectionView to block the Generate Boot Image
    /// workflow up-front with installation guidance (FR-050a).
    /// </summary>
    public static bool IsAdkInstalled() => FindAdkPath() is not null;

    /// <summary>
    /// Locates an ADK install that also has the WinPE add-on present (FR-050a).
    ///
    /// The base ADK ("Deployment Tools" + "Common") and the WinPE add-on are SEPARATE
    /// installers (adksetup.exe vs. adkwinpesetup.exe) that both extract into the SAME
    /// root folder. It is entirely possible — and was reproduced on a real workstation —
    /// for only the base ADK to be installed, in which case the ADK root directory exists
    /// but "Windows Preinstallation Environment" (containing copype.cmd / MakeWinPEMedia.cmd)
    /// does not. Checking only the root directory is a false positive: it lets the user
    /// proceed into Generate Boot Image, where copype.cmd then fails with
    /// "The system cannot find the path specified." (exit code 1).
    ///
    /// Per FR-050a, detection MUST target copype.cmd and MakeWinPEMedia.cmd specifically.
    /// </summary>
    private static string? FindAdkPath()
    {
        // Standard ADK install location
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit",
        };

        return candidates.FirstOrDefault(root =>
        {
            var winPeDir = Path.Combine(root, "Windows Preinstallation Environment");
            return File.Exists(Path.Combine(winPeDir, "copype.cmd"))
                && File.Exists(Path.Combine(winPeDir, "MakeWinPEMedia.cmd"));
        });
    }

    /// <summary>
    /// Boot sector files that copype.cmd unconditionally copies from
    /// <c>Deployment Tools\{arch}\Oscdimg</c> while staging (etfsboot.com is copied only if
    /// present, so it is intentionally excluded here — see the copype.cmd source).
    /// </summary>
    private static readonly string[] RequiredOscdimgBootFiles =
    [
        "efisys.bin", "efisys_noprompt.bin", "efisys_EX.bin", "efisys_noprompt_EX.bin",
    ];

    /// <summary>
    /// Verifies the base ADK's "Deployment Tools" ships the boot sector files copype.cmd
    /// needs, BEFORE invoking copype.cmd (FR-050a).
    ///
    /// The base ADK ("Deployment Tools") and the WinPE add-on are installed/updated
    /// independently. When the WinPE add-on is newer than Deployment Tools (e.g. only the
    /// add-on was updated), the mounted WinPE WIM/copype.cmd script expect the newer
    /// "_EX" boot manager files (added for the 2023 Secure Boot signing update) that an
    /// older Deployment Tools "Oscdimg" folder does not yet contain. Left unchecked, this
    /// surfaces deep inside copype.cmd as a confusing
    /// "ERROR: Unable to copy boot sector file: ...efisys_EX.bin..." failure. Catching it
    /// here up front gives an actionable message instead.
    /// </summary>
    private void EnsureOscdimgBootFilesPresent(string adkPath)
    {
        var oscdimgDir = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "Oscdimg");
        var missing = RequiredOscdimgBootFiles
            .Where(f => !File.Exists(Path.Combine(oscdimgDir, f)))
            .ToArray();

        if (missing.Length == 0)
            return;

        RaiseLog($"✗ Deployment Tools \"{oscdimgDir}\" is missing: {string.Join(", ", missing)}");
        throw new InvalidOperationException(
            $"The installed Windows ADK Deployment Tools are missing boot files ({string.Join(", ", missing)}) " +
            $"required by the installed WinPE add-on. This happens when the base ADK (Deployment Tools) is an " +
            "older version than the WinPE add-on — they must be the SAME version. Re-run the ADK installer " +
            "(adksetup.exe) and update Deployment Tools to match the WinPE add-on version, then try again. " +
            "Download both (matching versions) from https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install");
    }

    private async Task CopyWinPeFilesAsync(string adkPath, string winPeRoot, CancellationToken ct)
    {
        var copype = Path.Combine(adkPath, "Windows Preinstallation Environment", "copype.cmd");

        // copype.cmd resolves its source media via %WinPERoot%\%arch%, validates firmware
        // files via %OSCDImgRoot%\..\..\%arch%\Oscdimg, and mounts the WIM via
        // "%DISMRoot%\Dism.exe". All three variables are normally set by the ADK's
        // "Deployment and Imaging Tools Environment" prompt (Deployment Tools\DandISetEnv.bat)
        // — which this app never launches from. Without them, copype.cmd fails first with
        // "ERROR: The following processor architecture was not found: amd64." and, once
        // WinPERoot/OSCDImgRoot are set, with "'"\Dism.exe"' is not recognized..." even though
        // the ADK/WinPE add-on are correctly installed. Set them explicitly so the invocation
        // is self-contained regardless of the calling environment.
        var env = new Dictionary<string, string>
        {
            ["WinPERoot"]   = Path.Combine(adkPath, "Windows Preinstallation Environment"),
            ["OSCDImgRoot"] = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "Oscdimg"),
            ["DISMRoot"]    = Path.Combine(adkPath, "Deployment Tools", WinPeArch, "DISM"),
        };

        // cmd.exe's /C switch only preserves quotes verbatim when the command tail contains
        // EXACTLY TWO quote characters. Both copype's own path and winPeRoot can contain
        // spaces (e.g. a redirected "Documents" folder under OneDrive), so this command tail
        // has four quotes and falls into cmd's legacy fallback: it strips only the very first
        // and very last quote character of the whole string, leaving the inner quotes
        // unbalanced and the executable name misparsed as "C:\Program" (from "Program Files").
        // Wrapping the entire /c argument in one extra outer pair of quotes survives that
        // strip-first-and-last-quote fallback and leaves the original quoting intact.
        await RunExternalAsync("cmd.exe", $"/c \"\"{copype}\" {WinPeArch} \"{winPeRoot}\"\"", ct, env);
    }

    private async Task RunDismAsync(string args, CancellationToken ct)
    {
        await RunExternalAsync("dism.exe", args, ct);
    }

    // ── Driver injection (FR-051c) ────────────────────────────────────────────

    /// <summary>
    /// Recursively injects every driver package (.inf) beneath <paramref name="driverRootPath"/>
    /// into the mounted WIM using DISM offline driver servicing. No-op when the path is
    /// null/empty. Throws when a non-empty path does not exist. Skips (with a warning) when
    /// the folder exists but contains no driver packages (FR-051c).
    /// </summary>
    private async Task InjectDriversAsync(string mountDir, string? driverRootPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(driverRootPath))
            return;

        if (!Directory.Exists(driverRootPath))
            throw new DirectoryNotFoundException(
                $"Driver root folder not found: {driverRootPath}");

        var infCount = Directory
            .EnumerateFiles(driverRootPath, "*.inf", SearchOption.AllDirectories)
            .Count();

        if (infCount == 0)
        {
            LogNoDriversFound(_logger, driverRootPath);
            ReportProgress("No driver packages (.inf) found in driver root — skipping driver injection.", 68);
            return;
        }

        ReportProgress($"Injecting {infCount} driver package(s) from driver root…", 70);
        await RunDismAsync(
            $"/Image:\"{mountDir}\" /Add-Driver /Driver:\"{driverRootPath}\" /Recurse /ForceUnsigned", ct);
        LogDriversInjected(_logger, infCount, driverRootPath);
    }

    private async Task RunExternalAsync(
        string exe, string args, CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.EnvironmentVariables[key] = value;
        }

        // Surface the exact command being executed in the live UI log (FR-051d).
        RaiseLog($"> {exe} {args}");

        using var process = new System.Diagnostics.Process
        {
            StartInfo           = startInfo,
            EnableRaisingEvents = true,
        };

        // Capture stdout/stderr so a failure can surface the tool's actual diagnostic
        // message instead of just an exit code (e.g. copype.cmd / dism.exe error text),
        // and stream every line to the live UI log as it arrives.
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); RaiseLog(e.Data); } };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); RaiseLog(e.Data); } };

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ct.Register(() => { try { process.Kill(); } catch { } });

        // DISM /Mount-Image and /Unmount-Image in particular can run for 30+ seconds with
        // NO stdout/stderr output at all — without this, the log/step UI looks stalled with
        // no way to tell a slow step from a hung one. Emit a periodic "still running" line
        // until the process exits.
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = ReportHeartbeatAsync(exe, heartbeatCts.Token);

        int code;
        try
        {
            code = await tcs.Task;
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { /* expected */ }
        }

        if (code != 0)
        {
            var captured = output.ToString().Trim();
            var detail   = captured.Length > 0 ? $" Output: {captured}" : string.Empty;
            RaiseLog($"✗ {System.IO.Path.GetFileName(exe)} exited with code {code}.");
            throw new InvalidOperationException($"{exe} exited with code {code}.{detail}");
        }

        RaiseLog($"✓ {System.IO.Path.GetFileName(exe)} completed (exit 0).");
    }

    /// <summary>
    /// Emits a "still running" log line every few seconds so long, silent external commands
    /// (notably DISM mount/unmount) don't look stalled in the live command/output log.
    /// </summary>
    private async Task ReportHeartbeatAsync(string exe, CancellationToken ct)
    {
        var elapsedSeconds = 0;
        var exeName = System.IO.Path.GetFileName(exe);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                elapsedSeconds += 5;
                RaiseLog($"… {exeName} still running ({elapsedSeconds}s elapsed)…");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the command completes — not an error.
        }
    }


    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    /// <summary>
    /// Patches <c>DeviceGatewayApi:BaseUrl</c> in the Client's <c>appsettings.json</c> (staged
    /// inside the mounted WIM) with the live URL resolved from Operator API. No-op when the
    /// URL is empty or the file isn't present (e.g. an unexpected Client binaries layout) —
    /// callers treat failures as non-fatal to generation. Public so it can be unit tested
    /// directly against a staging folder without needing ADK/DISM.
    /// </summary>
    public static async Task StampDeviceGatewayBaseUrlAsync(string clientDestDir, string? baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        var appSettingsPath = Path.Combine(clientDestDir, "appsettings.json");
        if (!File.Exists(appSettingsPath))
            return;

        var json = await File.ReadAllTextAsync(appSettingsPath, ct);
        var root = JsonNode.Parse(json)?.AsObject() ?? [];
        var deviceGatewaySection = root["DeviceGatewayApi"]?.AsObject() ?? [];
        deviceGatewaySection["BaseUrl"] = baseUrl;
        root["DeviceGatewayApi"] = deviceGatewaySection;

        await File.WriteAllTextAsync(
            appSettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private void ReportProgress(string message, int percent)
    {
        ProgressChanged?.Invoke(this, (message, percent));
        LogProgress(_logger, message, percent);
    }

    /// <summary>Raises <see cref="LogMessage"/> for a single command/output line and mirrors it to the logger.</summary>
    private void RaiseLog(string line)
    {
        LogMessage?.Invoke(this, line);
        LogCommandLine(_logger, line);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image generated: {OutputWim} (sha256={Hash}).")]
    private static partial void LogGenerated(ILogger logger, string outputWim, string hash);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[{Percent}%] {Message}")]
    private static partial void LogProgress(ILogger logger, string message, int percent);

    [LoggerMessage(Level = LogLevel.Debug, Message = "cmd> {Line}")]
    private static partial void LogCommandLine(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media certificate PFX retrieved for embedding.")]
    private static partial void LogCertRetrieved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot media cert retrieval failed — generation continues without cert.")]
    private static partial void LogCertRetrieveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device Gateway URL resolved from Operator API and stamped into Client appsettings.json: {BaseUrl}.")]
    private static partial void LogDeviceGatewayUrlStamped(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device Gateway URL resolution/stamping failed — generation continues with the Client's source appsettings.json.")]
    private static partial void LogDeviceGatewayUrlStampFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected {Count} driver package(s) from driver root {DriverRoot}.")]
    private static partial void LogDriversInjected(ILogger logger, int count, string driverRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No driver packages (.inf) found under driver root {DriverRoot} — skipping driver injection.")]
    private static partial void LogNoDriversFound(ILogger logger, string driverRoot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to unmount/discard \"{MountDir}\" during failure rollback — it may still be mounted.")]
    private static partial void LogUnmountRollbackFailed(ILogger logger, string mountDir, Exception ex);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(filePath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

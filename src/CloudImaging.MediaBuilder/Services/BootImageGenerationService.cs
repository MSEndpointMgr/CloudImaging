using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Prefix for the per-run working directory created under <c>%TEMP%</c> by
    /// <see cref="GenerateAsync"/> (e.g. <c>ci-bootimage-3f9a...\Mount</c>). Shared between the
    /// directory's creation and <see cref="CleanupOrphanedWorkDirsAsync"/> — which sweeps up any
    /// directories matching this prefix left behind by a previous run that was killed (crashed,
    /// Task Manager, power loss) before it could clean up after itself — so both stay in sync.
    /// </summary>
    private const string WorkDirPrefix = "ci-bootimage-";

    /// <summary>
    /// Prefix for the per-run IPC directory created under <c>%TEMP%</c> by
    /// <see cref="RunElevatedChildProcessAsync"/>. Shared with <see cref="CleanupOrphanedIpcDirs"/>
    /// for the same reason as <see cref="WorkDirPrefix"/>.
    /// </summary>
    private const string ElevatedIpcDirPrefix = "ci-elevated-";

    /// <summary>
    /// How long a single external command (dism.exe, copype.cmd) must run before the live log
    /// gets an actionable hint about the likely cause, instead of just a ticking heartbeat.
    /// DISM mount/unmount legitimately takes tens of seconds — this is set well above that so
    /// the hint only fires when a step is running unusually long.
    /// </summary>

    /// <summary>
    /// Conservative allowance for the WinPE media copype.cmd produces plus DISM mount
    /// scratch space, used by the free-space pre-flight check. Not exact — copype's actual
    /// output size varies by ADK version — but large enough to catch "clearly not enough
    /// room" before any copying starts.
    /// </summary>
    private const long EstimatedWinPeWorkspaceBytes = 2L * 1024 * 1024 * 1024; // 2 GB

    /// <summary>Hidden CLI switch used to relaunch this same executable elevated (see <see cref="GenerateElevatedAsync"/>).</summary>
    public const string ElevatedWorkerArg = "--elevated-generate";

    /// <summary>
    /// Matches dism.exe's own console progress-bar redraw (e.g.
    /// "[==============86.0%======================                ]"). On a real console DISM
    /// repaints this single line in place using a bare carriage return, but .NET's redirected
    /// StreamReader splits on any of \r, \n, or \r\n — so each redraw otherwise arrives as its
    /// own "line" and floods the live log with dozens of near-duplicate entries for one DISM
    /// call. Lines matching this are routed through <see cref="RaiseHeartbeat"/> instead of
    /// <see cref="RaiseLog"/> so the UI collates them into one steadily-updating line, matching
    /// how they actually behave in a real terminal.
    /// </summary>
    private static readonly Regex DismProgressBarLineRegex = new(@"^\[[=\s]*\d+(\.\d+)?%[=\s]*\]$", RegexOptions.Compiled);

    private readonly ILogger<BootImageGenerationService> _logger;
    private readonly OperatorApiClient? _operatorApiClient;
    private readonly BrandingLogoEmbedService? _brandingLogoEmbedService;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, string, System.Diagnostics.Process> _startElevatedProcess;

    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <summary>
    /// Raised for every command executed and every line of tool output, so the UI can show
    /// a live command/activity log (e.g. the copype.cmd and dism.exe invocations and their
    /// output). Independent of <see cref="ProgressChanged"/>, which drives the step/percent UI.
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Raised in place of <see cref="LogMessage"/> for a line that should replace the
    /// previously-displayed heartbeat text rather than append a new log line — currently only
    /// dism.exe's own collated progress-bar redraw (see <see cref="DismProgressBarLineRegex"/>).
    /// This keeps that single line ticking over in place instead of flooding the log with a new
    /// line per percentage tick.
    /// </summary>
    public event EventHandler<string>? LogHeartbeat;

    /// <param name="operatorApiClient">
    /// Optional. When provided, <see cref="GenerateElevatedAsync"/> will retrieve the active
    /// boot media certificate PFX from the Operator API before generation starts, and
    /// generation FAILS if none can be retrieved (T173, FR-070) — the produced boot media
    /// would otherwise be unable to authenticate to the Device Gateway API. When null,
    /// pfxBytes must be supplied by the caller or no certificate is embedded (test/dev seam).
    /// </param>
    /// <param name="brandingLogoEmbedService">
    /// Optional. When provided, <see cref="GenerateElevatedAsync"/> will retrieve the
    /// portal-configured branding logo before generation starts and embed it alongside the
    /// Client (FR-002a, FR-051). Unlike the certificate, a missing/failed logo is never fatal —
    /// the Client falls back to its default logo when none is embedded.
    /// </param>
    /// <param name="isElevatedOverride">Test seam. Defaults to a real check of the current process token.</param>
    /// <param name="startElevatedProcessOverride">Test seam. Defaults to a real "runas"-elevated <see cref="System.Diagnostics.Process"/> launch.</param>
    public BootImageGenerationService(
        ILogger<BootImageGenerationService> logger,
        OperatorApiClient? operatorApiClient = null,
        BrandingLogoEmbedService? brandingLogoEmbedService = null,
        Func<bool>? isElevatedOverride = null,
        Func<string, string, System.Diagnostics.Process>? startElevatedProcessOverride = null)
    {
        _logger                    = logger;
        _operatorApiClient         = operatorApiClient;
        _brandingLogoEmbedService  = brandingLogoEmbedService;
        _isElevated                = isElevatedOverride ?? IsElevated;
        _startElevatedProcess      = startElevatedProcessOverride ?? StartElevatedProcess;
    }

    /// <summary>
    /// Sets the Entra ID Bearer token used for the Operator API calls this service makes
    /// directly (boot media certificate retrieval, branding logo retrieval, Device Gateway
    /// endpoint resolution). No-op when no <see cref="OperatorApiClient"/> was provided to this
    /// instance. Callers should set this (from <see cref="EntraAuthenticationService"/>) before
    /// calling <see cref="GenerateElevatedAsync"/>.
    /// </summary>
    public void SetOperatorApiAccessToken(string token) => _operatorApiClient?.SetAccessToken(token);

    public sealed record GenerationResult(string WimPath, string Sha256Hash);

    private sealed record ElevatedGenerationParams(
        string ClientBinariesPath, string OutputDirectory, string? DriverRootPath, string? PfxFilePath, string? LogoFilePath);

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
        // Resolve the boot media certificate and branding logo HERE, in this process, BEFORE
        // branching on elevation. The elevated worker process (see RunElevatedWorkerAsync)
        // constructs its own BootImageGenerationService with no OperatorApiClient of its own —
        // if either of these were left for GenerateAsync to fetch lazily, the elevated-relaunch
        // path (the one actually used on every non-Administrator run) would silently never
        // retrieve them. The resolved bytes are threaded through to the elevated worker via the
        // same file-based IPC used for everything else (see RunElevatedChildProcessAsync).
        if (pfxBytes is null && _operatorApiClient is not null)
            pfxBytes = await ResolveBootMediaCertificateAsync(ct);

        byte[]? logoBytes = _brandingLogoEmbedService is not null
            ? await _brandingLogoEmbedService.TryDownloadLogoAsync(ct)
            : null;

        if (_isElevated())
            return await GenerateAsync(clientBinariesPath, pfxBytes, outputDirectory, driverRootPath, logoBytes, ct);

        return await RunElevatedChildProcessAsync(clientBinariesPath, pfxBytes, logoBytes, outputDirectory, driverRootPath, ct);
    }

    /// <summary>
    /// Retrieves the active boot media certificate PFX from the Operator API (T173, FR-070).
    /// Called from <see cref="GenerateElevatedAsync"/> — before any elevation relaunch — so the
    /// bytes can be threaded through IPC to the elevated worker. Boot image generation MUST NOT
    /// proceed without an active certificate: the produced media would be unable to complete the
    /// mTLS handshake with the Device Gateway API and would be non-functional, so a retrieval
    /// failure here (network error, no active certificate configured, empty payload) is a hard
    /// failure, not a warning.
    /// </summary>
    private async Task<byte[]> ResolveBootMediaCertificateAsync(CancellationToken ct)
    {
        ReportProgress("Retrieving boot media certificate", 8);
        try
        {
            var pfx = await _operatorApiClient!.GetBootMediaCertPfxAsync(ct);
            if (pfx is not { Length: > 0 })
                throw new InvalidOperationException("The Operator API returned an empty boot media certificate.");

            LogCertRetrieved(_logger);
            return pfx;
        }
        catch (Exception ex)
        {
            LogCertRetrieveFailed(_logger, ex);
            throw new InvalidOperationException(
                "No active boot media certificate is configured in the Cloud Imaging Portal. " +
                "Boot image generation cannot continue: the produced media would be unable to " +
                "authenticate to the Device Gateway API. Configure an active boot media certificate " +
                "in the Cloud Imaging Portal, then try again.", ex);
        }
    }

    private async Task<GenerationResult> RunElevatedChildProcessAsync(
        string clientBinariesPath, byte[]? pfxBytes, byte[]? logoBytes, string outputDirectory, string? driverRootPath, CancellationToken ct)
    {
        // A previous run's IPC folder is only ever left behind when this (non-elevated) parent
        // process itself was killed/crashed before its own finally block ran (the elevated
        // worker has its own, separate work-dir/mount cleanup — see CleanupOrphanedWorkDirsAsync).
        // These folders hold nothing but small IPC files (no DISM mount to worry about), so
        // sweeping them up needs no elevation and is always safe to do up front.
        CleanupOrphanedIpcDirs();

        var ipcDir = Path.Combine(Path.GetTempPath(), $"{ElevatedIpcDirPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDir);
        var paramsFile   = Path.Combine(ipcDir, "params.json");
        var progressFile = Path.Combine(ipcDir, "progress.txt");
        var resultFile   = Path.Combine(ipcDir, "result.json");
        var cancelFile   = Path.Combine(ipcDir, "cancel.flag");

        try
        {
            string? pfxFilePath = null;
            if (pfxBytes is { Length: > 0 })
            {
                pfxFilePath = Path.Combine(ipcDir, "cert.pfx");
                await File.WriteAllBytesAsync(pfxFilePath, pfxBytes, ct);
            }

            string? logoFilePath = null;
            if (logoBytes is { Length: > 0 })
            {
                logoFilePath = Path.Combine(ipcDir, "logo.png");
                await File.WriteAllBytesAsync(logoFilePath, logoBytes, ct);
            }

            var request = new ElevatedGenerationParams(clientBinariesPath, outputDirectory, driverRootPath, pfxFilePath, logoFilePath);
            await File.WriteAllTextAsync(paramsFile, JsonSerializer.Serialize(request), ct);
            File.WriteAllText(progressFile, string.Empty);

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");
            var args = $"{ElevatedWorkerArg} \"{paramsFile}\" \"{progressFile}\" \"{resultFile}\" \"{cancelFile}\"";

            // Kept short enough to always fit the fixed-width progress header on one line
            // (see GenerateBootImageView.xaml's ProgressMessage TextBlock) — the full detail
            // about why elevation is needed still reaches the user via the elevation-request
            // UAC prompt itself, so it doesn't need to be repeated here.
            ReportProgress("Requesting Administrator privileges", 3);

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

            // Cancellation is signalled to the elevated worker via an IPC file rather than
            // killing the process outright: a hard Kill() mid-DISM-mount would abandon the
            // mounted WIM (and the worker's own temp working directory) with no chance to run
            // its existing mount-rollback/cleanup logic (see TryDiscardMountAsync and the
            // GenerateAsync outer finally). Writing the cancel flag lets the worker observe it
            // cooperatively (see RunElevatedWorkerAsync) and clean up exactly as it would for
            // any other failure, then exit normally with a result file.
            using var ctReg = ct.Register(() =>
            {
                try { File.WriteAllText(cancelFile, string.Empty); } catch { /* best effort */ }
            });

            var linesRead = 0;
            DateTime? cancelSignalledAt = null;
            using (elevatedProcess)
            {
                // With privilege brokers (UAC's consent.exe, CyberArk EPM, etc.) the process
                // handle returned by "runas" is frequently a short-lived launcher, NOT the
                // real elevated worker — so HasExited is unreliable and can never fire (or
                // fires immediately) while the actual worker keeps running. Treat the worker's
                // result file (always written on success AND failure) as the authoritative
                // completion signal instead, tailing progress/log lines while we wait. This
                // wait is intentionally NOT cancelled by "ct" itself (that would abort the wait
                // before the worker has a chance to clean up) — cancellation is instead handled
                // above via the cancel-flag file, with a grace period below before a hard kill.
                while (!File.Exists(resultFile))
                {
                    await Task.Delay(300, CancellationToken.None);
                    linesRead = TailProgress(progressFile, linesRead);

                    if (ct.IsCancellationRequested)
                    {
                        cancelSignalledAt ??= DateTime.UtcNow;
                        // Give the worker a grace period to unmount/discard the WIM and delete
                        // its temp folder before falling back to a hard kill (e.g. the worker
                        // is hung rather than merely slow to unmount).
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
                    throw new OperationCanceledException("Boot image generation was cancelled.", ct);
                throw new InvalidOperationException(
                    "The elevated boot image generation process exited without reporting a result.");
            }

            var resultJson = await File.ReadAllTextAsync(resultFile, CancellationToken.None);
            var result = JsonSerializer.Deserialize<ElevatedGenerationResult>(resultJson)
                ?? throw new InvalidOperationException("Could not parse the elevated process result.");

            if (!result.Success)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(result.Error ?? "Boot image generation was cancelled.", ct);
                throw new InvalidOperationException(result.Error ?? "Boot image generation failed in the elevated process.");
            }

            return new GenerationResult(result.WimPath!, result.Sha256Hash!);
        }
        finally
        {
            TryDeleteDirectoryRecursive(ipcDir);
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
            //   "L\t{text}"                → command/output log line (always append)
            //   "H\t{text}"                → heartbeat tick (replace previous heartbeat line)
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
            else if (line.StartsWith("H\t", StringComparison.Ordinal))
            {
                RaiseHeartbeat(line[2..]);
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
    ///
    /// Watches <paramref name="cancelFile"/> for the duration of generation: when the parent
    /// (non-elevated) process signals cancellation by creating that file (see
    /// <see cref="RunElevatedChildProcessAsync"/>), a local <see cref="CancellationTokenSource"/>
    /// is cancelled so <see cref="GenerateAsync"/> unwinds through its normal mount-rollback and
    /// temp-directory cleanup — exactly as it would for any other failure — instead of the
    /// process being killed outright.
    /// </summary>
    public static async Task RunElevatedWorkerAsync(
        string paramsFile, string progressFile, string resultFile, string cancelFile, ILoggerFactory loggerFactory)
    {
        using var cts = new CancellationTokenSource();
        var cancelWatcherTask = WatchForCancelSignalAsync(cancelFile, cts);

        try
        {
            var json = await File.ReadAllTextAsync(paramsFile);
            var p = JsonSerializer.Deserialize<ElevatedGenerationParams>(json)
                ?? throw new InvalidOperationException("Could not parse generation parameters.");

            byte[]? pfxBytes = p.PfxFilePath is not null ? await File.ReadAllBytesAsync(p.PfxFilePath) : null;
            byte[]? logoBytes = p.LogoFilePath is not null ? await File.ReadAllBytesAsync(p.LogoFilePath) : null;

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
            svc.LogHeartbeat    += (_, line) => Append($"H\t{line}");

            var result = await svc.GenerateAsync(p.ClientBinariesPath, pfxBytes, p.OutputDirectory, p.DriverRootPath, logoBytes, cts.Token);

            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedGenerationResult(true, result.WimPath, result.Sha256Hash, null)));
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedGenerationResult(false, null, null, "Boot image generation was cancelled.")));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultFile,
                JsonSerializer.Serialize(new ElevatedGenerationResult(false, null, null, ex.Message)));
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
    /// Generates a WinPE boot image. <paramref name="pfxBytes"/> and <paramref name="logoBytes"/>
    /// must already be resolved by the caller (see <see cref="GenerateElevatedAsync"/>, which
    /// resolves both from the Operator API before elevation) — this method only embeds whatever
    /// it is given and performs no Operator API calls of its own.
    /// </summary>
    /// <param name="clientBinariesPath">Folder containing the Cloud Imaging Client binaries.</param>
    /// <param name="pfxBytes">PFX bytes to embed as <c>certificates\bootmedia.pfx</c> (FR-070).</param>
    /// <param name="outputDirectory">Directory where the generated WIM will be placed.</param>
    /// <param name="driverRootPath">
    /// Optional. Root folder of pre-staged driver packages. When provided, every
    /// <c>.inf</c> package beneath it is recursively injected into the WIM (FR-051c).
    /// When null/empty, no driver injection is performed.
    /// </param>
    /// <param name="logoBytes">Branding logo bytes to embed at <c>branding\logo.png</c> (FR-002a). Optional — a missing logo is never fatal.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GenerationResult> GenerateAsync(
        string clientBinariesPath,
        byte[]? pfxBytes,
        string outputDirectory,
        string? driverRootPath = null,
        byte[]? logoBytes = null,
        CancellationToken ct = default)
    {
        // Sweep up whatever a previous run left behind if the process was killed/crashed
        // mid-generation (Task Manager, power loss, etc.) rather than exiting normally through
        // this method's own "finally" below — including discarding any DISM mount still left
        // mounted from that run. This method always executes elevated (either because the
        // whole process is already elevated, or because it IS the elevated worker relaunched by
        // GenerateElevatedAsync), so it's always safe to run the DISM unmount this needs. Must
        // run BEFORE the new workDir below is created, so it never sweeps up its own folder.
        await CleanupOrphanedWorkDirsAsync(ct);

        var workDir = Path.Combine(Path.GetTempPath(), $"{WorkDirPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        // Computed up front (rather than after the WIM is finalized) so the same value can be
        // embedded in the manifest as BootImageManifest.ImageVersion and reused for the output
        // file name below — the two are intentionally the same identifier.
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        try
        {
            ReportProgress("Verifying ADK installation", 5);
            var adkPath = FindAdkPath();
            if (adkPath is null)
                throw new InvalidOperationException(
                    "Windows ADK with WinPE add-on is not installed. " +
                    "Download both from https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install");

            // The boot media certificate is resolved by GenerateElevatedAsync before this method
            // runs (possibly in a separate elevated process) — see ResolveBootMediaCertificateAsync.

            ReportProgress("Copying WinPE base files", 15);
            EnsureOscdimgBootFilesPresent(adkPath);

            // Free-space pre-flight (inspired by OSDLiteDeploy's Get-FreeSpace guard): fail
            // fast with a clear message instead of partway through copype.cmd/DISM with a
            // confusing "disk full" I/O error. EstimatedWinPeWorkspaceBytes is a conservative
            // allowance for the WinPE media copype produces plus DISM mount scratch space;
            // the client binaries size is known exactly up front.
            var requiredWorkspaceBytes = EstimatedWinPeWorkspaceBytes + DiskSpaceGuard.DirectorySizeBytes(clientBinariesPath);
            DiskSpaceGuard.EnsureFreeSpace(workDir, requiredWorkspaceBytes, "build the WinPE boot image");

            var winPeRoot = Path.Combine(workDir, "WinPE");
            await CopyWinPeFilesAsync(adkPath, winPeRoot, ct);

            ReportProgress("Mounting WIM for customization", 30);
            var mountDir = Path.Combine(workDir, "Mount");
            Directory.CreateDirectory(mountDir);
            var wimPath  = Path.Combine(winPeRoot, "media", "sources", "boot.wim");
            await RunDismAsync($"/Mount-Image /ImageFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\"", ct);
            var mounted = true;

            try
            {
                ReportProgress("Injecting Cloud Imaging Client", 50);
                var clientDestDir = Path.Combine(mountDir, "CloudImaging");
                Directory.CreateDirectory(clientDestDir);
                CopyDirectory(clientBinariesPath, clientDestDir, ct);

                // Embed the portal-configured branding logo, if one was resolved — otherwise the
                // Client falls back to its default logo at runtime (FR-002a).
                if (logoBytes is { Length: > 0 })
                {
                    var logoDest = Path.Combine(clientDestDir, BrandingLogoEmbedService.LogoRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(logoDest)!);
                    await File.WriteAllBytesAsync(logoDest, logoBytes, ct);
                    LogBrandingLogoEmbedded(_logger, logoBytes.Length);
                }

                // Resolve the live Device Gateway URL from Operator API and stamp it into the
                // Client's appsettings.json — every boot image build picks up the current URL,
                // regardless of what shipped in the source binaries (FR-062 config bootstrap).
                if (_operatorApiClient is not null)
                {
                    ReportProgress("Resolving Device Gateway endpoint", 53);
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
                    ReportProgress("Boot media certificate embedded", 60);
                }

                // Inject pre-staged storage/network drivers into the mounted WIM (FR-051c)
                var driversInjected = await InjectDriversAsync(mountDir, driverRootPath, ct);

                ReportProgress("Configuring WinPE auto-start", 63);
                await ConfigureWinPeAutoStartAsync(mountDir, ct);

                // Embed the integrity/provenance manifest (T065, FR-051) — a descoped, honest
                // replacement for the old "signed manifest" claim; see BootImageManifest's remarks.
                ReportProgress("Embedding boot image manifest", 64);
                var manifest = BootImageManifestService.Build(
                    clientDestDir, timestamp, driversInjected, driverRootPath, logoBytes, pfxBytes);
                await BootImageManifestService.EmbedAsync(mountDir, manifest, ct);
                LogManifestEmbedded(_logger, timestamp, driversInjected);

                ReportProgress("Unmounting and committing WIM", 75);
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

            ReportProgress("Copying output WIM", 88);
            Directory.CreateDirectory(outputDirectory);

            // Never overwrite a previous run's WIM: each generation gets its own timestamped
            // filename (the same timestamp embedded as BootImageManifest.ImageVersion above), so
            // an earlier successful build in the same output folder is always still there
            // afterwards rather than silently replaced.
            var outputWim  = Path.Combine(outputDirectory, $"cloud-imaging-boot-{timestamp}.wim");
            DiskSpaceGuard.EnsureFreeSpace(outputDirectory, new FileInfo(wimPath).Length, "copy the finished boot image to the output folder");
            File.Copy(wimPath, outputWim, overwrite: false);

            ReportProgress("Computing SHA-256 hash", 95);
            var hash = await ComputeSha256Async(outputWim, ct);

            ReportProgress("Boot image generated successfully.", 100);
            LogGenerated(_logger, outputWim, hash);

            return new GenerationResult(outputWim, hash);
        }
        finally
        {
            // Best-effort cleanup
            TryDeleteDirectoryRecursive(workDir);
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
        RaiseLog($"Rolling back: unmounting and discarding \"{mountDir}\" after a failure.");
        try
        {
            await RunDismAsync($"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogUnmountRollbackFailed(_logger, mountDir, ex);
            RaiseLog(
                $"FAILED: could not unmount \"{mountDir}\" during rollback — the WIM may still be mounted. " +
                $"Run 'dism /Cleanup-Mountpoints' to clear orphaned mounts. {ex.Message}");
        }
    }

    /// <summary>
    /// Sweeps up leftover <c>%TEMP%\ci-bootimage-*</c> working folders from a previous run of
    /// this app that never reached its own <see cref="GenerateAsync"/> "finally" cleanup —
    /// most commonly because the process was killed (Task Manager, a crash, a forced shutdown)
    /// while a WIM was still mounted mid-generation. Left alone, that folder's <c>Mount</c>
    /// subdirectory holds a DISM mount point that stays locked forever (surviving even a reboot,
    /// per how DISM mount points work), which blocks deleting the folder and, if left long
    /// enough, can exhaust the handful of concurrent mount points DISM allows on the machine.
    ///
    /// For each leftover folder found: discards its <c>Mount</c> subdirectory (if present) via
    /// <c>/Unmount-Image /Discard</c> — harmless (and swallowed) if that mount already isn't
    /// live, e.g. the process died before or after actually mounting anything — then deletes the
    /// whole folder. Always best-effort: called at the start of every new generation, so it must
    /// never throw or block the run it's trying to make room for.
    /// </summary>
    private async Task CleanupOrphanedWorkDirsAsync(CancellationToken ct)
    {
        string[] orphaned;
        try
        {
            orphaned = Directory.GetDirectories(Path.GetTempPath(), $"{WorkDirPrefix}*");
        }
        catch (Exception ex)
        {
            LogOrphanedWorkDirScanFailed(_logger, ex);
            return;
        }

        if (orphaned.Length == 0)
            return;

        ReportProgress(
            $"Cleaning up {orphaned.Length} leftover working folder(s) from a previous run", 2);

        foreach (var dir in orphaned)
        {
            var mountDir = Path.Combine(dir, "Mount");
            if (Directory.Exists(mountDir))
            {
                RaiseLog($"Discarding a mounted image left over from a previous run: \"{mountDir}\".");
                try
                {
                    await RunDismAsync($"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Expected, not just tolerated, whenever the leftover folder's Mount
                    // subdirectory exists but isn't (or is no longer) a live mount point — e.g.
                    // the previous process was killed before mounting, or after already
                    // committing/discarding it — so this is logged at a low severity rather
                    // than surfaced as a failure in the live log.
                    LogOrphanedMountCleanupFailed(_logger, mountDir, ex);
                }
            }

            TryDeleteDirectoryRecursive(dir);
        }
    }

    /// <summary>
    /// Sweeps up leftover <c>%TEMP%\ci-elevated-*</c> IPC folders from a previous (non-elevated)
    /// parent process that was itself killed/crashed before <see cref="RunElevatedChildProcessAsync"/>'s
    /// own "finally" ran. These folders hold nothing but small IPC files (params/progress/result/
    /// cancel-flag, optionally a cert.pfx) — no DISM mount is ever created directly under one, so
    /// unlike <see cref="CleanupOrphanedWorkDirsAsync"/> this needs no elevation and is always
    /// safe to run up front. Best-effort: must never block starting a new elevated run.
    /// </summary>
    private void CleanupOrphanedIpcDirs()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), $"{ElevatedIpcDirPrefix}*"))
                TryDeleteDirectoryRecursive(dir);
        }
        catch (Exception ex)
        {
            LogOrphanedIpcDirScanFailed(_logger, ex);
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

        RaiseLog($"FAILED: Deployment Tools \"{oscdimgDir}\" is missing: {string.Join(", ", missing)}");
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
    /// <summary>Returns the number of driver packages (.inf) injected, or 0 when skipped/not requested.</summary>
    private async Task<int> InjectDriversAsync(string mountDir, string? driverRootPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(driverRootPath))
            return 0;

        if (!Directory.Exists(driverRootPath))
            throw new DirectoryNotFoundException(
                $"Driver root folder not found: {driverRootPath}");

        var infCount = Directory
            .EnumerateFiles(driverRootPath, "*.inf", SearchOption.AllDirectories)
            .Count();

        if (infCount == 0)
        {
            LogNoDriversFound(_logger, driverRootPath);
            ReportProgress("No driver packages found — skipping injection", 68);
            return 0;
        }

        ReportProgress($"Injecting {infCount} driver package(s)", 70);
        await RunDismAsync(
            $"/Image:\"{mountDir}\" /Add-Driver /Driver:\"{driverRootPath}\" /Recurse /ForceUnsigned", ct);
        LogDriversInjected(_logger, infCount, driverRootPath);
        return infCount;
    }

    /// <summary>
    /// Configures the mounted WIM so the Cloud Imaging Client launches automatically at WinPE
    /// startup with no visible console window (FR-051 auto-start).
    ///
    /// WinPE's shell launcher (winpeshl.exe) looks for <c>Windows\System32\winpeshl.ini</c> at
    /// boot; when present, it launches exactly the apps listed there instead of its built-in
    /// fallback of <c>cmd.exe /c startnet.cmd</c> — which is what shows the default black
    /// console window the user sees today. Listing the Client directly (after
    /// <c>wpeinit.exe</c>, which performs the PnP/network initialization the Client depends
    /// on) means the console never appears at all — more reliable than trying to minimize it
    /// after the fact, since WinPE has no taskbar to restore a minimized window from, and
    /// self-minimizing a running console from within a batch script would require PowerShell
    /// or a scripting host that isn't guaranteed to be present in a minimal WinPE image.
    ///
    /// <c>startnet.cmd</c> is also updated as a fallback for anyone who deletes
    /// winpeshl.ini — winpeshl.exe ignores it whenever winpeshl.ini exists.
    /// </summary>
    private static async Task ConfigureWinPeAutoStartAsync(string mountDir, CancellationToken ct)
    {
        const string clientPath = @"X:\CloudImaging\CloudImaging.Client.exe";
        var system32Dir = Path.Combine(mountDir, "Windows", "System32");
        Directory.CreateDirectory(system32Dir);

        await File.WriteAllTextAsync(
            Path.Combine(system32Dir, "winpeshl.ini"),
            $"[LaunchApps]\r\nwpeinit.exe\r\n{clientPath}\r\n",
            ct);

        await File.WriteAllTextAsync(
            Path.Combine(system32Dir, "startnet.cmd"),
            $"@echo off\r\nwpeinit.exe\r\n{clientPath}\r\n",
            ct);
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
        RaiseLog($"{exe} {args}");

        using var process = new System.Diagnostics.Process
        {
            StartInfo           = startInfo,
            EnableRaisingEvents = true,
        };

        // Capture stdout/stderr so a failure can surface the tool's actual diagnostic
        // message instead of just an exit code (e.g. copype.cmd / dism.exe error text),
        // and stream every line to the live UI log as it arrives, untagged — the command line
        // just logged above already identifies which tool this output belongs to, and the whole
        // point is for the log to read as one continuous activity stream rather than a set of
        // labelled sub-logs. dism.exe's own progress-bar redraws are collated into a single
        // in-place heartbeat line rather than appended as separate log lines (see
        // DismProgressBarLineRegex), so the log isn't flooded with a new line per percentage tick.
        var output = new System.Text.StringBuilder();
        void HandleOutputLine(string data)
        {
            output.AppendLine(data);

            // Trim rather than pass the raw line through as-is: external tools (notably
            // copype.cmd) mix indentation levels in their own console output (e.g. an indented
            // working-directory path following an unindented status line) which reads as
            // inconsistent once every line shares one flush-left log column.
            var trimmed = data.Trim();
            if (DismProgressBarLineRegex.IsMatch(trimmed))
                RaiseHeartbeat(trimmed);
            else
                RaiseLog(trimmed);
        }
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) HandleOutputLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) HandleOutputLine(e.Data); };

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ct.Register(() => { try { process.Kill(); } catch { } });

        var code = await tcs.Task;

        // The ct.Register above kills the process on cancellation, which makes it exit with a
        // non-zero code — surface that as a clean cancellation rather than a confusing generic
        // "exited with code -1" failure (callers, e.g. GenerateAsync's mount-rollback finally,
        // still run their normal cleanup either way, since OperationCanceledException propagates
        // like any other exception).
        if (ct.IsCancellationRequested)
        {
            RaiseLog($"{System.IO.Path.GetFileName(exe)} cancelled.");
            throw new OperationCanceledException($"{System.IO.Path.GetFileName(exe)} was cancelled.", ct);
        }

        if (code != 0)
        {
            var captured = output.ToString().Trim();
            var detail   = captured.Length > 0 ? $" Output: {captured}" : string.Empty;
            RaiseLog($"FAILED: {System.IO.Path.GetFileName(exe)} exited with code {code}.");
            throw new InvalidOperationException($"{exe} exited with code {code}.{detail}");
        }

        RaiseLog($"OK: {System.IO.Path.GetFileName(exe)} completed (exit 0).");
    }

    /// <summary>
    /// Deletes a directory tree even when it contains read-only files — which copype.cmd
    /// leaves behind when it copies the ADK's own read-only WinPE media source files.
    /// <see cref="Directory.Delete(string, bool)"/> throws <see cref="UnauthorizedAccessException"/>
    /// on a read-only file regardless of process privilege (elevation does not bypass the
    /// attribute; it must be cleared first) — left unhandled, this silently aborts cleanup and
    /// leaves the entire WinPE working directory (several hundred MB) behind in %TEMP% on every
    /// run. Best-effort throughout: a cleanup failure must never mask the real generation
    /// result/error.
    /// </summary>
    private static void TryDeleteDirectoryRecursive(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch { /* best effort — Directory.Delete below will surface anything that still blocks removal */ }
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort cleanup — never let this mask the real generation result/error */ }
    }


    private static void CopyDirectory(string source, string dest, CancellationToken ct)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            // Respond to cancellation between files rather than only at the next await point —
            // this copy has no other await inside its loop and can otherwise run for a while
            // uninterrupted when the Client binaries folder is large.
            ct.ThrowIfCancellationRequested();

            // Debug symbols are never needed to run the Client and can be a large fraction
            // of a build's total size — skipping them reduces how much data DISM has to
            // recompress into the WIM on /Unmount-Image /Commit. This is also why unmounting
            // legitimately takes noticeably longer than mounting: mounting just exposes the
            // existing compressed data, while committing has to compress every added/changed
            // file (the more/bigger the injected Client binaries and drivers, the longer it
            // takes) — it isn't a sign that anything has stalled.
            if (string.Equals(Path.GetExtension(file), ".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
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

    /// <summary>Raises <see cref="LogHeartbeat"/> for a tick that should replace the previous heartbeat line.</summary>
    private void RaiseHeartbeat(string line)
    {
        LogHeartbeat?.Invoke(this, line);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot media certificate retrieval failed — generation cannot continue.")]
    private static partial void LogCertRetrieveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding logo embedded ({Bytes} bytes).")]
    private static partial void LogBrandingLogoEmbedded(ILogger logger, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device Gateway URL resolved from Operator API and stamped into Client appsettings.json: {BaseUrl}.")]
    private static partial void LogDeviceGatewayUrlStamped(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device Gateway URL resolution/stamping failed — generation continues with the Client's source appsettings.json.")]
    private static partial void LogDeviceGatewayUrlStampFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected {Count} driver package(s) from driver root {DriverRoot}.")]
    private static partial void LogDriversInjected(ILogger logger, int count, string driverRoot);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image manifest embedded (imageVersion={ImageVersion}, driversInjected={DriversInjected}).")]
    private static partial void LogManifestEmbedded(ILogger logger, string imageVersion, int driversInjected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No driver packages (.inf) found under driver root {DriverRoot} — skipping driver injection.")]
    private static partial void LogNoDriversFound(ILogger logger, string driverRoot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to unmount/discard \"{MountDir}\" during failure rollback — it may still be mounted.")]
    private static partial void LogUnmountRollbackFailed(ILogger logger, string mountDir, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not enumerate %TEMP% for leftover boot-image working folders from a previous run.")]
    private static partial void LogOrphanedWorkDirScanFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Leftover mount \"{MountDir}\" from a previous run was not a live DISM mount point (already unmounted, or never mounted) — nothing to discard.")]
    private static partial void LogOrphanedMountCleanupFailed(ILogger logger, string mountDir, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not enumerate %TEMP% for leftover elevated-generation IPC folders from a previous run.")]
    private static partial void LogOrphanedIpcDirScanFailed(ILogger logger, Exception ex);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(filePath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

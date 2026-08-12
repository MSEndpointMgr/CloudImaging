using System.IO;
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

    private readonly ILogger<BootImageGenerationService> _logger;
    private readonly OperatorApiClient? _operatorApiClient;

    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    /// <param name="operatorApiClient">
    /// Optional. When provided, the service will attempt to retrieve the active boot
    /// media certificate PFX from the Operator API and embed it in the WIM (T173, FR-070).
    /// When null, pfxBytes must be supplied by the caller or cert embedding is skipped.
    /// </param>
    public BootImageGenerationService(
        ILogger<BootImageGenerationService> logger,
        OperatorApiClient? operatorApiClient = null)
    {
        _logger             = logger;
        _operatorApiClient  = operatorApiClient;
    }

    public sealed record GenerationResult(string WimPath, string Sha256Hash);

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
                    "Download it from https://go.microsoft.com/fwlink/?linkid=2243390");

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
            var winPeRoot = Path.Combine(workDir, "WinPE");
            await CopyWinPeFilesAsync(adkPath, winPeRoot, ct);

            ReportProgress("Mounting WIM for customization…", 30);
            var mountDir = Path.Combine(workDir, "Mount");
            Directory.CreateDirectory(mountDir);
            var wimPath  = Path.Combine(winPeRoot, "media", "sources", "boot.wim");
            await RunDismAsync($"/Mount-Image /ImageFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\"", ct);

            ReportProgress("Injecting Cloud Imaging Client…", 50);
            var clientDestDir = Path.Combine(mountDir, "CloudImaging");
            Directory.CreateDirectory(clientDestDir);
            CopyDirectory(clientBinariesPath, clientDestDir);

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

    // ── ADK discovery ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the Windows ADK (with the WinPE add-on) is installed on this
    /// workstation. Used by the OperationSelectionView to block the Generate Boot Image
    /// workflow up-front with installation guidance (FR-050a).
    /// </summary>
    public static bool IsAdkInstalled() => FindAdkPath() is not null;

    private static string? FindAdkPath()
    {
        // Standard ADK install location
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit",
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static async Task CopyWinPeFilesAsync(string adkPath, string winPeRoot, CancellationToken ct)
    {
        var copype = Path.Combine(adkPath, "Windows Preinstallation Environment", "copype.cmd");
        await RunExternalAsync("cmd.exe", $"/c \"{copype}\" {WinPeArch} \"{winPeRoot}\"", ct);
    }

    private static async Task RunDismAsync(string args, CancellationToken ct)
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

    private static async Task RunExternalAsync(string exe, string args, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
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
        int code = await tcs.Task;
        if (code != 0)
            throw new InvalidOperationException($"{exe} exited with code {code}.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    private void ReportProgress(string message, int percent)
    {
        ProgressChanged?.Invoke(this, (message, percent));
        LogProgress(_logger, message, percent);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image generated: {OutputWim} (sha256={Hash}).")]
    private static partial void LogGenerated(ILogger logger, string outputWim, string hash);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[{Percent}%] {Message}")]
    private static partial void LogProgress(ILogger logger, string message, int percent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media certificate PFX retrieved for embedding.")]
    private static partial void LogCertRetrieved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot media cert retrieval failed — generation continues without cert.")]
    private static partial void LogCertRetrieveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected {Count} driver package(s) from driver root {DriverRoot}.")]
    private static partial void LogDriversInjected(ILogger logger, int count, string driverRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No driver packages (.inf) found under driver root {DriverRoot} — skipping driver injection.")]
    private static partial void LogNoDriversFound(ILogger logger, string driverRoot);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(filePath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

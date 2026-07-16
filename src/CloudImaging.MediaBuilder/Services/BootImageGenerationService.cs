using System.IO;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Orchestrates the WinPE boot image generation workflow (T064, FR-051, FR-067, FR-070).
///
/// Workflow steps:
///   1. Verify ADK / WinPE add-on is installed.
///   2. Locate or download Cloud Imaging Client binaries.
///   3. Copy WinPE base files to working directory.
///   4. Mount WIM, inject Client binaries + cert + branding.
///   5. Unmount and commit WIM.
///   6. Output WIM to the specified output directory.
///
/// This service calls OS-level tools (DISM, makewinpemedia) — it is designed to run
/// on Windows with the ADK installed.  Unit tests mock the tool invocation.
/// </summary>
public sealed partial class BootImageGenerationService
{
    private const string WinPeArch = "amd64";

    private readonly ILogger<BootImageGenerationService> _logger;

    /// <summary>Progress callback — receives a message and 0–100 percent.</summary>
    public event EventHandler<(string Message, int Percent)>? ProgressChanged;

    public BootImageGenerationService(ILogger<BootImageGenerationService> logger)
        => _logger = logger;

    /// <summary>
    /// Generation result: output WIM path and computed SHA-256 hash.
    /// </summary>
    public sealed record GenerationResult(string WimPath, string Sha256Hash);

    /// <summary>
    /// Generates a WinPE boot image.
    /// </summary>
    /// <param name="clientBinariesPath">Folder containing the Cloud Imaging Client binaries.</param>
    /// <param name="pfxBytes">PFX bytes to embed as <c>certificates\bootmedia.pfx</c> (FR-070).</param>
    /// <param name="outputDirectory">Directory where the generated WIM will be placed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GenerationResult> GenerateAsync(
        string clientBinariesPath,
        byte[]? pfxBytes,
        string outputDirectory,
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

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(filePath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

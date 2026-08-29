using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Applies the Windows Recovery Environment (WinRE) image to the device — the
/// "ApplyRecoveryImage" step of the imaging pipeline, run after boot configuration.
///
/// Unlike <see cref="ImageApplyService"/> (which uses DISM to apply the full OS image), the
/// recovery image is simply copied into place and registered with the Windows Recovery
/// Environment agent (<c>reagentc.exe</c>) — WinRE images are not applied via DISM.
///
/// <see cref="ApplyAsync"/> applies a Recovery Image downloaded from the Portal catalog (the
/// preferred path — this is how admins deploy a custom WinRE with injected drivers or tools).
/// <see cref="ApplyFromEmbeddedImageAsync"/> is the fallback used when no Recovery Image has
/// been published: it reuses the <c>Winre.wim</c> the OS image already carries under
/// <c>Windows\System32\Recovery</c> instead of failing the session outright.
/// </summary>
public sealed partial class RecoveryImageService
{
    private readonly ILogger<RecoveryImageService> _logger;

    public RecoveryImageService(ILogger<RecoveryImageService> logger) => _logger = logger;

    /// <summary>
    /// Verifies the SHA-256 hash of the downloaded WinRE image, copies it to both the
    /// conventional location under the Windows volume (<c>Windows\System32\Recovery\Winre.wim</c>)
    /// and the dedicated Recovery partition, then registers and enables it via
    /// <c>reagentc.exe</c>. Finally removes the Recovery partition's temporary drive letter
    /// (assigned by <see cref="DiskFormatService"/> only so this step could write to it) —
    /// best-effort, since a leftover letter is cosmetic and must never fail the pipeline.
    /// </summary>
    /// <param name="recoveryWimPath">Local path of the downloaded WinRE image.</param>
    /// <param name="expectedHash">Expected SHA-256 hash (hex string, case-insensitive).</param>
    /// <param name="windowsVolume">Drive letter of the applied Windows volume (e.g. "C:").</param>
    /// <param name="recoveryVolume">Drive letter of the Recovery partition (e.g. "D:").</param>
    /// <exception cref="InvalidDataException">SHA-256 mismatch.</exception>
    /// <exception cref="InvalidOperationException"><c>reagentc.exe</c> returned a non-zero exit code.</exception>
    public async Task ApplyAsync(
        string recoveryWimPath,
        string expectedHash,
        string windowsVolume,
        string recoveryVolume,
        CancellationToken ct = default)
    {
        WinPeEnvironmentGuard.EnsureRunningInWinPe("Applying the recovery image");

        LogVerifyingHash(_logger, recoveryWimPath);
        var actualHash = await ComputeSha256Async(recoveryWimPath, ct);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogHashMismatch(_logger, expectedHash, actualHash);
            throw new InvalidDataException(
                $"SHA-256 mismatch. Expected: {expectedHash}, Actual: {actualHash}");
        }

        LogHashVerified(_logger, recoveryWimPath);

        var windowsRecoveryWimPath = GetWindowsRecoveryWimPath(windowsVolume);
        Directory.CreateDirectory(Path.GetDirectoryName(windowsRecoveryWimPath)!);
        File.Copy(recoveryWimPath, windowsRecoveryWimPath, overwrite: true);

        await FinalizeApplyAsync(windowsVolume, recoveryVolume, ct);
    }

    /// <summary>
    /// Fallback used when no Recovery Image has been published to the Portal catalog
    /// (<see cref="Services.DeviceGatewayApiClient.GetLatestRecoveryImageAsync"/> returned null).
    /// Every captured OS image already carries its own <c>Winre.wim</c> at the conventional
    /// <c>Windows\System32\Recovery</c> location as a normal part of the Windows install — this
    /// reuses that file (already implicitly hash-verified as part of the OS image apply step)
    /// instead of hard-failing the session. Returns <c>false</c> (without throwing) if the applied
    /// OS image has no embedded <c>Winre.wim</c> either, so the caller can fail the pipeline with
    /// a clear error.
    /// </summary>
    /// <param name="windowsVolume">Drive letter of the applied Windows volume (e.g. "C:").</param>
    /// <param name="recoveryVolume">Drive letter of the Recovery partition (e.g. "D:").</param>
    public async Task<bool> ApplyFromEmbeddedImageAsync(
        string windowsVolume,
        string recoveryVolume,
        CancellationToken ct = default)
    {
        var windowsRecoveryWimPath = GetWindowsRecoveryWimPath(windowsVolume);
        if (!File.Exists(windowsRecoveryWimPath))
        {
            // Nothing to apply — a read-only check, not a destructive action, so this can report
            // "no fallback available" without needing to be running in WinPE first.
            LogEmbeddedImageNotFound(_logger, windowsRecoveryWimPath);
            return false;
        }

        WinPeEnvironmentGuard.EnsureRunningInWinPe("Applying the recovery image");

        LogUsingEmbeddedFallback(_logger, windowsRecoveryWimPath);
        await FinalizeApplyAsync(windowsVolume, recoveryVolume, ct);
        return true;
    }

    /// <summary>
    /// Shared tail of both <see cref="ApplyAsync"/> and <see cref="ApplyFromEmbeddedImageAsync"/>:
    /// copies the WinRE image already staged at <c>{windowsVolume}\Windows\System32\Recovery\Winre.wim</c>
    /// onto the dedicated Recovery partition, registers and enables it via <c>reagentc.exe</c>, then
    /// best-effort removes the Recovery partition's temporary drive letter.
    /// </summary>
    private async Task FinalizeApplyAsync(string windowsVolume, string recoveryVolume, CancellationToken ct)
    {
        var windowsRecoveryWimPath = GetWindowsRecoveryWimPath(windowsVolume);
        var partitionRecoveryDir = Path.Combine($"{recoveryVolume}\\", "Recovery", "WindowsRE");
        Directory.CreateDirectory(partitionRecoveryDir);
        File.Copy(windowsRecoveryWimPath, Path.Combine(partitionRecoveryDir, "Winre.wim"), overwrite: true);

        await RunReagentcAsync($"/setreimage /path \"{partitionRecoveryDir}\" /target \"{windowsVolume}\\Windows\"", ct);
        await RunReagentcAsync($"/enable /target \"{windowsVolume}\\Windows\"", ct);

        await RemoveRecoveryDriveLetterAsync(recoveryVolume, ct);

        LogApplyCompleted(_logger, windowsVolume, recoveryVolume);
    }

    /// <summary>Computes the conventional path of the WinRE image embedded in an applied OS image.</summary>
    private static string GetWindowsRecoveryWimPath(string windowsVolume) =>
        Path.Combine($"{windowsVolume}\\", "Windows", "System32", "Recovery", "Winre.wim");

    private async Task RunReagentcAsync(string arguments, CancellationToken ct)
    {
        LogStartingReagentc(_logger, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "reagentc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            LogReagentcFailed(_logger, arguments, process.ExitCode, stdOut + stdErr);
            throw new InvalidOperationException(
                $"reagentc.exe {arguments} exited with code {process.ExitCode}: {stdOut}{stdErr}");
        }
    }

    /// <summary>
    /// Removes the Recovery partition's temporary drive letter, restoring the hidden-partition
    /// convention used by standard Windows installs. Best-effort: failure here must never fail
    /// the overall imaging pipeline, since the recovery image is already fully applied and
    /// functional at this point regardless of whether the letter is removed.
    /// </summary>
    private async Task RemoveRecoveryDriveLetterAsync(string recoveryVolume, CancellationToken ct)
    {
        var letter = recoveryVolume.TrimEnd(':');
        var script = $"select volume {letter}\r\nremove letter={letter} noerr\r\n";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ci-diskpart-hide-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            await process.StandardOutput.ReadToEndAsync(ct);
            await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                LogHideRecoveryLetterFailed(_logger, process.ExitCode);
            }
        }
        catch (Exception ex)
        {
            LogHideRecoveryLetterFailed(_logger, ex);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Verifying SHA-256 hash for {FilePath}…")]
    private static partial void LogVerifyingHash(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hash mismatch! Expected={Expected} Actual={Actual}")]
    private static partial void LogHashMismatch(ILogger logger, string expected, string actual);

    [LoggerMessage(Level = LogLevel.Information, Message = "SHA-256 verified for {FilePath}.")]
    private static partial void LogHashVerified(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting reagentc.exe {Arguments}")]
    private static partial void LogStartingReagentc(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Error, Message = "reagentc.exe {Arguments} exited with code {ExitCode}. Output: {Output}")]
    private static partial void LogReagentcFailed(ILogger logger, string arguments, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovery image applied. Windows={WindowsVolume} Recovery={RecoveryVolume}")]
    private static partial void LogApplyCompleted(ILogger logger, string windowsVolume, string recoveryVolume);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the Recovery partition's temporary drive letter (exit code {ExitCode}). This is cosmetic only and does not affect imaging.")]
    private static partial void LogHideRecoveryLetterFailed(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the Recovery partition's temporary drive letter. This is cosmetic only and does not affect imaging.")]
    private static partial void LogHideRecoveryLetterFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No published recovery image and no embedded WinRE found at {WimPath}.")]
    private static partial void LogEmbeddedImageNotFound(ILogger logger, string wimPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "No published recovery image — falling back to the OS image's embedded WinRE at {WimPath}.")]
    private static partial void LogUsingEmbeddedFallback(ILogger logger, string wimPath);
}

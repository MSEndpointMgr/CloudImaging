using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Applies the downloaded Windows Recovery Environment (WinRE) image to the device — the
/// "ApplyRecoveryImage" step of the imaging pipeline, run after boot configuration.
///
/// Unlike <see cref="ImageApplyService"/> (which uses DISM to apply the full OS image), the
/// recovery image is simply copied into place and registered with the Windows Recovery
/// Environment agent (<c>reagentc.exe</c>) — WinRE images are not applied via DISM.
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
        LogVerifyingHash(_logger, recoveryWimPath);
        var actualHash = await ComputeSha256Async(recoveryWimPath, ct);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogHashMismatch(_logger, expectedHash, actualHash);
            throw new InvalidDataException(
                $"SHA-256 mismatch. Expected: {expectedHash}, Actual: {actualHash}");
        }

        LogHashVerified(_logger, recoveryWimPath);

        var windowsRecoveryDir = Path.Combine($"{windowsVolume}\\", "Windows", "System32", "Recovery");
        Directory.CreateDirectory(windowsRecoveryDir);
        File.Copy(recoveryWimPath, Path.Combine(windowsRecoveryDir, "Winre.wim"), overwrite: true);

        var partitionRecoveryDir = Path.Combine($"{recoveryVolume}\\", "Recovery", "WindowsRE");
        Directory.CreateDirectory(partitionRecoveryDir);
        File.Copy(recoveryWimPath, Path.Combine(partitionRecoveryDir, "Winre.wim"), overwrite: true);

        await RunReagentcAsync($"/setreimage /path \"{partitionRecoveryDir}\" /target \"{windowsVolume}\\Windows\"", ct);
        await RunReagentcAsync($"/enable /target \"{windowsVolume}\\Windows\"", ct);

        await RemoveRecoveryDriveLetterAsync(recoveryVolume, ct);

        LogApplyCompleted(_logger, windowsVolume, recoveryVolume);
    }

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
}

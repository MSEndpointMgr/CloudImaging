using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Applies a Windows image (WIM) file to the target disk using DISM (T053, FR-006).
/// Verifies the SHA-256 hash of the WIM before invoking DISM.
/// Reports apply progress from DISM output.
/// </summary>
public sealed partial class ImageApplyService
{
    private readonly ILogger<ImageApplyService> _logger;

    public ImageApplyService(ILogger<ImageApplyService> logger) => _logger = logger;

    /// <summary>
    /// Verifies the SHA-256 hash and applies the WIM to the target volume.
    /// </summary>
    /// <param name="wimPath">Local path of the downloaded WIM file.</param>
    /// <param name="expectedHash">Expected SHA-256 hash (hex string, case-insensitive).</param>
    /// <param name="targetVolume">Drive letter for the target volume (e.g. "C:").</param>
    /// <param name="onProgress">Callback receiving 0–100 progress.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidDataException">SHA-256 mismatch.</exception>
    /// <exception cref="InvalidOperationException">DISM returned a non-zero exit code.</exception>
    public async Task ApplyAsync(
        string wimPath,
        string expectedHash,
        string targetVolume,
        Action<int>? onProgress,
        CancellationToken ct = default)
    {
        // Step 1: SHA-256 verification before invoking DISM (FR-006)
        LogVerifyingHash(_logger, wimPath);
        var actualHash = await ComputeSha256Async(wimPath, ct);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogHashMismatch(_logger, expectedHash, actualHash);
            throw new InvalidDataException(
                $"SHA-256 mismatch. Expected: {expectedHash}, Actual: {actualHash}");
        }

        LogHashVerified(_logger, wimPath);

        // Step 2: Apply WIM via DISM
        var dismArgs = $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:{targetVolume}\\";
        LogStartingDism(_logger, dismArgs);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "dism.exe",
                Arguments              = dismArgs,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            },
            EnableRaisingEvents = true,
        };

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                // DISM outputs progress lines like: "[==        20.0%          ]"
                if (int.TryParse(
                    System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+)\.?\d*\s*%").Groups[1].Value,
                    out var pct))
                {
                    onProgress?.Invoke(pct);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        ct.Register(() => { try { process.Kill(); } catch { /* best-effort */ } });

        int exitCode = await tcs.Task;
        if (exitCode != 0)
        {
            LogDismFailed(_logger, exitCode);
            throw new InvalidOperationException($"DISM exited with code {exitCode}.");
        }

        onProgress?.Invoke(100);
        LogApplyCompleted(_logger, targetVolume);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting DISM: {Args}")]
    private static partial void LogStartingDism(ILogger logger, string args);

    [LoggerMessage(Level = LogLevel.Error, Message = "DISM exited with code {ExitCode}.")]
    private static partial void LogDismFailed(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image successfully applied to {Volume}.")]
    private static partial void LogApplyCompleted(ILogger logger, string volume);
}

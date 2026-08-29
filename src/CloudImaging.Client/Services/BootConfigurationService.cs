using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Runs <c>bcdboot</c> to create UEFI boot files on the EFI System Partition after the OS image
/// has been applied — the "ConfigureBoot" step of the imaging pipeline. Without this step, the
/// previously single-partition, non-bootable layout would never actually boot: applying the WIM
/// alone does not populate the ESP or create BCD boot entries.
/// </summary>
public sealed partial class BootConfigurationService
{
    private readonly ILogger<BootConfigurationService> _logger;

    public BootConfigurationService(ILogger<BootConfigurationService> logger) => _logger = logger;

    /// <summary>
    /// Runs <c>bcdboot {windowsVolume}\Windows /s {efiSystemVolume} /f UEFI</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">bcdboot returned a non-zero exit code.</exception>
    public async Task ConfigureAsync(string windowsVolume, string efiSystemVolume, CancellationToken ct = default)
    {
        WinPeEnvironmentGuard.EnsureRunningInWinPe("Configuring boot files");

        var arguments = $"\"{windowsVolume}\\Windows\" /s {efiSystemVolume} /f UEFI";
        LogStartingBcdboot(_logger, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bcdboot.exe",
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
            LogBcdbootFailed(_logger, process.ExitCode, stdOut + stdErr);
            throw new InvalidOperationException($"bcdboot exited with code {process.ExitCode}: {stdOut}{stdErr}");
        }

        LogBcdbootCompleted(_logger, efiSystemVolume);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting bcdboot {Arguments}")]
    private static partial void LogStartingBcdboot(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Error, Message = "bcdboot exited with code {ExitCode}. Output: {Output}")]
    private static partial void LogBcdbootFailed(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot files configured on {EfiSystemVolume}.")]
    private static partial void LogBcdbootCompleted(ILogger logger, string efiSystemVolume);
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Restarts the device via the built-in Windows <c>shutdown</c> command (present in both a
/// full Windows install and WinPE), invoked by <see cref="ViewModels.ResultsViewModel"/> once
/// its post-success countdown reaches zero.
/// </summary>
public sealed partial class SystemRestartService
{
    private readonly ILogger<SystemRestartService> _logger;

    public SystemRestartService(ILogger<SystemRestartService> logger) => _logger = logger;

    /// <summary>
    /// Restarts the machine immediately via <c>shutdown /r /t 0</c>.
    ///
    /// SAFETY: In DEV_SIMULATION (Debug) builds this only logs the intent instead of actually
    /// restarting. The dev simulation navigator reaches this exact same code path on a
    /// developer's real desktop rather than WinPE on a target device, so it must never actually
    /// reboot the machine running the debugger — mirrors the DEV_SIMULATION safety guard used
    /// throughout <c>DevMode\DevSimulationLauncher.cs</c>.
    /// </summary>
    public void Restart()
    {
#if DEV_SIMULATION
        LogRestartSuppressed(_logger);
#else
        LogRestarting(_logger);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0 /c \"Cloud Imaging: restarting after successful imaging\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogRestartFailed(_logger, ex);
        }
#endif
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imaging succeeded — restarting the system now.")]
    private static partial void LogRestarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DEV SIMULATION: system restart suppressed (would run shutdown /r /t 0).")]
    private static partial void LogRestartSuppressed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invoke shutdown /r.")]
    private static partial void LogRestartFailed(ILogger logger, Exception ex);
}

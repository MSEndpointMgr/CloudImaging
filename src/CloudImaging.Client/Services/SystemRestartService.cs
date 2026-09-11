using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Restarts the device via the Windows PE utility, invoked by
/// <see cref="ViewModels.ResultsViewModel"/> once its post-success countdown reaches zero.
/// </summary>
public sealed partial class SystemRestartService
{
    private readonly ILogger<SystemRestartService> _logger;

    public SystemRestartService(ILogger<SystemRestartService> logger) => _logger = logger;

    /// <summary>
    /// Restarts the Windows PE session via <c>wpeutil Reboot</c>.
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
            Process.Start(CreateRestartProcessStartInfo());
        }
        catch (Exception ex)
        {
            LogRestartFailed(_logger, ex);
        }
#endif
    }

    internal static ProcessStartInfo CreateRestartProcessStartInfo() => new()
    {
        FileName = "wpeutil.exe",
        Arguments = "Reboot",
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Imaging succeeded — restarting the system now.")]
    private static partial void LogRestarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DEV SIMULATION: system restart suppressed (would run wpeutil Reboot).")]
    private static partial void LogRestartSuppressed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invoke wpeutil Reboot.")]
    private static partial void LogRestartFailed(ILogger logger, Exception ex);
}

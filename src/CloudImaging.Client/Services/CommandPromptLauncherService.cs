using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Services;

/// <summary>
/// Launches an interactive <c>cmd.exe</c> console for advanced WinPE troubleshooting
/// ("Support Tools", FR-051d). Opt-in per boot image via Media Builder's "Enable command
/// prompt access" checkbox, which stamps <c>SupportTools:CommandPromptEnabled</c> into the
/// Client's appsettings.json (see <c>BootImageGenerationService.StampSupportToolsConfigAsync</c>)
/// — off by default since it grants full unrestricted shell access, bypassing the guided
/// imaging workflow entirely.
///
/// <see cref="Views.MainWindow"/> sets <c>Topmost="True"</c> (WinPE has no shell/taskbar, so the
/// Client must always stay above everything else). Launching cmd.exe naively would therefore
/// render BEHIND the always-on-top Client and be unusable, so callers must supply
/// <paramref name="setMainWindowTopmost"/>-style callback (see <see cref="Launch"/>) which this
/// service uses to toggle Topmost off for the duration of the console session and restore it
/// once cmd.exe exits.
/// </summary>
public sealed partial class CommandPromptLauncherService
{
    private readonly ILogger<CommandPromptLauncherService> _logger;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    /// <param name="logger">Defaults to a no-op logger when not supplied (e.g. simple call sites).</param>
    /// <param name="startProcessOverride">
    /// Test seam. Defaults to a real <see cref="Process.Start(ProcessStartInfo)"/> call. Tests can
    /// substitute a fake that returns an already-exiting process instead of really opening an
    /// interactive console window.
    /// </param>
    public CommandPromptLauncherService(
        ILogger<CommandPromptLauncherService>? logger = null,
        Func<ProcessStartInfo, Process?>? startProcessOverride = null)
    {
        _logger       = logger ?? NullLogger<CommandPromptLauncherService>.Instance;
        _startProcess = startProcessOverride ?? (psi => Process.Start(psi));
    }

    /// <summary>
    /// Launches cmd.exe as a real, visible, interactive console. <paramref name="setMainWindowTopmost"/>
    /// is invoked with <c>false</c> immediately (so the console isn't hidden behind the
    /// always-on-top Client) and with <c>true</c> once cmd.exe exits. The callback is expected to
    /// marshal onto the UI thread itself if needed — <see cref="Process.Exited"/> fires on a
    /// thread-pool thread, not the UI thread.
    /// </summary>
    public void Launch(Action<bool> setMainWindowTopmost)
    {
        ArgumentNullException.ThrowIfNull(setMainWindowTopmost);

        LogLaunching(_logger);
        setMainWindowTopmost(false);

        Process? process;
        try
        {
            process = _startProcess(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow  = false,
            });
        }
        catch (Exception ex)
        {
            LogLaunchFailed(_logger, ex);
            setMainWindowTopmost(true);
            return;
        }

        if (process is null)
        {
            setMainWindowTopmost(true);
            return;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            LogExited(_logger);
            setMainWindowTopmost(true);
            process.Dispose();
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching interactive command prompt (support tools).")]
    private static partial void LogLaunching(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to launch command prompt.")]
    private static partial void LogLaunchFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command prompt closed.")]
    private static partial void LogExited(ILogger logger);
}

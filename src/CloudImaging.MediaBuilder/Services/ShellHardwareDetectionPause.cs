using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Stops the Shell Hardware Detection service while USB partitions are created, and starts it
/// again afterwards. Windows gives a new partition a drive letter a moment before diskpart formats
/// it, and that service then has Explorer offer to format the "unformatted" drive itself.
/// </summary>
internal sealed partial class ShellHardwareDetectionPause : IDisposable
{
    private const string DefaultServiceName = "ShellHWDetection";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceRunning = 0x00000004;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly string _serviceName;
    private bool _restart;

    private ShellHardwareDetectionPause(ILogger logger, string serviceName, bool restart)
    {
        _logger = logger;
        _serviceName = serviceName;
        _restart = restart;
    }

    /// <summary>
    /// Stops the service if it is running. Never throws: without the pause, partitioning still
    /// works and Windows may only show its prompt.
    /// </summary>
    public static async Task<ShellHardwareDetectionPause> BeginAsync(ILogger logger, string serviceName = DefaultServiceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            LogNotPaused(logger, serviceName, error);
            return new(logger, serviceName, restart: false);
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStop);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                LogNotPaused(logger, serviceName, error);
                return new(logger, serviceName, restart: false);
            }

            try
            {
                if (!QueryServiceStatus(service, out var status) || status.CurrentState != ServiceRunning)
                {
                    return new(logger, serviceName, restart: false);
                }

                if (!ControlService(service, ServiceControlStop, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    LogNotPaused(logger, serviceName, error);
                    return new(logger, serviceName, restart: false);
                }

                // Not cancellable: once stopped, the service must be started again by Dispose.
                var deadline = DateTime.UtcNow + StopTimeout;
                while (QueryServiceStatus(service, out status) && status.CurrentState != ServiceStopped && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100, CancellationToken.None);
                }

                return new(logger, serviceName, restart: true);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Starts the service again if this pause stopped it.</summary>
    public void Dispose()
    {
        if (!_restart)
        {
            return;
        }

        _restart = false;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            LogNotRestarted(_logger, _serviceName, error);
            return;
        }

        try
        {
            var service = OpenService(manager, _serviceName, ServiceStart);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                LogNotRestarted(_logger, _serviceName, error);
                return;
            }

            try
            {
                // 1056: someone else already started it again.
                if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() is var error && error != 1056)
                {
                    LogNotRestarted(_logger, _serviceName, error);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not pause the {Service} service while partitioning (Win32 error {Error}); Windows may offer to format the new partitions.")]
    private static partial void LogNotPaused(ILogger logger, string service, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the {Service} service again after partitioning (Win32 error {Error}). It starts again with Windows.")]
    private static partial void LogNotRestarted(ILogger logger, string service, int error);
}

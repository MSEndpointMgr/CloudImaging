using System.Runtime.InteropServices;

namespace CloudImaging.Client.Services;

/// <summary>
/// Starts a Windows service through the Service Control Manager. Windows RE ships neither sc.exe
/// nor net.exe, so the service API is the only way to start the WLAN service there.
/// </summary>
internal static class WindowsServiceStarter
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStopped = 1;
    private const uint ServiceRunning = 4;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceRequestTimeout = 1053;

    /// <summary>
    /// Starts <paramref name="serviceName"/> (and, through the SCM, its dependencies) and waits until
    /// it runs. Returns 0 on success, otherwise the Win32 error code.
    /// </summary>
    public static int Start(string serviceName, TimeSpan wait)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return Marshal.GetLastWin32Error();
        try
        {
            var service = OpenService(manager, serviceName, ServiceStart | ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error();
            try
            {
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                        return error;
                }

                var deadline = DateTime.UtcNow + wait;
                while (QueryServiceStatus(service, out var status))
                {
                    if (status.CurrentState == ServiceRunning)
                        return 0;
                    if (status.CurrentState == ServiceStopped)
                        return status.Win32ExitCode != 0 ? (int)status.Win32ExitCode : ErrorServiceNotActive;
                    if (DateTime.UtcNow >= deadline)
                        return ErrorServiceRequestTimeout;
                    Thread.Sleep(250);
                }
                return Marshal.GetLastWin32Error();
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

    /// <summary>
    /// The state of every service <paramref name="serviceName"/> depends on, transitively, for the log
    /// when it would not start: Windows only reports that "a dependency" failed, not which one.
    /// </summary>
    public static string DescribeDependencies(string serviceName)
    {
        var lines = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([serviceName]);
        var manager = OpenSCManager(null, null, ScManagerConnect);
        try
        {
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (!visited.Add(name))
                    continue;

                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                if (key is null)
                {
                    lines.Add($"{name}: not registered");
                    continue;
                }

                var start = key.GetValue("Start") is int s && s == 4 ? ", disabled" : string.Empty;
                lines.Add($"{name}: {QueryState(manager, name)}{start}");
                foreach (var dependency in key.GetValue("DependOnService") as string[] ?? [])
                    queue.Enqueue(dependency);
            }
        }
        finally
        {
            if (manager != IntPtr.Zero)
                CloseServiceHandle(manager);
        }
        return string.Join("; ", lines);
    }

    private static string QueryState(IntPtr manager, string name)
    {
        if (manager == IntPtr.Zero)
            return "unknown";
        var service = OpenService(manager, name, ServiceQueryStatus);
        if (service == IntPtr.Zero)
            return $"cannot open (error {Marshal.GetLastWin32Error()})";
        try
        {
            if (!QueryServiceStatus(service, out var status))
                return "unknown";
            return status.CurrentState switch
            {
                ServiceRunning => "running",
                ServiceStopped when status.Win32ExitCode != 0 => $"stopped (exit {status.Win32ExitCode})",
                ServiceStopped => "stopped",
                _ => $"state {status.CurrentState}",
            };
        }
        finally
        {
            CloseServiceHandle(service);
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
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

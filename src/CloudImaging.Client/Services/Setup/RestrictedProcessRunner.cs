using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudImaging.Client.Services.Setup;

/// <summary>What to start, where, with which extra environment variables and for how long.</summary>
public sealed record RestrictedProcessRequest(string FileName, string Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout);

/// <summary>How a restricted process ended.</summary>
public enum RestrictedProcessOutcome
{
    /// <summary>The process exited by itself; <see cref="RestrictedProcessResult.ExitCode"/> is set.</summary>
    Exited,

    /// <summary>The timeout passed and the whole process tree was stopped.</summary>
    TimedOut,

    /// <summary>The restart guard could not be set up, so the process was never started.</summary>
    GuardUnavailable,
}

/// <summary>The result of one run, with the last lines of its merged standard output and error.</summary>
public sealed record RestrictedProcessResult(RestrictedProcessOutcome Outcome, int? ExitCode, TimeSpan Duration, IReadOnlyList<string> Output);

/// <summary>
/// Starts a process that cannot restart the device: its token has no shutdown privileges, and it
/// runs in a kill-on-close job that also blocks ExitWindows, so the timeout stops the whole tree.
/// </summary>
public interface IRestrictedProcessRunner
{
    /// <summary>Runs the process to completion or timeout. Throws <see cref="Win32Exception"/> when it cannot be started.</summary>
    RestrictedProcessResult Run(RestrictedProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRestrictedProcessRunner"/> over CreateRestrictedToken, CreateProcessAsUser and a job
/// object. A restricted copy of the caller's own token needs no SeAssignPrimaryTokenPrivilege. When
/// the token or the job cannot be created the process is not started: running a script that could
/// restart the device mid-imaging is worse than not running it.
/// </summary>
public sealed class RestrictedProcessRunner : IRestrictedProcessRunner
{
    /// <summary>Lines of output kept per run.</summary>
    public const int MaxOutputLines = 200;

    private static readonly string[] RemovedPrivileges = ["SeShutdownPrivilege", "SeRemoteShutdownPrivilege"];

    /// <inheritdoc/>
    public RestrictedProcessResult Run(RestrictedProcessRequest request, CancellationToken cancellationToken)
    {
        var restrictedToken = IntPtr.Zero;
        var job = IntPtr.Zero;
        try
        {
            restrictedToken = CreateRestrictedCopyOfOwnToken();
            job = CreateGuardJob();
            if (restrictedToken == IntPtr.Zero || job == IntPtr.Zero)
            {
                return new RestrictedProcessResult(RestrictedProcessOutcome.GuardUnavailable, null, TimeSpan.Zero, []);
            }
            return Start(request, restrictedToken, job, cancellationToken);
        }
        finally
        {
            // Closing the job kills anything the process left running (KILL_ON_JOB_CLOSE).
            if (job != IntPtr.Zero) CloseHandle(job);
            if (restrictedToken != IntPtr.Zero) CloseHandle(restrictedToken);
        }
    }

    private static RestrictedProcessResult Start(RestrictedProcessRequest request, IntPtr token, IntPtr job, CancellationToken cancellationToken)
    {
        var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out var readPipe, out var writePipe, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0);

        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = writePipe,
            hStdError = writePipe,
        };
        var commandLine = $"\"{request.FileName}\" {request.Arguments}\0".ToCharArray();
        var environment = EnvironmentBlock(request.Environment);
        var output = new Queue<string>();
        Thread? reader = null;
        var stopwatch = Stopwatch.StartNew();
        PROCESS_INFORMATION process = default;
        try
        {
            if (!CreateProcessAsUser(token, null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment, request.WorkingDirectory, ref startup, out process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            CloseHandle(writePipe);
            writePipe = IntPtr.Zero;

            if (!AssignProcessToJobObject(job, process.hProcess))
            {
                TerminateProcess(process.hProcess, 1);
                return new RestrictedProcessResult(RestrictedProcessOutcome.GuardUnavailable, null, TimeSpan.Zero, []);
            }

            reader = new Thread(() => ReadLines(readPipe, output)) { IsBackground = true, Name = "Restricted process output" };
            reader.Start();
            if (ResumeThread(process.hThread) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                TerminateJobObject(job, 1);
                throw new Win32Exception(error);
            }

            // Short waits so a cancelled pipeline stops the script promptly.
            var timedOut = false;
            while (WaitForSingleObject(process.hProcess, 250) == WAIT_TIMEOUT)
            {
                if (stopwatch.Elapsed >= request.Timeout || cancellationToken.IsCancellationRequested)
                {
                    TerminateJobObject(job, 1);
                    _ = WaitForSingleObject(process.hProcess, 5_000);
                    timedOut = true;
                    break;
                }
            }
            stopwatch.Stop();
            int? exitCode = !timedOut && GetExitCodeProcess(process.hProcess, out var code) ? (int)code : null;
            reader.Join(TimeSpan.FromSeconds(5));
            lock (output)
            {
                return new RestrictedProcessResult(timedOut ? RestrictedProcessOutcome.TimedOut : RestrictedProcessOutcome.Exited, exitCode, stopwatch.Elapsed, [.. output]);
            }
        }
        finally
        {
            if (writePipe != IntPtr.Zero) CloseHandle(writePipe);
            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
            if (reader is null) CloseHandle(readPipe);
        }
    }

    private static void ReadLines(IntPtr readPipe, Queue<string> output)
    {
        using var stream = new FileStream(new SafeFileHandle(readPipe, ownsHandle: true), FileAccess.Read);
        using var text = new StreamReader(stream, Encoding.Default);
        try
        {
            while (text.ReadLine() is { } line)
            {
                lock (output)
                {
                    output.Enqueue(line);
                    if (output.Count > MaxOutputLines) output.Dequeue();
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke when the job was terminated; what was read so far is kept.
        }
    }

    /// <summary>The current environment plus the request's variables, as a sorted Unicode block.</summary>
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }
        foreach (var (name, value) in extra)
        {
            variables[name] = value;
        }
        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        return block.Append('\0').ToString();
    }

    private static IntPtr CreateRestrictedCopyOfOwnToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out var own))
        {
            return IntPtr.Zero;
        }
        try
        {
            var remove = new LUID_AND_ATTRIBUTES[RemovedPrivileges.Length];
            for (var i = 0; i < RemovedPrivileges.Length; i++)
            {
                if (!LookupPrivilegeValue(null, RemovedPrivileges[i], out remove[i].Luid))
                {
                    return IntPtr.Zero;
                }
            }
            return CreateRestrictedToken(own, 0, 0, IntPtr.Zero, (uint)remove.Length, remove, 0, IntPtr.Zero, out var restricted) ? restricted : IntPtr.Zero;
        }
        finally
        {
            CloseHandle(own);
        }
    }

    private static IntPtr CreateGuardJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_EXITWINDOWS };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !SetInformationJobObject(job, JobObjectBasicUIRestrictions, ref ui, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    // ── Win32 interop ────────────────────────────────────────────────────────────────────────

    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_UILIMIT_EXITWINDOWS = 0x00000080;
    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(
        IntPtr existingTokenHandle,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        [In] LUID_AND_ATTRIBUTES[] privilegesToDelete,
        uint restrictedSidCount,
        IntPtr sidsToRestrict,
        out IntPtr newTokenHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        [In, Out] char[] lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        string lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}

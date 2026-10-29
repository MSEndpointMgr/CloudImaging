<#
.SYNOPSIS
    Runs the Windows setup work Cloud Imaging staged for this device.

.DESCRIPTION
    Started once by the answer file's RunSynchronous command in the specialize pass, as SYSTEM.
    Reads the manifest the Cloud Imaging Client wrote in WinPE, verifies the SHA-256 of every file
    an item needs, and starts each item in order: driver installers first, then the setup profile's
    Specialize scripts. Every item runs with a restricted token that has no shutdown
    privileges, inside a kill-on-close job that also blocks ExitWindows, so nothing it starts can
    restart the device in the middle of Windows setup. Items that time out are stopped with their
    whole process tree. A failed item never stops the next one.

    One ASCII line per item is written to the log, which is never deleted. Everything that was
    staged is removed afterwards, also after a failure, together with the folders a vendor
    installer extracts itself to and staging folders left empty. The script always exits 0, so
    Windows setup never treats it as a failed command.

.PARAMETER ManifestPath
    Path of the manifest the Client wrote in WinPE.

.PARAMETER LogPath
    Path of the log file. Its folder is created when missing.

.EXAMPLE
    powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Windows\Setup\CloudImaging\Invoke-CloudImagingSetup.ps1"

.NOTES
    FileName:    Invoke-CloudImagingSetup.ps1
    Author:      MSEndpointMgr
    Contact:     @MSEndpointMgr
    Created:     2026-10-23
    Updated:     2026-10-29

    Version history:
    1.0.0 - (2026-10-23) Initial version.
    1.1.0 - (2026-10-29) Removes staging folders left empty after cleanup, outside the Windows folder.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false, HelpMessage = "Path of the manifest the Client wrote in WinPE.")]
    [ValidateNotNullOrEmpty()]
    [string] $ManifestPath = "$($env:WINDIR)\Setup\CloudImaging\manifest.json",
    [Parameter(Mandatory = $false, HelpMessage = "Path of the log file; its folder is created when missing.")]
    [ValidateNotNullOrEmpty()]
    [string] $LogPath = "$($env:WINDIR)\Logs\CloudImaging\SetupRunner.log"
)
Begin {
    $Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $Results = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $CleanupPaths = New-Object -TypeName "System.Collections.Generic.List[System.Object]"
    $SucceededCount = 0
    $FailedCount = 0
    $Position = 0
    $Manifest = $null
    $ProfileLabel = "No setup profile"
    $GuardReady = $false
    $CleanedUp = $false
    $SystemRoot = [System.IO.Path]::GetFullPath("$($env:SystemDrive)\")
    $LogFolder = [System.IO.Path]::GetFullPath((Split-Path -Path $LogPath -Parent))

    function Write-RunnerLog {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Fields of the line, joined with two spaces.")]
            [ValidateNotNullOrEmpty()]
            [string[]] $Fields
        )
        $Line = "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture))  $($Fields -join '  ')"
        try {
            if (-not (Test-Path -Path $LogFolder)) {
                New-Item -Path $LogFolder -ItemType Directory -Force | Out-Null
            }

            # ASCII so the log reads the same in every tool; anything else becomes a question mark.
            Add-Content -Path $LogPath -Value $Line -Encoding Ascii
        }
        catch [System.Exception] {
            Write-Warning -Message "Could not write to $($LogPath): $($_.Exception.Message)"
        }
    }

    function Resolve-SystemPath {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Path relative to the system drive root.")]
            [ValidateNotNullOrEmpty()]
            [string] $RelativePath
        )

        # Paths come from a file on disk, so one that would leave the system drive is refused.
        if ([System.IO.Path]::IsPathRooted($RelativePath) -or ($RelativePath -split '[\\/]' -contains '..')) {
            return $null
        }
        $FullPath = [System.IO.Path]::GetFullPath((Join-Path -Path $SystemRoot -ChildPath $RelativePath))
        if (-not $FullPath.StartsWith($SystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
        return $FullPath
    }

    function Get-StagedFileHash {
        param (
            [Parameter(Mandatory = $true, HelpMessage = "Full path of the file to hash.")]
            [ValidateNotNullOrEmpty()]
            [string] $Path
        )

        # .NET directly rather than Get-FileHash, so the runner depends on no module being loadable.
        $Algorithm = [System.Security.Cryptography.SHA256]::Create()
        $Stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($Algorithm.ComputeHash($Stream))).Replace("-", "")
        }
        finally {
            $Stream.Dispose()
            $Algorithm.Dispose()
        }
    }

    function Invoke-StagedCleanup {
        foreach ($CleanupPath in $CleanupPaths) {
            $FullPath = Resolve-SystemPath -RelativePath $CleanupPath
            if ($null -eq $FullPath) {
                continue
            }

            # The log folder is the only record of what ran, so it is never removed.
            if ($LogFolder.StartsWith($FullPath.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or $LogFolder -ieq $FullPath.TrimEnd('\')) {
                continue
            }
            try {
                if (Test-Path -LiteralPath $FullPath) {
                    Remove-Item -LiteralPath $FullPath -Recurse -Force -ErrorAction Stop
                }

                # Staging parents such as CloudImaging\Drivers and SWSetup go once empty; folders under Windows belong to Windows and stay.
                $Parent = Split-Path -Path $FullPath -Parent
                while ($Parent.Length -gt $SystemRoot.Length -and ($Parent.Substring($SystemRoot.Length) -split '\\')[0] -ine "Windows" -and (Test-Path -LiteralPath $Parent) -and $null -eq (Get-ChildItem -LiteralPath $Parent -Force | Select-Object -First 1)) {
                    Remove-Item -LiteralPath $Parent -Force -ErrorAction Stop
                    $Parent = Split-Path -Path $Parent -Parent
                }
            }
            catch [System.Exception] {
                Write-Warning -Message "Could not remove $($FullPath): $($_.Exception.Message)"
                Write-RunnerLog -Fields @("Cleanup", "`"$($CleanupPath)`"", "Failed", $_.Exception.Message)
            }
        }
    }

    $LauncherSource = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudImagingSetup
{
    public static class RestrictedLauncher
    {
        private static IntPtr token = IntPtr.Zero;

        public static bool PrepareToken()
        {
            IntPtr own;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0001 | 0x0008, out own)) return false;
            try
            {
                LUID_AND_ATTRIBUTES[] remove = new LUID_AND_ATTRIBUTES[2];
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out remove[0].Luid)) return false;
                if (!LookupPrivilegeValue(null, "SeRemoteShutdownPrivilege", out remove[1].Luid)) return false;
                return CreateRestrictedToken(own, 0, 0, IntPtr.Zero, 2, remove, 0, IntPtr.Zero, out token);
            }
            finally
            {
                CloseHandle(own);
            }
        }

        // Returns the exit code, -1 when the job could not be set up, or -2 after a timeout.
        public static int Run(string commandLine, string workingDirectory, int timeoutSeconds)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return -1;
            PROCESS_INFORMATION process = new PROCESS_INFORMATION();
            try
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000;
                JOBOBJECT_BASIC_UI_RESTRICTIONS ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS();
                ui.UIRestrictionsClass = 0x80;
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))) return -1;
                if (!SetInformationJobObject(job, 4, ref ui, (uint)Marshal.SizeOf(typeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)))) return -1;

                STARTUPINFO startup = new STARTUPINFO();
                startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                if (!CreateProcessAsUser(token, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x00000004 | 0x08000000, IntPtr.Zero, workingDirectory, ref startup, out process))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                if (!AssignProcessToJobObject(job, process.hProcess))
                {
                    TerminateProcess(process.hProcess, 1);
                    return -1;
                }
                ResumeThread(process.hThread);
                if (WaitForSingleObject(process.hProcess, (uint)timeoutSeconds * 1000) == 0x00000102)
                {
                    TerminateJobObject(job, 1);
                    WaitForSingleObject(process.hProcess, 5000);
                    return -2;
                }
                uint exitCode;
                GetExitCodeProcess(process.hProcess, out exitCode);
                return unchecked((int)exitCode);
            }
            finally
            {
                if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
                if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
                CloseHandle(job);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars;
            public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
            public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
            public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_UI_RESTRICTIONS { public uint UIRestrictionsClass; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount, [In] LUID_AND_ATTRIBUTES[] privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
"@
}
Process {
    try {
        # Without a readable manifest there is nothing to run; whatever was staged is removed.
        try {
            $Manifest = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        }
        catch [System.Exception] {
            Write-Warning -Message "The manifest could not be read: $($_.Exception.Message)"
            Write-RunnerLog -Fields @("Start", "No setup profile", "The manifest could not be read, so nothing ran")
            $CleanupPaths.Add([System.IO.Path]::GetFullPath((Split-Path -Path $ManifestPath -Parent)).Substring($SystemRoot.Length))
            return
        }

        if ($null -ne $Manifest.profile) {
            $ProfileLabel = "$($Manifest.profile.name), version $($Manifest.profile.version)"
        }
        $Items = @($Manifest.items | Sort-Object -Property order)
        foreach ($Item in $Items) {
            foreach ($ItemCleanupPath in @($Item.cleanupPaths)) {
                if (-not [string]::IsNullOrWhiteSpace($ItemCleanupPath)) {
                    $CleanupPaths.Add($ItemCleanupPath)
                }
            }
        }
        foreach ($ManifestCleanupPath in @($Manifest.cleanupPaths)) {
            if (-not [string]::IsNullOrWhiteSpace($ManifestCleanupPath)) {
                $CleanupPaths.Add($ManifestCleanupPath)
            }
        }
        Write-RunnerLog -Fields @("Start", $ProfileLabel, "$($Items.Count) $(if ($Items.Count -eq 1) { 'item' } else { 'items' })")

        # The guard is compiled once; without it no item may run, because a restart inside
        # specialize breaks Windows setup.
        try {
            Add-Type -TypeDefinition $LauncherSource -Language CSharp -ErrorAction Stop
            $GuardReady = [CloudImagingSetup.RestrictedLauncher]::PrepareToken()
        }
        catch [System.Exception] {
            Write-Warning -Message "The restart guard could not be set up: $($_.Exception.Message)"
            $GuardReady = $false
        }

        foreach ($Item in $Items) {
            $Position++
            Write-Progress -Activity "Cloud Imaging setup" -Status "$($Item.name)" -PercentComplete (($Position - 1) * 100 / [Math]::Max(1, $Items.Count))
            $ItemLabel = "$($Position)/$($Items.Count)"
            $NameField = "`"$($Item.name)`""
            $Outcome = "Failed"
            $Detail = ""

            # An item that never started still gets a duration, so every log line has the same fields.
            $Duration = "0.0 s"
            try {
                if (-not $GuardReady) {
                    $Detail = "Not run because restarts could not be blocked on this device"
                }
                else {
                    # Every file is checked before anything starts, so a changed or missing file never runs.
                    $FilesValid = $true
                    $FirstFile = $null
                    foreach ($File in @($Item.files)) {
                        $FullPath = Resolve-SystemPath -RelativePath $File.path
                        if ($null -eq $FullPath) {
                            $FilesValid = $false
                            $Detail = "Skipped because a path leaves the system drive"
                            break
                        }
                        if ($null -eq $FirstFile) {
                            $FirstFile = $FullPath
                        }
                        if (-not (Test-Path -LiteralPath $FullPath -PathType Leaf) -or (Get-StagedFileHash -Path $FullPath) -ine $File.sha256) {
                            $FilesValid = $false
                            $Detail = "File does not match the manifest"
                            break
                        }
                    }

                    $Executable = $Item.command.executable
                    if ($FilesValid -and $Executable -match '\\') {
                        $Executable = Resolve-SystemPath -RelativePath $Executable
                    }
                    $WorkingDirectory = Resolve-SystemPath -RelativePath $Item.command.workingDirectory
                    if ($FilesValid -and ($null -eq $Executable -or $null -eq $WorkingDirectory)) {
                        $FilesValid = $false
                        $Detail = "Skipped because a path leaves the system drive"
                    }

                    if ($FilesValid) {
                        $Arguments = "$($Item.command.arguments)".Replace("{0}", "$($FirstFile)")
                        $Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                        $ExitCode = [CloudImagingSetup.RestrictedLauncher]::Run("`"$($Executable)`" $($Arguments)", $WorkingDirectory, [int]$Item.timeoutSeconds)
                        $Stopwatch.Stop()
                        $Duration = "$($Stopwatch.Elapsed.TotalSeconds.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture)) s"
                        if ($ExitCode -eq -1) {
                            $Detail = "Not run because restarts could not be blocked on this device"
                            $Duration = "0.0 s"
                        }
                        elseif ($ExitCode -eq -2) {
                            $Minutes = [int][Math]::Ceiling([int]$Item.timeoutSeconds / 60)
                            $Detail = "Stopped after $($Minutes) $(if ($Minutes -eq 1) { 'minute' } else { 'minutes' })"
                        }
                        elseif (@($Item.successExitCodes) -contains $ExitCode) {
                            $Outcome = "Succeeded"
                            $Detail = "exit $($ExitCode)"
                        }
                        else {
                            $Detail = "exit $($ExitCode)"
                        }
                    }
                }
            }
            catch [System.Exception] {
                Write-Warning -Message "Item $($ItemLabel) $($NameField) could not run: $($_.Exception.Message)"
                $Detail = "Could not start: $($_.Exception.Message)"
            }

            if ($Outcome -eq "Succeeded") {
                $SucceededCount++
            }
            else {
                $FailedCount++
            }
            $Results.Add([PSCustomObject]@{
                Position = $Position
                Name = $Item.name
                Outcome = $Outcome
                Detail = $Detail
            })
            Write-RunnerLog -Fields @($ItemLabel, $Item.kind, $NameField, $Outcome, $Detail, $Duration)
        }
        Write-Progress -Activity "Cloud Imaging setup" -Completed
        Write-RunnerLog -Fields @("End", $ProfileLabel, "$($SucceededCount) succeeded, $($FailedCount) failed")
    }
    catch [System.Exception] {
        Write-Warning -Message "The setup runner stopped: $($_.Exception.Message)"
        Write-RunnerLog -Fields @("End", $ProfileLabel, "Stopped: $($_.Exception.Message)")
        Invoke-StagedCleanup
        $CleanedUp = $true
    }
}
End {
    if (-not $CleanedUp) {
        Invoke-StagedCleanup
    }
    exit 0
}

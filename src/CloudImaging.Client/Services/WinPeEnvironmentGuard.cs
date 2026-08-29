using Microsoft.Win32;

namespace CloudImaging.Client.Services;

/// <summary>
/// Refuses to let destructive imaging operations (disk format, OS image download, DISM apply)
/// run anywhere except inside a genuine WinPE boot environment.
/// </summary>
/// <remarks>
/// <para>
/// The Client is built to run only from WinPE boot media (FR-001) — <see cref="DiskFormatService"/>
/// wipes the sole eligible fixed disk with diskpart's <c>clean</c> command, and
/// <see cref="ImageApplyService"/> applies an OS image directly over whatever is already on that
/// disk. Nothing about the published, self-contained executable itself stops someone from
/// copying it onto a normal Windows install (or launching a locally built copy on their own
/// machine) and triggering the same pipeline there by mistake, which would download, format, and
/// re-image the live system it's running from. This guard is the last line of defense against
/// that: every destructive service checks it before doing anything irreversible, independent of
/// how it was invoked, rather than relying solely on the caller (<see cref="ViewModels.ImagingWorkflowViewModel"/>)
/// to have checked first.
/// </para>
/// <para>
/// Detection uses the registry key WinPE creates on every boot,
/// <c>HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\MiniNT</c> — the same technique
/// Microsoft Deployment Toolkit and Configuration Manager task sequences use to detect a WinPE
/// environment. It exists only inside WinPE; a full Windows install (client or server SKU) never
/// creates it.
/// </para>
/// </remarks>
public static class WinPeEnvironmentGuard
{
    private const string MiniNtKeyPath = @"SYSTEM\CurrentControlSet\Control\MiniNT";

    /// <summary>True when the current process is running inside a WinPE boot environment.</summary>
    public static bool IsWinPe()
    {
#if DEV_SIMULATION
        // DEV-ONLY escape hatch so a developer can exercise the real format/download/apply
        // pipeline end-to-end (e.g. against a disposable test VM) without needing an actual
        // WinPE boot. Compiled only into Debug builds (see DEV_SIMULATION in the .csproj) —
        // this branch does not exist in Release, so it can never be used to bypass the guard
        // in a shipped build, and it cannot affect Release-configured test runs either.
        if (Environment.GetEnvironmentVariable("CLOUDIMAGING_SIMULATE_WINPE") == "1")
            return true;
#endif
        using var key = Registry.LocalMachine.OpenSubKey(MiniNtKeyPath);
        return key is not null;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless currently running inside WinPE.
    /// </summary>
    /// <param name="action">Short description of the operation being refused, e.g. "Formatting the target disk".</param>
    public static void EnsureRunningInWinPe(string action)
    {
        if (!IsWinPe())
        {
            throw new InvalidOperationException(
                $"{action} is only permitted while running from WinPE boot media. This device does " +
                "not appear to be running WinPE, so the operation has been refused to avoid modifying " +
                "a live Windows installation.");
        }
    }
}

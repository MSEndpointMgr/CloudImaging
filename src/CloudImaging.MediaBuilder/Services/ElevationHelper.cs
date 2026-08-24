using System.Diagnostics;
using System.IO;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Shared helpers for the "relaunch this same executable elevated via one UAC prompt" pattern
/// used by both <see cref="BootImageGenerationService"/> (DISM image mounting) and
/// <see cref="UsbPreparationService"/> (diskpart partitioning + bootsect activation). Both
/// operations require Administrator privileges, but the main Media Builder process must stay
/// non-elevated so Entra ID sign-in can keep using MSAL's Windows broker (WAM), which does not
/// work reliably from an elevated process.
/// </summary>
internal static class ElevationHelper
{
    /// <summary>True when the current process is running with Administrator privileges.</summary>
    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relaunches <paramref name="exePath"/> elevated (one UAC prompt) with the given
    /// <paramref name="args"/>. Throws <see cref="System.ComponentModel.Win32Exception"/> with
    /// native error code 1223 (ERROR_CANCELLED) when the user declines the UAC prompt.
    /// </summary>
    public static Process StartElevatedProcess(string exePath, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = exePath,
            Arguments       = args,
            UseShellExecute = true,
            Verb            = "runas",
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the elevated process.");
    }

    /// <summary>
    /// Deletes a directory tree even when it contains read-only files (e.g. left behind by
    /// copype.cmd or DISM). <see cref="Directory.Delete(string, bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on a read-only file regardless of process
    /// privilege — elevation does not bypass the attribute; it must be cleared first.
    /// Best-effort throughout: a cleanup failure must never mask the real workflow result/error.
    /// </summary>
    public static void TryDeleteDirectoryRecursive(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch { /* best effort — Directory.Delete below will surface anything that still blocks removal */ }
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort cleanup — never let this mask the real workflow result/error */ }
    }
}

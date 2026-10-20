using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Shared helpers for the "relaunch this same executable elevated via one UAC prompt" pattern
/// used by <see cref="BootImageGenerationService"/> (DISM image mounting),
/// <see cref="UsbPreparationService"/> (diskpart partitioning + bootsect activation), and
/// <see cref="IsoGenerationService"/> (copype.cmd's own DISM mount while staging WinPE media).
/// All three operations require Administrator privileges, but the main Media Builder process
/// must stay non-elevated so Entra ID sign-in can keep using MSAL's Windows broker (WAM), which
/// does not work reliably from an elevated process.
/// </summary>
internal static class ElevationHelper
{
    private const string OwnerMarkerFileName = "owner.pid";

    /// <summary>True when the current process is running with Administrator privileges.</summary>
    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Marks a temp IPC/working folder as owned by this process, so another Media Builder
    /// instance's orphan sweep never deletes it while a (possibly parentless) worker still uses it.
    /// </summary>
    public static void WriteOwnerMarker(string dir)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(dir, OwnerMarkerFileName),
                $"{current.Id}|{current.ProcessName}|{current.StartTime.Ticks}");
        }
        catch { /* best effort: without a marker the folder is simply unprotected */ }
    }

    /// <summary>
    /// True when the folder's owner marker names a still-running process. PID, name, and start
    /// time must all match so a reused PID is not mistaken for the owner.
    /// </summary>
    public static bool IsOwnedByLiveProcess(string dir) => GetOwnerState(dir) == OwnerState.Alive;

    /// <summary>Whether a folder's owner is running, has exited, or cannot be determined.</summary>
    public enum OwnerState
    {
        /// <summary>No readable marker, or the owner could not be inspected.</summary>
        Unknown,
        /// <summary>The owner process is still running.</summary>
        Alive,
        /// <summary>The owner process has exited (or its PID now belongs to another process).</summary>
        Gone,
    }

    /// <summary>Reads the folder's owner marker and checks that process.</summary>
    public static OwnerState GetOwnerState(string dir)
    {
        string[] parts;
        try
        {
            var markerFile = Path.Combine(dir, OwnerMarkerFileName);
            if (!File.Exists(markerFile))
                return OwnerState.Unknown;
            parts = File.ReadAllText(markerFile).Split('|');
        }
        catch
        {
            return OwnerState.Unknown;
        }

        if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[2], out var startTicks))
            return OwnerState.Unknown;

        try
        {
            using var owner = Process.GetProcessById(pid);
            return owner.ProcessName == parts[1] && owner.StartTime.Ticks == startTicks ? OwnerState.Alive : OwnerState.Gone;
        }
        catch (ArgumentException)
        {
            // No process with that PID.
            return OwnerState.Gone;
        }
        catch
        {
            // e.g. access denied reading another session's process: can't tell.
            return OwnerState.Unknown;
        }
    }

    /// <summary>
    /// Elevated-worker side of cancellation: cancels <paramref name="cts"/> when the parent writes
    /// <paramref name="cancelFile"/>, or when the parent that owns the IPC folder has exited (closed
    /// or killed), so a worker never keeps running unattended and still cleans up its mounts.
    /// </summary>
    public static async Task WatchForCancelOrParentExitAsync(string cancelFile, CancellationTokenSource cts)
    {
        var ipcDir = Path.GetDirectoryName(cancelFile) ?? string.Empty;
        var tick = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                // The owner check is a process lookup, so it runs every ~1.2 s rather than every tick.
                if (File.Exists(cancelFile) || (++tick % 4 == 0 && GetOwnerState(ipcDir) == OwnerState.Gone))
                {
                    cts.Cancel();
                    return;
                }
                await Task.Delay(300, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the worker finishes and cancels cts in its finally.
        }
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
    ///
    /// Retries a few times with a short delay before giving up: right after a DISM
    /// <c>/Unmount-Image /Commit</c>, a handle on a file under this path (held by DISM itself
    /// or by antivirus real-time scanning of the just-written WIM) can take a moment longer to
    /// release, so a single un-retried delete attempt here was failing often enough in practice
    /// to leave a working folder behind on nearly every run. Returns whether the directory was
    /// actually removed so callers can tell an orphaned-folder count is genuinely growing rather
    /// than silently swallowing that.
    /// </summary>
    public static bool TryDeleteDirectoryRecursive(string path) => TryDeleteDirectoryRecursive(path, out _);

    /// <summary>Same as <see cref="TryDeleteDirectoryRecursive(string)"/>, also returning why deletion failed.</summary>
    public static bool TryDeleteDirectoryRecursive(string path, out string? failureReason)
    {
        const int maxAttempts        = 4;
        const int retryDelayMs       = 250;

        failureReason = null;
        if (!Directory.Exists(path))
            return true;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
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
                return true;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                Thread.Sleep(retryDelayMs);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        failureReason = lastError?.Message;
        return false;
    }
}

using System.Diagnostics;
using System.IO;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Undoes DISM mounts and attached disk images left behind by a Media Builder run that was killed
/// before its own cleanup ran. Each working folder keeps a journal of what it mounted, written
/// before the mount starts, so the sweep knows exactly what to undo.
/// </summary>
public static class MountCleanup
{
    /// <summary>Journal file inside a working folder listing its mounts, one per line.</summary>
    public const string JournalFileName = "mounts.journal";

    // Working folders that can hold a mount. IPC folders (ci-*-elevated-*) never do.
    private static readonly string[] WorkDirPrefixes = ["ci-bootimage-", "ci-deploy-"];

    // Folders from before the journal existed were themselves the mount directory.
    private const string LegacyDeployMountPrefix = "ci-deploy-mount-";

    private const string WimKind = "wim";
    private const string DiskImageKind = "iso";
    private static readonly object JournalLock = new();

    /// <summary>One journal entry: a WIM mount directory or an attached disk image (ISO).</summary>
    /// <param name="Kind"><c>wim</c> or <c>iso</c>.</param>
    /// <param name="Path">Mount directory for a WIM; image file path for an ISO.</param>
    public sealed record MountRecord(string Kind, string Path)
    {
        /// <summary>True for a DISM WIM mount.</summary>
        public bool IsWim => Kind == WimKind;
    }

    /// <summary>Records a WIM mount in <paramref name="workDir"/> before <c>/Mount-Image</c> runs.</summary>
    public static void RecordWimMount(string workDir, string mountDir) => Append(workDir, new MountRecord(WimKind, mountDir));

    /// <summary>Records a disk image (ISO) in <paramref name="workDir"/> before it is attached.</summary>
    public static void RecordDiskImage(string workDir, string imagePath) => Append(workDir, new MountRecord(DiskImageKind, imagePath));

    /// <summary>Removes an entry once its mount or attachment has been undone normally.</summary>
    public static void Forget(string workDir, string path)
    {
        lock (JournalLock)
        {
            try
            {
                var remaining = ReadJournal(workDir).Where(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
                File.WriteAllLines(System.IO.Path.Combine(workDir, JournalFileName), remaining.Select(Format));
            }
            catch { /* best effort: a stale entry only costs a harmless no-op during the next sweep */ }
        }
    }

    /// <summary>Reads the journal of <paramref name="workDir"/>; empty when there is none.</summary>
    public static IReadOnlyList<MountRecord> ReadJournal(string workDir)
    {
        var journal = System.IO.Path.Combine(workDir, JournalFileName);
        if (!File.Exists(journal))
            return [];

        var records = new List<MountRecord>();
        foreach (var line in File.ReadAllLines(journal))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0] is WimKind or DiskImageKind && parts[1].Length > 0)
                records.Add(new MountRecord(parts[0], parts[1]));
        }
        return records;
    }

    /// <summary>Working folders under <paramref name="tempRoot"/> (default %TEMP%) left by runs that are no longer alive.</summary>
    public static IReadOnlyList<string> FindOrphanedWorkDirs(string? tempRoot = null)
    {
        var root = tempRoot ?? System.IO.Path.GetTempPath();
        try
        {
            return WorkDirPrefixes
                .SelectMany(prefix => Directory.GetDirectories(root, $"{prefix}*"))
                .Where(dir => !ElevationHelper.IsOwnedByLiveProcess(dir))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Undoes everything orphaned working folders recorded, then deletes them. Must run elevated.
    /// Never throws: it runs at the start of every elevated operation and must not block it.
    /// </summary>
    /// <param name="log">Receives one line per action, for the live log.</param>
    /// <param name="tempRoot">Folder to scan; defaults to %TEMP%. Test seam.</param>
    /// <param name="runProcess">Runs (file, arguments) and returns the exit code. Test seam.</param>
    /// <returns>How many orphaned folders were found.</returns>
    public static async Task<int> SweepAsync(Action<string>? log = null, string? tempRoot = null, Func<string, string, Task<int>>? runProcess = null)
    {
        var orphaned = FindOrphanedWorkDirs(tempRoot);
        if (orphaned.Count == 0)
            return 0;

        runProcess ??= RunHiddenAsync;
        log?.Invoke($"Cleaning up {orphaned.Count} leftover working folder(s) from an interrupted run.");

        // Clears stale mount registrations that a per-folder unmount cannot.
        await TryRunAsync(runProcess, "dism.exe", "/Cleanup-Mountpoints");

        // A hive left loaded by the Wi-Fi check keeps its file open and would block the unmount.
        foreach (var hive in MountedRecoveryImage.HiveNames)
            await TryRunAsync(runProcess, "reg.exe", $"unload HKLM\\{hive}");

        foreach (var dir in orphaned)
        {
            foreach (var record in RecordsToUndo(dir))
            {
                if (record.IsWim)
                {
                    log?.Invoke($"Discarding a mounted image left over from an interrupted run: \"{record.Path}\".");
                    await TryRunAsync(runProcess, "dism.exe", $"/Unmount-Image /MountDir:\"{record.Path}\" /Discard");
                }
                else
                {
                    log?.Invoke($"Detaching a disk image left attached by an interrupted run: \"{record.Path}\".");
                    await TryRunAsync(runProcess, "powershell.exe", DismountDiskImageArguments(record.Path));
                }
            }

            if (!ElevationHelper.TryDeleteDirectoryRecursive(dir, out var failureReason))
                log?.Invoke($"Could not remove leftover folder \"{dir}\". It will be retried next time. ({failureReason})");
        }

        return orphaned.Count;
    }

    /// <summary>PowerShell arguments that detach <paramref name="imagePath"/>; quotes are escaped for a single-quoted literal.</summary>
    public static string DismountDiskImageArguments(string imagePath) =>
        $"-NoProfile -NonInteractive -Command \"Dismount-DiskImage -ImagePath '{imagePath.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null\"";

    private static IEnumerable<MountRecord> RecordsToUndo(string dir)
    {
        IReadOnlyList<MountRecord> journal;
        try { journal = ReadJournal(dir); }
        catch { journal = []; }

        if (journal.Count > 0)
            return journal;

        // No journal: folders from before it existed.
        var mountSubDir = System.IO.Path.Combine(dir, "Mount");
        if (Directory.Exists(mountSubDir))
            return [new MountRecord(WimKind, mountSubDir)];
        if (System.IO.Path.GetFileName(dir).StartsWith(LegacyDeployMountPrefix, StringComparison.OrdinalIgnoreCase))
            return [new MountRecord(WimKind, dir)];
        return [];
    }

    private static void Append(string workDir, MountRecord record)
    {
        lock (JournalLock)
        {
            // Best effort: without an entry the sweep still finds the standard Mount subfolder.
            try { File.AppendAllLines(System.IO.Path.Combine(workDir, JournalFileName), [Format(record)]); }
            catch { /* see comment above */ }
        }
    }

    private static string Format(MountRecord record) => $"{record.Kind}\t{record.Path}";

    private static async Task TryRunAsync(Func<string, string, Task<int>> runProcess, string fileName, string arguments)
    {
        // Undoing something that is no longer mounted fails harmlessly, so exit codes are ignored.
        try { await runProcess(fileName, arguments); }
        catch { /* best effort */ }
    }

    private static async Task<int> RunHiddenAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Could not start {fileName}.");

        // Drain both streams so a chatty tool never blocks on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }
}

using System.Globalization;
using System.IO;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Pre-flight free-disk-space guard (inspired by the legacy OSDLiteDeploy module's
/// Get-FreeSpace check) so large downloads/copies fail fast with a clear message instead of
/// partway through a confusing I/O error.
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>Extra headroom required beyond the exact byte count being written.</summary>
    private const long SafetyMarginBytes = 512L * 1024 * 1024; // 512 MB

    /// <summary>
    /// Throws <see cref="IOException"/> if the drive hosting <paramref name="path"/> does not
    /// have at least <paramref name="requiredBytes"/> plus a safety margin of free space.
    /// Best-effort: if the drive's free space cannot be determined, the check is skipped
    /// rather than blocking progress.
    /// </summary>
    public static void EnsureFreeSpace(string path, long requiredBytes, string activityDescription)
    {
        if (requiredBytes <= 0)
            return;

        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(root))
            return;

        long available;
        try
        {
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return;
        }

        var required = requiredBytes + SafetyMarginBytes;
        if (available < required)
        {
            throw new IOException(
                $"Not enough free disk space on {root} to {activityDescription}. " +
                $"Required: {FormatBytes(required)}, available: {FormatBytes(available)}.");
        }
    }

    /// <summary>Recursively sums file sizes beneath <paramref name="directory"/>. Returns 0 if it does not exist.</summary>
    public static long DirectorySizeBytes(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* ignore files that vanish/are inaccessible mid-scan */ }
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{size:0.0} {units[unit]}");
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>
/// BIOS version text shared by Imaging Core (which decides at registration) and the Client (which
/// checks again before it stages the update), so both read and compare versions the same way.
/// </summary>
public static partial class BiosVersion
{
    /// <summary>
    /// The version in a device's SMBIOS BIOS version, for example <c>01.06.01</c> from
    /// <c>X84 Ver. 01.06.01</c>; null when it holds none.
    /// </summary>
    public static string? Installed(string? smbiosVersion)
    {
        if (string.IsNullOrWhiteSpace(smbiosVersion))
        {
            return null;
        }

        var matches = VersionPattern().Matches(smbiosVersion);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    /// <summary>Compares two versions per numeric segment; HP's own tool compares them as text.</summary>
    public static int Compare(string? a, string? b)
    {
        var left = Parse(a) ?? [];
        var right = Parse(b) ?? [];
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var compare = (i < left.Length ? left[i] : 0).CompareTo(i < right.Length ? right[i] : 0);
            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    /// <summary>The numeric segments of a version, or null when it is not one.</summary>
    public static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionPattern();
}

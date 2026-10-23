using System.Globalization;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Win32;

namespace CloudImaging.Client.Services.Setup;

/// <summary>
/// Pure mapping from a setup profile registry value to the offline hive it lands in: the hive file
/// in the applied image, the key it is mounted under in WinPE, the key path inside the hive, and
/// the typed data to write.
/// </summary>
public static class OfflineRegistryPath
{
    private const string CurrentControlSet = "CurrentControlSet";

    /// <summary>Key name under HKLM that the hive is mounted at, for example <c>CI_OFFLINE_SOFTWARE</c>.</summary>
    public static string MountName(SetupRegistryHive hive) => hive switch
    {
        SetupRegistryHive.Software => "CI_OFFLINE_SOFTWARE",
        SetupRegistryHive.System => "CI_OFFLINE_SYSTEM",
        SetupRegistryHive.DefaultUser => "CI_OFFLINE_DEFAULTUSER",
        _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
    };

    /// <summary>Full mount root as shown in logs, for example <c>HKLM\CI_OFFLINE_SOFTWARE</c>.</summary>
    public static string MountRoot(SetupRegistryHive hive) => $@"HKLM\{MountName(hive)}";

    /// <summary>Hive file relative to the applied volume's root.</summary>
    public static string HiveFile(SetupRegistryHive hive) => hive switch
    {
        SetupRegistryHive.Software => @"Windows\System32\config\SOFTWARE",
        SetupRegistryHive.System => @"Windows\System32\config\SYSTEM",
        SetupRegistryHive.DefaultUser => @"Users\Default\NTUSER.DAT",
        _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
    };

    /// <summary>
    /// The key inside the mounted hive. Offline there is no <c>CurrentControlSet</c>: under SYSTEM it
    /// becomes <c>ControlSet00N</c>, where N is the image's <c>Select\Current</c>.
    /// </summary>
    public static string MapKey(SetupRegistryHive hive, string key, int currentControlSet)
    {
        var trimmed = key.Trim();
        if (hive != SetupRegistryHive.System)
        {
            return trimmed;
        }

        var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
        var first = separator < 0 ? trimmed : trimmed[..separator];
        if (!first.Equals(CurrentControlSet, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var controlSet = string.Create(CultureInfo.InvariantCulture, $"ControlSet{currentControlSet:D3}");
        return separator < 0 ? controlSet : controlSet + trimmed[separator..];
    }

    /// <summary>Converts profile data to the value and kind the registry API takes.</summary>
    public static bool TryParseData(SetupRegistryValueType type, string data, out object value, out RegistryValueKind kind)
    {
        switch (type)
        {
            case SetupRegistryValueType.Text:
                value = data;
                kind = RegistryValueKind.String;
                return true;
            case SetupRegistryValueType.ExpandString:
                value = data;
                kind = RegistryValueKind.ExpandString;
                return true;
            case SetupRegistryValueType.MultiString:
                value = data.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                kind = RegistryValueKind.MultiString;
                return true;
            case SetupRegistryValueType.DWord when TryParseNumber(data, 8, uint.MaxValue, out var dword):
                // The registry API takes a DWORD as a signed int; the bits are what matter.
                value = unchecked((int)(uint)dword);
                kind = RegistryValueKind.DWord;
                return true;
            case SetupRegistryValueType.QWord when TryParseNumber(data, 16, ulong.MaxValue, out var qword):
                value = unchecked((long)qword);
                kind = RegistryValueKind.QWord;
                return true;
            default:
                value = string.Empty;
                kind = RegistryValueKind.Unknown;
                return false;
        }
    }

    /// <summary>Full path with value name, as shown on the deployment timeline.</summary>
    public static string DisplayTarget(SetupRegistryValue value)
    {
        var prefix = value.Hive switch
        {
            SetupRegistryHive.Software => @"HKLM\SOFTWARE",
            SetupRegistryHive.System => @"HKLM\SYSTEM",
            _ => "Default user",
        };
        var valueName = string.IsNullOrEmpty(value.ValueName) ? "(Default)" : value.ValueName;
        return $@"{prefix}\{value.Key.Trim()}\{valueName}";
    }

    private static bool TryParseNumber(string data, int maxHexDigits, ulong max, out ulong value)
    {
        value = 0;
        var text = data.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = text[2..];
            return hex.Length > 0 && hex.Length <= maxHexDigits
                && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                && value <= max;
        }
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= max;
    }
}

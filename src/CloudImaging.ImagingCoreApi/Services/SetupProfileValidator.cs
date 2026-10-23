using System.Globalization;
using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Field and limit rules for setup profiles. Pure: every problem is returned at once as a
/// ready-to-show sentence, in the order the editor shows the sections.
/// </summary>
public static class SetupProfileValidator
{
    /// <summary>Longest profile, registry value and script name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Longest registry key.</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Longest component of a registry key.</summary>
    public const int MaxKeyComponentLength = 255;

    /// <summary>Longest registry value name.</summary>
    public const int MaxValueNameLength = 255;

    /// <summary>Longest String, Expandable string or Multi-string data.</summary>
    public const int MaxStringDataLength = 4096;

    /// <summary>Shortest script timeout in minutes.</summary>
    public const int MinTimeoutMinutes = 1;

    /// <summary>Longest script timeout in minutes.</summary>
    public const int MaxTimeoutMinutes = 30;

    /// <summary>Message when a profile targets nothing.</summary>
    public const string EmptyTargetingMessage = "Choose at least one location or All devices.";

    private const string CurrentControlSet = "CurrentControlSet";

    /// <summary>Validates a profile's name and targeting.</summary>
    public static List<string> ValidateProfile(string? name, SetupProfileTargeting? targeting, IReadOnlySet<Guid> knownLocationIds)
    {
        var problems = new List<string>();
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            problems.Add("Enter a profile name of 1 to 100 characters.");
        }

        if (targeting is null || (!targeting.AllDevices && targeting.LocationIds.Count == 0))
        {
            problems.Add(EmptyTargetingMessage);
        }
        else if (targeting.AllDevices && targeting.LocationIds.Count > 0)
        {
            problems.Add("Choose either All devices or Selected locations, not both.");
        }
        else if (targeting.LocationIds.Any(id => !knownLocationIds.Contains(id)))
        {
            problems.Add("One of the chosen locations no longer exists. Reload the page and choose again.");
        }
        return problems;
    }

    /// <summary>Validates the registry values and scripts of a version's content.</summary>
    public static List<string> ValidateContent(SetupProfileContent content)
    {
        var problems = new List<string>();
        ValidateRegistryValues(content.RegistryValues, problems);
        ValidateScripts(content.Scripts, problems);
        return problems;
    }

    private static void ValidateRegistryValues(IReadOnlyList<SetupRegistryValue> values, List<string> problems)
    {
        if (values.Count > SetupProfileContent.MaxRegistryValues)
        {
            problems.Add("A profile can have at most 200 registry values.");
        }

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var label = Label(value.Name);
            if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Trim().Length > MaxNameLength)
            {
                problems.Add("Every registry value needs a name of 1 to 100 characters.");
            }

            if (ValidateKey(value.Hive, value.Key) is { } keyProblem)
            {
                problems.Add(string.Format(CultureInfo.InvariantCulture, keyProblem, label));
            }

            if ((value.ValueName ?? string.Empty).Length > MaxValueNameLength)
            {
                problems.Add($"The value name of {label} can be at most 255 characters.");
            }

            if (ValidateData(value.Type, value.Data ?? string.Empty) is { } dataProblem)
            {
                problems.Add(string.Format(CultureInfo.InvariantCulture, dataProblem, label));
            }

            var identity = $"{value.Hive}|{value.Key?.Trim()}|{value.ValueName ?? string.Empty}";
            if (seen.TryGetValue(identity, out var first))
            {
                problems.Add($"{label} sets the same value as {first}. Keep one of them.");
            }
            else
            {
                seen[identity] = label;
            }
        }
    }

    private static void ValidateScripts(IReadOnlyList<SetupScript> scripts, List<string> problems)
    {
        if (scripts.Count > SetupProfileContent.MaxScripts)
        {
            problems.Add("A profile can have at most 20 scripts.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            var label = Label(script.Name, "a script without a name");
            if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Trim().Length > MaxNameLength)
            {
                problems.Add("Every script needs a name of 1 to 100 characters.");
            }
            else if (!names.Add(script.Name.Trim()))
            {
                problems.Add($"Script names must be unique. Rename {label} and save again.");
            }

            if (!Enum.IsDefined(script.Runs))
            {
                problems.Add($"Choose when {label} runs.");
            }

            if (script.TimeoutMinutes is < MinTimeoutMinutes or > MaxTimeoutMinutes)
            {
                problems.Add($"The timeout of {label} must be 1 to 30 minutes.");
            }

            if (string.IsNullOrWhiteSpace(script.Content))
            {
                problems.Add($"{label} is empty. Add the script text.");
            }
            else if (Encoding.UTF8.GetByteCount(script.Content) > SetupProfileContent.MaxScriptBytes)
            {
                problems.Add($"{label} is larger than 64 KB. Shorten it.");
            }
        }

        foreach (var phase in scripts.GroupBy(s => s.Runs))
        {
            var orders = phase.Select(s => s.Order).Order().ToList();
            if (!orders.SequenceEqual(Enumerable.Range(1, orders.Count)))
            {
                problems.Add("Script order must run from 1 without gaps within each phase. Reload the page and try again.");
                break;
            }
        }
    }

    /// <summary>Returns a composite-format problem for a key ({0} is the value's label), or null.</summary>
    private static string? ValidateKey(SetupRegistryHive hive, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            return "The key of {0} must be 1 to 512 characters.";
        }

        if (key.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith('\\') || key.EndsWith('\\')
            || key.Split('\\').Any(component => component.Length == 0))
        {
            return "The key of {0} must be relative to the hive, for example Policies\\Microsoft\\FVE.";
        }

        if (key.Split('\\').Any(component => component.Length > MaxKeyComponentLength))
        {
            return "Each part of the key of {0} can be at most 255 characters.";
        }

        var first = key.Split('\\')[0];
        if (hive != SetupRegistryHive.System && first.Equals(CurrentControlSet, StringComparison.OrdinalIgnoreCase))
        {
            return "The key of {0} starts with CurrentControlSet, which only exists under HKLM SYSTEM.";
        }
        return null;
    }

    /// <summary>Returns a composite-format problem for data ({0} is the value's label), or null.</summary>
    private static string? ValidateData(SetupRegistryValueType type, string data) => type switch
    {
        SetupRegistryValueType.Text or SetupRegistryValueType.ExpandString when data.Length > MaxStringDataLength =>
            "The data of {0} can be at most 4,096 characters.",
        SetupRegistryValueType.MultiString when data.Length > MaxStringDataLength =>
            "The data of {0} can be at most 4,096 characters.",
        SetupRegistryValueType.MultiString when data.Length == 0 || data.Split('\n').Any(line => line.Length == 0) =>
            "The data of {0} cannot contain empty lines.",
        SetupRegistryValueType.DWord when !TryParseNumber(data, 8, uint.MaxValue, out _) =>
            "The data of {0} must be a DWORD: 0 to 4294967295, or 0x followed by up to 8 hex digits.",
        SetupRegistryValueType.QWord when !TryParseNumber(data, 16, ulong.MaxValue, out _) =>
            "The data of {0} must be a QWORD: 0 to 18446744073709551615, or 0x followed by up to 16 hex digits.",
        _ when !Enum.IsDefined(type) => "Choose a type for {0}.",
        _ => null,
    };

    /// <summary>Parses decimal or 0x hex data within <paramref name="max"/>.</summary>
    public static bool TryParseNumber(string data, int maxHexDigits, ulong max, out ulong value)
    {
        value = 0;
        var text = data.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = text[2..];
            return hex.Length is > 0 && hex.Length <= maxHexDigits
                && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                && value <= max;
        }
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= max;
    }

    private static string Label(string? name, string unnamed = "a registry value without a name") =>
        string.IsNullOrWhiteSpace(name) ? unnamed : $"\"{name.Trim()}\"";
}

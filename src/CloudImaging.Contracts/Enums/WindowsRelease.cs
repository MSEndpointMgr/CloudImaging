using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudImaging.Contracts.Enums;

/// <summary>
/// A Windows release an OS image installs and a model is onboarded for, such as Windows 11,
/// version 26H2. Any release Microsoft ships later is valid without a code change. JSON carries
/// <c>Windows11_26H2</c>; <see cref="ToString"/> gives <c>Windows11Version26H2</c>, the form stored
/// table keys and blob names already hold.
/// </summary>
[JsonConverter(typeof(WindowsReleaseJsonConverter))]
public readonly partial record struct WindowsRelease : IComparable<WindowsRelease>
{
    private WindowsRelease(int major, string version)
    {
        Major = major;
        Version = version;
    }

    /// <summary>Windows 10, version 22H2.</summary>
    public static WindowsRelease Windows10Version22H2 { get; } = new(10, "22H2");

    /// <summary>Windows 11, version 23H2.</summary>
    public static WindowsRelease Windows11Version23H2 { get; } = new(11, "23H2");

    /// <summary>Windows 11, version 24H2.</summary>
    public static WindowsRelease Windows11Version24H2 { get; } = new(11, "24H2");

    /// <summary>Windows 11, version 25H2.</summary>
    public static WindowsRelease Windows11Version25H2 { get; } = new(11, "25H2");

    /// <summary>The Windows version: 10 or 11.</summary>
    public int Major { get; }

    /// <summary>The release within the Windows version, such as <c>26H2</c>.</summary>
    public string Version { get; }

    /// <summary>"Windows 11, version 26H2", as Microsoft names the release.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"Windows {Major}, version {Version}");

    /// <summary>The JSON and route form, <c>Windows11_26H2</c>.</summary>
    public string WireName => string.Create(CultureInfo.InvariantCulture, $"Windows{Major}_{Version}");

    /// <summary><c>Windows11Version26H2</c>, the form stored keys and blob names hold.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Windows{Major}Version{Version}");

    /// <summary>Orders by Windows version, then by release.</summary>
    public int CompareTo(WindowsRelease other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : string.CompareOrdinal(Version, other.Version);

    /// <summary>A release from its Windows version (10 or 11) and release (<c>26H2</c>); false when either is not one.</summary>
    public static bool TryCreate(int major, string? version, out WindowsRelease release)
    {
        var text = version?.Trim();
        if (major is not (10 or 11) || text is null || !VersionPattern().IsMatch(text))
        {
            release = default;
            return false;
        }

        release = new WindowsRelease(major, text.ToUpperInvariant());
        return true;
    }

    /// <summary>Reads <c>Windows11_26H2</c>, or the stored form <c>Windows11Version26H2</c>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out WindowsRelease release)
    {
        var match = value is null ? null : ValuePattern().Match(value.Trim());
        if (match is not { Success: true })
        {
            release = default;
            return false;
        }

        return TryCreate(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, out release);
    }

    /// <summary>Reads Microsoft's name of a release, such as "Windows 11, version 26H2".</summary>
    public static bool TryParseName(string? name, out WindowsRelease release)
    {
        var match = name is null ? null : NamePattern().Match(name);
        if (match is not { Success: true })
        {
            release = default;
            return false;
        }

        return TryCreate(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, out release);
    }

    /// <summary>True when <paramref name="left"/> is an older release.</summary>
    public static bool operator <(WindowsRelease left, WindowsRelease right) => left.CompareTo(right) < 0;

    /// <summary>True when <paramref name="left"/> is the same or an older release.</summary>
    public static bool operator <=(WindowsRelease left, WindowsRelease right) => left.CompareTo(right) <= 0;

    /// <summary>True when <paramref name="left"/> is a newer release.</summary>
    public static bool operator >(WindowsRelease left, WindowsRelease right) => left.CompareTo(right) > 0;

    /// <summary>True when <paramref name="left"/> is the same or a newer release.</summary>
    public static bool operator >=(WindowsRelease left, WindowsRelease right) => left.CompareTo(right) >= 0;

    [GeneratedRegex(@"^Windows(10|11)(?:_|Version)(\d{2}H[12])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"\bWindows (10|11), version (\d{2}H[12])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\d{2}H[12]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>Reads and writes <see cref="WindowsRelease"/> as <c>Windows11_26H2</c>.</summary>
public sealed class WindowsReleaseJsonConverter : JsonConverter<WindowsRelease>
{
    /// <inheritdoc/>
    public override WindowsRelease Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WindowsRelease value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.WireName);
    }

    /// <inheritdoc/>
    public override WindowsRelease ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc/>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, WindowsRelease value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.WireName);
    }

    private static WindowsRelease Parse(string? value) =>
        WindowsRelease.TryParse(value, out var release) ? release : throw new JsonException($"\"{value}\" is not a Windows release.");
}

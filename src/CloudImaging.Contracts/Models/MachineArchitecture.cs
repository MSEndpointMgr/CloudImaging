using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Target processor architecture for boot media, Client binaries, and imaging artifacts
/// (todo/arm64-support.md). Serialized as the lowercase wire values "x64"/"arm64" via
/// <see cref="MachineArchitectureJsonConverter"/> so it round-trips with the string literals
/// already used across the Device Gateway API, USB preparation manifest, and boot-image
/// self-update contract.
///
/// A missing/null value on any older record predates architecture tracking and MUST be treated
/// as <see cref="X64"/> by every reader — there is no ARM64 product history to account for.
/// </summary>
[JsonConverter(typeof(MachineArchitectureJsonConverter))]
public enum MachineArchitecture
{
    /// <summary>64-bit x86 (AMD64/Intel 64).</summary>
    X64,

    /// <summary>64-bit ARM.</summary>
    Arm64,
}

/// <summary>Reads/writes <see cref="MachineArchitecture"/> as the lowercase wire values "x64"/"arm64".</summary>
public sealed class MachineArchitectureJsonConverter : JsonConverter<MachineArchitecture>
{
    /// <inheritdoc/>
    public override MachineArchitecture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "x64" => MachineArchitecture.X64,
            "arm64" => MachineArchitecture.Arm64,
            var other => throw new JsonException($"Unknown machine architecture \"{other}\"."),
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, MachineArchitecture value, JsonSerializerOptions options) =>
        writer.WriteStringValue(MachineArchitecturePlatform.Slug(value));
}

/// <summary>
/// Centralized per-architecture platform mapping (todo/arm64-support.md "Architecture model"),
/// so ADK/RID/UEFI-loader values are never hardcoded ad hoc at individual call sites.
/// </summary>
public static class MachineArchitecturePlatform
{
    /// <summary>Lowercase wire/display slug, e.g. for output file names and JSON — matches the JSON converter's wire format.</summary>
    public static string Slug(MachineArchitecture architecture) => architecture switch
    {
        MachineArchitecture.X64 => "x64",
        MachineArchitecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unsupported machine architecture."),
    };

    /// <summary>Parses a wire slug ("x64"/"arm64"); false for anything else.</summary>
    public static bool TryParseSlug(string? slug, out MachineArchitecture architecture)
    {
        (var ok, architecture) = slug switch
        {
            "x64" => (true, MachineArchitecture.X64),
            "arm64" => (true, MachineArchitecture.Arm64),
            _ => (false, MachineArchitecture.X64),
        };
        return ok;
    }

    /// <summary>Reads a persisted slug; missing/unknown values predate architecture tracking and are x64.</summary>
    public static MachineArchitecture ParseSlugOrDefault(string? slug) =>
        slug == "arm64" ? MachineArchitecture.Arm64 : MachineArchitecture.X64;

    /// <summary>ADK folder/argument name, e.g. <c>Deployment Tools\{name}\Oscdimg</c> and copype.cmd's own architecture argument.</summary>
    public static string AdkArchitectureName(MachineArchitecture architecture) => architecture switch
    {
        MachineArchitecture.X64 => "amd64",
        MachineArchitecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unsupported machine architecture."),
    };

    /// <summary>.NET self-contained publish runtime identifier for the Cloud Imaging Client.</summary>
    public static string DotNetRuntimeIdentifier(MachineArchitecture architecture) => architecture switch
    {
        MachineArchitecture.X64 => "win-x64",
        MachineArchitecture.Arm64 => "win-arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unsupported machine architecture."),
    };

    /// <summary>UEFI fallback boot loader file name expected under <c>\efi\boot\</c>. There is no BIOS fallback for ARM64.</summary>
    public static string UefiBootLoaderFileName(MachineArchitecture architecture) => architecture switch
    {
        MachineArchitecture.X64 => "bootx64.efi",
        MachineArchitecture.Arm64 => "bootaa64.efi",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unsupported machine architecture."),
    };

    /// <summary>
    /// The processor architecture of the machine currently running this code. ADK tool
    /// EXECUTABLES (Dism.exe, Oscdimg.exe) are native binaries that only run on a matching host,
    /// unlike the target <see cref="MachineArchitecture"/> passed to copype.cmd, which just
    /// selects which WinPE/boot-sector DATA files get staged. Callers must use this, not the
    /// selected target architecture, whenever resolving a path to one of those executables.
    /// </summary>
    public static MachineArchitecture HostArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => MachineArchitecture.X64,
        Architecture.Arm64 => MachineArchitecture.Arm64,
        var other => throw new PlatformNotSupportedException($"Unsupported host processor architecture: {other}."),
    };
}

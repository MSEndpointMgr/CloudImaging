using System.Text.Json;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>Architecture handling shared by the boot, OS and recovery image upload paths.</summary>
public static class ImageArchitecture
{
    /// <summary>
    /// Reads the optional <c>architecture</c> request property. Missing/null is valid (legacy
    /// callers); anything other than "x64"/"arm64" returns false so it is rejected, not defaulted.
    /// </summary>
    public static bool TryReadRequested(JsonElement body, out MachineArchitecture? requested)
    {
        requested = null;
        if (!body.TryGetProperty("architecture", out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String && MachineArchitecturePlatform.TryParseSlug(prop.GetString(), out var parsed))
        {
            requested = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reconciles the WIM's own recorded architecture (authoritative when present) with the
    /// operator's selection. Returns a rejection message for x86 images or a contradicting selection.
    /// </summary>
    public static string? Resolve(int? detectedRaw, MachineArchitecture? requested, string imageKind, out MachineArchitecture? resolved)
    {
        resolved = requested;
        if (detectedRaw is not { } raw)
        {
            return null;
        }

        var detected = WimMetadataReader.ToMachineArchitecture(raw);
        if (detected is null)
        {
            return $"This {imageKind} targets {WimMetadataReader.DescribeProcessorArchitecture(raw)}, which is not supported. Only x64 and arm64 images can be published.";
        }

        if (requested is { } selected && selected != detected)
        {
            return $"The selected architecture ({MachineArchitecturePlatform.Slug(selected)}) does not match the {imageKind}, which targets {MachineArchitecturePlatform.Slug(detected.Value)}.";
        }

        resolved = detected;
        return null;
    }

    /// <summary>Error message used when a requested architecture string is neither "x64" nor "arm64".</summary>
    public const string InvalidArchitectureMessage = "architecture must be \"x64\" or \"arm64\".";
}

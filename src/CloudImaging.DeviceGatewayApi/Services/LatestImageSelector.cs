using System.Text.Json;

namespace CloudImaging.DeviceGatewayApi.Services;

/// <summary>Per-architecture "latest published" selection over an Imaging Core catalog listing.</summary>
public static class LatestImageSelector
{
    /// <summary>Returns true and the requested slug when valid; missing means x64 (older Clients never send it).</summary>
    public static bool TryReadArchitecture(string? query, out string architecture)
    {
        architecture = query ?? "x64";
        return architecture is "x64" or "arm64";
    }

    /// <summary>Returns true and the requested base; missing means WindowsPE (Clients that predate Windows RE never send it).</summary>
    public static bool TryReadBase(string? query, out string bootImageBase)
    {
        bootImageBase = string.IsNullOrEmpty(query) ? "WindowsPE" : query;
        return bootImageBase is "WindowsPE" or "WindowsRE";
    }

    /// <summary>
    /// First entry flagged latest for <paramref name="architecture"/> and <paramref name="bootImageBase"/>.
    /// An entry with no "architecture" reads as x64, and one with no "base" as WindowsPE.
    /// </summary>
    public static JsonElement? FindLatestForArchitecture(JsonElement catalog, string architecture, string bootImageBase = "WindowsPE")
    {
        foreach (var image in catalog.EnumerateArray())
        {
            var isLatest = image.TryGetProperty("isLatestPublished", out var flag) && flag.GetBoolean();
            var imageArchitecture = image.TryGetProperty("architecture", out var archProp) && archProp.GetString() == "arm64" ? "arm64" : "x64";
            var imageBase = image.TryGetProperty("base", out var baseProp) && baseProp.GetString() == "WindowsRE" ? "WindowsRE" : "WindowsPE";
            if (isLatest && imageArchitecture == architecture && imageBase == bootImageBase)
            {
                return image;
            }
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="bootImageId"/> is an active catalog entry still in pre-production.
    /// Entries without "isProduction" predate the pre-production stage and are production.
    /// </summary>
    public static bool IsPreProduction(JsonElement catalog, Guid? bootImageId)
    {
        if (bootImageId is null)
        {
            return false;
        }

        foreach (var image in catalog.EnumerateArray())
        {
            if (image.TryGetProperty("bootImageId", out var id) && id.TryGetGuid(out var guid) && guid == bootImageId)
            {
                return image.TryGetProperty("isProduction", out var production) && production.ValueKind == JsonValueKind.False;
            }
        }
        return false;
    }
}

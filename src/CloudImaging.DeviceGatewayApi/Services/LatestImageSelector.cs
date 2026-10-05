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

    /// <summary>
    /// First entry flagged latest for <paramref name="architecture"/>. An entry with no
    /// "architecture" property predates architecture tracking and reads as x64.
    /// </summary>
    public static JsonElement? FindLatestForArchitecture(JsonElement catalog, string architecture)
    {
        foreach (var image in catalog.EnumerateArray())
        {
            var isLatest = image.TryGetProperty("isLatestPublished", out var flag) && flag.GetBoolean();
            var imageArchitecture = image.TryGetProperty("architecture", out var archProp) && archProp.GetString() == "arm64" ? "arm64" : "x64";
            if (isLatest && imageArchitecture == architecture)
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

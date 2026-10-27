using System.Text.Json;
using CloudImaging.Contracts.Models.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// The time zones offered for the sync schedule (research R32): one entry per Windows time zone,
/// labelled with its Windows display name and stored as its IANA name. Read once from the embedded
/// list; entries the host's <see cref="TimeZoneInfo"/> cannot resolve are left out.
/// </summary>
public static class DriverTimeZoneCatalog
{
    /// <summary>Stored zone when none is chosen.</summary>
    public const string DefaultZone = "Etc/UTC";

    private const string ResourceName = "CloudImaging.ImagingCoreApi.Data.time-zones.json";

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<(IReadOnlyList<DriverTimeZone> All, IReadOnlyList<DriverTimeZone> Offered, Dictionary<string, TimeZoneInfo> Resolved)> Loaded = new(Load);

    /// <summary>Every entry in the embedded list, in the Windows order.</summary>
    public static IReadOnlyList<DriverTimeZone> All => Loaded.Value.All;

    /// <summary>Entries this host resolves, in the Windows order.</summary>
    public static IReadOnlyList<DriverTimeZone> Offered => Loaded.Value.Offered;

    /// <summary>The zone of an offered <c>ianaId</c>; false for anything else.</summary>
    public static bool TryGet(string? ianaId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        return ianaId is not null && Loaded.Value.Resolved.TryGetValue(ianaId, out zone!);
    }

    private static (IReadOnlyList<DriverTimeZone>, IReadOnlyList<DriverTimeZone>, Dictionary<string, TimeZoneInfo>) Load()
    {
        using var stream = typeof(DriverTimeZoneCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded time zone list is missing.");
        var file = JsonSerializer.Deserialize<TimeZoneFile>(stream, FileOptions)
            ?? throw new InvalidOperationException("The embedded time zone list is empty.");
        var all = file.Zones.Select(z => new DriverTimeZone { IanaId = z.IanaId, DisplayName = z.DisplayName, Covers = z.Covers }).ToList();
        var resolved = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        foreach (var zone in all)
        {
            try
            {
                resolved[zone.IanaId] = TimeZoneInfo.FindSystemTimeZoneById(zone.IanaId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Not offered on this host.
            }
        }

        return (all, all.Where(z => resolved.ContainsKey(z.IanaId)).ToList(), resolved);
    }

    private sealed record TimeZoneFile(List<TimeZoneEntry> Zones);

    private sealed record TimeZoneEntry(string IanaId, string DisplayName, List<string> Covers);
}

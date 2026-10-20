namespace CloudImaging.Contracts.Models;

/// <summary>
/// Admin-managed location label (e.g. "Seattle HQ", "Chicago Warehouse — Dock 3"). Locations are
/// selected by a technician in Media Builder when preparing USB boot media and are carried
/// through the manifest → device registration → session so the portal can show/filter devices by
/// the site they were imaged at. Locations are plain metadata — not a security or tenancy
/// boundary — so deleting one does not retroactively affect sessions already tagged with its
/// (denormalized) name.
/// </summary>
public sealed class Location
{
    /// <summary>Identifier for this location.</summary>
    public required Guid LocationId { get; init; }

    /// <summary>Display name shown in the portal and Media Builder.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Region typed before regions came from countries. Kept until the location is saved again;
    /// use <see cref="RegionConfiguration.EffectiveRegion"/> for the region a location resolves to.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>ISO-3166 alpha-2 country code (e.g. SE). Determines the location's region.</summary>
    public string? CountryCode { get; init; }

    /// <summary>When the location was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

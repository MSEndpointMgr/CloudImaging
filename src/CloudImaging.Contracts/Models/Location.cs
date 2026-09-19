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
    /// <summary>Unique identifier of the location.</summary>
    public required Guid LocationId { get; init; }

    /// <summary>Display name of the location (e.g. "Seattle HQ").</summary>
    public required string Name { get; init; }

    /// <summary>UTC timestamp when the location was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

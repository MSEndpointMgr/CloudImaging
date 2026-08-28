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
    public required Guid LocationId { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

namespace CloudImaging.Contracts.Models;

/// <summary>
/// A portal user's preferred location, keyed by their Entra ID object id (oid). Backs the
/// per-technician "my location" filter on the Sessions page and the default location shown to
/// them in Media Builder. Neither the Operator API nor Imaging Core API otherwise have any
/// concept of "the signed-in portal user" — the userId is passed explicitly as a plain request
/// parameter, the same way sessionId/imageId are today.
/// </summary>
public sealed class UserLocationPreference
{
    public required string UserId { get; init; }
    public Guid? LocationId { get; init; }
    public string? LocationName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

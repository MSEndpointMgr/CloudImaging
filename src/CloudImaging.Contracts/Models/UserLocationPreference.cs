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
    /// <summary>Entra ID object id (oid) of the portal user the preference belongs to.</summary>
    public required string UserId { get; init; }

    /// <summary>Preferred location catalog entry id, if set.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Denormalized name of the preferred location, for display.</summary>
    public string? LocationName { get; init; }

    /// <summary>UTC timestamp when the preference was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

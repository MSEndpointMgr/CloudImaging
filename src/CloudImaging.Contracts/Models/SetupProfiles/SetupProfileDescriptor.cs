using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.SetupProfiles;

/// <summary>
/// The setup profile version pinned on a session, as returned to the device. The content itself is
/// downloaded from <see cref="ContentUrl"/> and checked against <see cref="ContentSha256"/>.
/// </summary>
public sealed record SetupProfileDescriptor
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name when the session was assigned.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Pinned version.</summary>
    public required int Version { get; init; }

    /// <summary>Failure handling of the pinned version.</summary>
    public SetupFailureHandling FailureHandling { get; init; }

    /// <summary>Short-lived read-only URL of the version document. Never logged.</summary>
    public required string ContentUrl { get; init; }

    /// <summary>When <see cref="ContentUrl"/> expires.</summary>
    public DateTimeOffset ContentUrlExpiresAt { get; init; }

    /// <summary>Lowercase hex SHA-256 of the version document.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Size of the version document in bytes.</summary>
    public long ContentSizeBytes { get; init; }
}

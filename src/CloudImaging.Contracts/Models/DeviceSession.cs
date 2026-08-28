using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Represents a single device imaging session (FR-021, Key Entities).
/// This is the canonical DTO; persistence models in ImagingCoreApi extend it.
/// </summary>
public sealed class DeviceSession
{
    public required Guid SessionId { get; init; }
    public required SessionState State { get; init; }

    // Device identity (captured at registration)
    public required string DeviceSerialNumber { get; init; }
    public required string DeviceManufacturer { get; init; }
    public required string DeviceModel { get; init; }
    public DeviceHardwareMetadata? HardwareMetadata { get; init; }

    /// <summary>
    /// Location the device was registered from, carried from the USB boot media's preparation
    /// manifest (Media Builder) through the registration payload. Null when unspecified.
    /// </summary>
    public Guid? LocationId { get; init; }

    /// <summary>
    /// Denormalized location name captured at session-creation time — stored (not just the id)
    /// so historical sessions still display a location after the catalog entry is deleted.
    /// </summary>
    public string? LocationName { get; init; }

    // Pre-flight result
    public PreFlightAuthorizationResult PreFlightAuthorizationResult { get; init; }

    // Passcode — stored as hash at rest; returned only at SessionInit
    public string? Passcode { get; init; }
    public DateTimeOffset? PasscodeExpiresAt { get; init; }
    public bool PasscodeConsumed { get; init; }

    // Device-session token
    public string? DeviceSessionToken { get; init; }
    public DateTimeOffset? DeviceSessionTokenExpiresAt { get; init; }

    // Assignment
    public Guid? AssignedOsImageId { get; init; }
    public string? SasTokenUrl { get; init; }
    public DateTimeOffset? SasTokenUrlExpiresAt { get; init; }

    /// <summary>
    /// The <see cref="PartitioningScheme"/> in effect at session-creation time, serialized to
    /// JSON. Locked at creation so later admin edits to the global scheme do not affect sessions
    /// already in progress.
    /// </summary>
    public string? PartitioningSchemeSnapshotJson { get; init; }

    // Progress
    public int OverallProgressPercent { get; init; }
    public string? CurrentStep { get; init; }
    public IReadOnlyList<ImagingStep> Steps { get; init; } = [];

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    public DateTimeOffset? PurgeAt { get; init; }
}

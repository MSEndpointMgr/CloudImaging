using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Represents a single device imaging session (FR-021, Key Entities).
/// This is the canonical DTO; persistence models in ImagingCoreApi extend it.
/// </summary>
/// <remarks>
/// A <c>record</c> specifically so state transitions can be written as <c>session with { ... }</c>.
/// This used to be a class, which forced every transition to re-list all ~25 properties by hand;
/// each omission silently dropped data with no compiler error, and several had accumulated
/// (location, hardware metadata and the partitioning snapshot were all being lost on transition).
/// Do not convert it back.
/// </remarks>
public sealed record DeviceSession
{
    public required Guid SessionId { get; init; }
    public required SessionState State { get; init; }

    // Device identity (captured at registration)
    public required string DeviceSerialNumber { get; init; }
    public required string DeviceManufacturer { get; init; }
    public required string DeviceModel { get; init; }

    /// <summary>
    /// MAC address of the first non-loopback adapter that was up at registration — informational,
    /// used by operators to correlate a device with network/DHCP records. Carried from
    /// <see cref="DeviceRegistrationPayload.MacAddress"/>; the full per-adapter list lives in
    /// <see cref="HardwareMetadata"/>.
    /// </summary>
    public string? MacAddress { get; init; }

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

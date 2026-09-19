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
    /// <summary>Unique identifier of the session.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Current lifecycle state of the session.</summary>
    public required SessionState State { get; init; }

    // Device identity (captured at registration)

    /// <summary>Device serial number as reported by the hardware at registration.</summary>
    public required string DeviceSerialNumber { get; init; }

    /// <summary>Device manufacturer as reported by the hardware at registration.</summary>
    public required string DeviceManufacturer { get; init; }

    /// <summary>Device model as reported by the hardware at registration.</summary>
    public required string DeviceModel { get; init; }

    /// <summary>
    /// MAC address of the first non-loopback adapter that was up at registration — informational,
    /// used by operators to correlate a device with network/DHCP records. Carried from
    /// <see cref="DeviceRegistrationPayload.MacAddress"/>; the full per-adapter list lives in
    /// <see cref="HardwareMetadata"/>.
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>Detailed hardware metadata collected silently for audit purposes (FR-001a).</summary>
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

    /// <summary>Outcome of the device pre-flight authorization check, if enabled.</summary>
    public PreFlightAuthorizationResult PreFlightAuthorizationResult { get; init; }

    // Passcode — stored as hash at rest; returned only at SessionInit

    /// <summary>Operator passcode (hash at rest), returned to the device only at SessionInit.</summary>
    public string? Passcode { get; init; }

    /// <summary>UTC expiry of the current passcode.</summary>
    public DateTimeOffset? PasscodeExpiresAt { get; init; }

    /// <summary>True once the passcode has been redeemed by the device.</summary>
    public bool PasscodeConsumed { get; init; }

    // Device-session token

    /// <summary>Long-lived bearer token issued after passcode authorization.</summary>
    public string? DeviceSessionToken { get; init; }

    /// <summary>UTC expiry of the device-session token.</summary>
    public DateTimeOffset? DeviceSessionTokenExpiresAt { get; init; }

    // Assignment

    /// <summary>OS image catalog entry assigned to this session, if any.</summary>
    public Guid? AssignedOsImageId { get; init; }

    /// <summary>Time-limited SAS URL for downloading the assigned OS image.</summary>
    public string? SasTokenUrl { get; init; }

    /// <summary>UTC expiry of the SAS download URL.</summary>
    public DateTimeOffset? SasTokenUrlExpiresAt { get; init; }

    /// <summary>
    /// The <see cref="PartitioningScheme"/> in effect at session-creation time, serialized to
    /// JSON. Locked at creation so later admin edits to the global scheme do not affect sessions
    /// already in progress.
    /// </summary>
    public string? PartitioningSchemeSnapshotJson { get; init; }

    // Progress

    /// <summary>Aggregate imaging progress, 0-100.</summary>
    public int OverallProgressPercent { get; init; }

    /// <summary>Name of the step currently executing, if any.</summary>
    public string? CurrentStep { get; init; }

    /// <summary>Per-step progress detail for the current pipeline.</summary>
    public IReadOnlyList<ImagingStep> Steps { get; init; } = [];

    // Timestamps

    /// <summary>UTC timestamp when the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp of the last heartbeat received from the device.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>UTC timestamp of the terminal state transition, if the session has finished.</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>UTC timestamp after which the record may be purged, if terminal.</summary>
    public DateTimeOffset? PurgeAt { get; init; }
}

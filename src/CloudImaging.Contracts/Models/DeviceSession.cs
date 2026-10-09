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
    /// <summary>Identifier for this session.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Current lifecycle state of the session.</summary>
    public required SessionState State { get; init; }

    // Device identity (captured at registration)
    /// <summary>Device serial number.</summary>
    public required string DeviceSerialNumber { get; init; }

    /// <summary>Device manufacturer, as reported by firmware.</summary>
    public required string DeviceManufacturer { get; init; }

    /// <summary>Device model, as reported by firmware.</summary>
    public required string DeviceModel { get; init; }

    /// <summary>
    /// MAC address of the first non-loopback adapter that was up at registration — informational,
    /// used by operators to correlate a device with network/DHCP records. Carried from
    /// <see cref="DeviceRegistrationPayload.MacAddress"/>; the full per-adapter list lives in
    /// <see cref="HardwareMetadata"/>.
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>Hardware metadata the Client reported at registration.</summary>
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

    /// <summary>Device processor architecture reported at registration; x64 for sessions that predate it.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;

    // Pre-flight result
    /// <summary>Outcome of pre-flight authorization for this session.</summary>
    public PreFlightAuthorizationResult PreFlightAuthorizationResult { get; init; }

    /// <summary>Firmware security state the Client reported; null for Clients that predate it.</summary>
    public DeviceSecurityPosture? SecurityPosture { get; init; }

    /// <summary>Every pre-flight check as evaluated at creation. Never re-evaluated afterwards.</summary>
    public IReadOnlyList<PreFlightCheckResult> PreFlightChecks { get; init; } = [];

    // Passcode — stored as hash at rest; returned only at SessionInit
    /// <summary>The coupling passcode, returned to the device only at <see cref="SessionState.SessionInit"/>.</summary>
    public string? Passcode { get; init; }

    /// <summary>When the passcode expires if not consumed.</summary>
    public DateTimeOffset? PasscodeExpiresAt { get; init; }

    /// <summary>True once a technician has coupled the session with this passcode.</summary>
    public bool PasscodeConsumed { get; init; }

    // Device-session token
    /// <summary>Bearer token the device uses to authenticate subsequent session calls.</summary>
    public string? DeviceSessionToken { get; init; }

    /// <summary>When <see cref="DeviceSessionToken"/> expires.</summary>
    public DateTimeOffset? DeviceSessionTokenExpiresAt { get; init; }

    // Assignment
    /// <summary>OS image assigned to this session, if any.</summary>
    public Guid? AssignedOsImageId { get; init; }

    /// <summary>SAS URL the device downloads the assigned OS image from.</summary>
    public string? SasTokenUrl { get; init; }

    /// <summary>When <see cref="SasTokenUrl"/> expires.</summary>
    public DateTimeOffset? SasTokenUrlExpiresAt { get; init; }

    /// <summary>
    /// The <see cref="PartitioningScheme"/> in effect at session-creation time, serialized to
    /// JSON. Locked at creation so later admin edits to the global scheme do not affect sessions
    /// already in progress.
    /// </summary>
    public string? PartitioningSchemeSnapshotJson { get; init; }

    // Progress
    /// <summary>Overall imaging progress, 0-100.</summary>
    public int OverallProgressPercent { get; init; }

    /// <summary>Name of the imaging step currently in progress, or null when none has started.</summary>
    public string? CurrentStep { get; init; }

    /// <summary>Status of every imaging step.</summary>
    public IReadOnlyList<ImagingStep> Steps { get; init; } = [];

    // Timestamps
    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the device last reported a heartbeat.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the session reached a terminal state.</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>When the session record is eligible for lifecycle cleanup.</summary>
    public DateTimeOffset? PurgeAt { get; init; }
}

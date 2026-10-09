using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Durable audit record of a device imaging session's final outcome, written once at the
/// moment a <see cref="DeviceSession"/> transitions to a terminal state — before the live
/// session record is purged from the "DeviceSessions" table (Reports feature).
///
/// Unlike <see cref="DeviceSession"/>, records in this table are retained for reporting
/// purposes according to <see cref="PortalConfiguration.SessionHistoryRetentionDays"/> and are
/// not tied to the live session's own (much shorter) purge lifecycle.
/// </summary>
public sealed class SessionHistoryRecord
{
    /// <summary>Identifier of the session this record is the outcome of.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The terminal <see cref="SessionState"/> the session ended in.</summary>
    public required SessionState FinalState { get; init; }

    /// <summary>Device serial number.</summary>
    public required string DeviceSerialNumber { get; init; }

    /// <summary>Device manufacturer, as reported by firmware.</summary>
    public required string DeviceManufacturer { get; init; }

    /// <summary>Device model, as reported by firmware.</summary>
    public required string DeviceModel { get; init; }

    /// <summary>Location snapshot retained even if the catalog entry is later renamed or deleted.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Display name of <see cref="LocationId"/>, captured at session-creation time.</summary>
    public string? LocationName { get; init; }

    /// <summary>Outcome of pre-flight authorization for this session.</summary>
    public PreFlightAuthorizationResult PreFlightAuthorizationResult { get; init; }

    /// <summary>Pre-flight checks as evaluated at session creation, including administrator approvals.</summary>
    public IReadOnlyList<PreFlightCheckResult> PreFlightChecks { get; init; } = [];

    /// <summary>Device processor architecture; x64 for records that predate it.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;

    /// <summary>OS image that was assigned to the session, if any.</summary>
    public Guid? AssignedOsImageId { get; init; }

    /// <summary>The step that was in progress when the session failed, if applicable.</summary>
    public ImagingStepName? FailedStepName { get; init; }

    /// <summary>Error detail captured from the failed step, if applicable.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>When the session was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the session reached <see cref="FinalState"/>.</summary>
    public required DateTimeOffset TerminalAt { get; init; }

    /// <summary>Session duration from creation to terminal transition.</summary>
    public TimeSpan Duration => TerminalAt - CreatedAt;
}

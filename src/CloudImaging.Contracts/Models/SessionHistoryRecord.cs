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
    public required Guid SessionId { get; init; }

    /// <summary>The terminal <see cref="SessionState"/> the session ended in.</summary>
    public required SessionState FinalState { get; init; }

    public required string DeviceSerialNumber { get; init; }
    public required string DeviceManufacturer { get; init; }
    public required string DeviceModel { get; init; }

    public PreFlightAuthorizationResult PreFlightAuthorizationResult { get; init; }

    public Guid? AssignedOsImageId { get; init; }

    /// <summary>The step that was in progress when the session failed, if applicable.</summary>
    public ImagingStepName? FailedStepName { get; init; }

    /// <summary>Error detail captured from the failed step, if applicable.</summary>
    public string? ErrorDetail { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset TerminalAt { get; init; }

    /// <summary>Session duration from creation to terminal transition.</summary>
    public TimeSpan Duration => TerminalAt - CreatedAt;
}

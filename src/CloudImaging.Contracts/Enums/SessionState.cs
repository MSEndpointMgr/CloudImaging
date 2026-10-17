using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Session lifecycle states (FR-021).</summary>
/// <remarks>
/// Serialized as its name, not its ordinal: every session DTO hand-built by the Imaging Core API
/// writes this value with <c>.ToString()</c>, and the portal matches it against names such as
/// "SessionCompleted". Without this converter a model serialized directly (rather than through a
/// hand-built DTO), such as <see cref="Models.SessionHistoryRecord"/> on the session-history
/// endpoint, would emit an ordinal instead and silently break every consumer.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionState
{
    /// <summary>Registration and pre-flight authorization are being evaluated.</summary>
    SessionInit,

    /// <summary>The device may be imaged and is waiting for its passcode to be coupled.</summary>
    SessionAllowed,

    /// <summary>The passcode was consumed; the device is coupled but has no OS image yet.</summary>
    SessionAssigned,

    /// <summary>A technician assigned an active OS image and Imaging Core issued its download URL.</summary>
    SessionStarted,

    /// <summary>At least one imaging step has started.</summary>
    SessionInProgress,

    /// <summary>All imaging stages completed successfully. Terminal.</summary>
    SessionCompleted,

    /// <summary>A stage failed, or a coupled session timed out. Terminal.</summary>
    SessionFailed,

    /// <summary>The device failed a required pre-flight check. Terminal.</summary>
    SessionNotAuthorized,

    /// <summary>
    /// Terminal state for a session that was never coupled (still <see cref="SessionInit"/> or
    /// <see cref="SessionAllowed"/>) when the inactivity timeout elapsed — e.g. the operator
    /// never entered the passcode before it expired. Distinct from <see cref="SessionFailed"/>,
    /// which is reserved for sessions that failed after an operator had already coupled the
    /// device (a real, diagnosable failure). SessionExpired is a benign timeout, not a failure,
    /// so it must never surface in the portal's Monitor tab or count as a failed device.
    /// </summary>
    SessionExpired
}

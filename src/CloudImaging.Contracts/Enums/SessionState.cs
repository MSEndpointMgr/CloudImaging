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
    /// <summary>Session created on registration; awaiting operator passcode entry.</summary>
    SessionInit,

    /// <summary>Device authorized by passcode; an OS image can be assigned.</summary>
    SessionAllowed,

    /// <summary>An OS image has been assigned to the session.</summary>
    SessionAssigned,

    /// <summary>Device started the imaging pipeline.</summary>
    SessionStarted,

    /// <summary>Imaging pipeline is actively progressing.</summary>
    SessionInProgress,

    /// <summary>Imaging completed successfully; this is a terminal state.</summary>
    SessionCompleted,

    /// <summary>Imaging failed after coupling; this is a terminal state.</summary>
    SessionFailed,

    /// <summary>Device was not authorized and the session was rejected.</summary>
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

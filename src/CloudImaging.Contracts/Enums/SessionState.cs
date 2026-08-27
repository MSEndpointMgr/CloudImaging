namespace CloudImaging.Contracts.Enums;

/// <summary>Session lifecycle states (FR-021).</summary>
public enum SessionState
{
    SessionInit,
    SessionAllowed,
    SessionAssigned,
    SessionStarted,
    SessionInProgress,
    SessionCompleted,
    SessionFailed,
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

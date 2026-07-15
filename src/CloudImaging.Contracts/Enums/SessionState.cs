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
    SessionNotAuthorized
}

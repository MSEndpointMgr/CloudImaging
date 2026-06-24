# Device Session Initialization and Polling

Device bootstrap, session registration, passcode display, and polling for assignment.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant ClientWinPE as Cloud Imaging<br/>Client (WinPE)
    participant DeviceGW as Device Gateway<br/>API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Table<br/>Storage

    Tech->>ClientWinPE: Boot device with WinPE (from USB)
    ClientWinPE->>ClientWinPE: Launch Cloud Imaging Client
    ClientWinPE->>ClientWinPE: Read branding logo from executable directory (FR-002a)
    
    ClientWinPE->>DeviceGW: POST /api/v1/sessions (register)
    Note over DeviceGW: Unauthenticated request (public endpoint)
    DeviceGW->>ImagingCore: Create session record + pre-flight authorization
    ImagingCore->>Storage: Persist SessionInit record
    Note over ImagingCore: State: SessionInit<br/>Pre-flight: Graph query vs Autopilot V1 + Corporate Identifiers
    
    alt Pre-flight authorized (or pre-flight disabled)
        ImagingCore->>Storage: Transition to SessionAllowed
        ImagingCore-->>DeviceGW: session-id + device-session token + passcode
        DeviceGW-->>ClientWinPE: device-session token + passcode
        ClientWinPE->>ClientWinPE: Display session ID and passcode (SessionInitView)
    else Pre-flight not authorized (no match in Autopilot or Corporate Identifiers)
        ImagingCore->>Storage: Transition to SessionNotAuthorized (terminal)
        ImagingCore-->>DeviceGW: SessionNotAuthorized
        DeviceGW-->>ClientWinPE: SessionNotAuthorized
        ClientWinPE->>ClientWinPE: Immediate transition to ResultsView<br/>Not Authorized outcome (FR-003a)
        Note over ClientWinPE: Display device serial number + enrollment guidance<br/>No further polling
    end
    Tech->>Tech: Read passcode from device
    
    loop Every 30 seconds (while awaiting assignment)
        ClientWinPE->>DeviceGW: GET /api/v1/sessions/{session-id}/status<br/>(device-session token)
        Note over DeviceGW: Validate token + session-id
        DeviceGW->>ImagingCore: Fetch session state
        ImagingCore->>Storage: Query session record
        
        alt Session state = SessionAllowed (awaiting operator coupling)
            Storage-->>ImagingCore: Session record
            Note over ImagingCore: State: SessionAllowed<br/>Waiting for operator to couple and assign
            ImagingCore-->>DeviceGW: Session state + currentStep (null) + overallProgressPercent (0)
            DeviceGW-->>ClientWinPE: awaiting coupling
        else Session state = SessionAssigned
            Storage-->>ImagingCore: Session + image assignment
            Note over ImagingCore: State: SessionAssigned<br/>Image: {image-id}<br/>SHA256Hash: {hash}<br/>SAS token URL + expiry
            ImagingCore-->>DeviceGW: Session state + SAS token URL + hash + currentStep + overallProgressPercent
            DeviceGW-->>ClientWinPE: image-id + SAS token URL + sha256Hash + currentStep + overallProgressPercent
            Note over ClientWinPE: Use hash for pre-download cache validation
        end
    end
```

## Session State Transitions

| State | Condition | Next State |
|-------|-----------|-----------|
| SessionInit | Session registered; pre-flight authorization runs immediately | SessionAllowed (match or pre-flight disabled) / SessionNotAuthorized (no match) |
| SessionNotAuthorized | Device not in Autopilot V1 or Corporate Identifiers | Terminal -- no further polling; ResultsView Not Authorized shown |
| SessionAllowed | Pre-flight passed; awaiting operator coupling and image assignment | SessionAssigned |
| SessionAssigned | Image assigned, SAS issued | SessionStarted (on next client poll) |
| SessionStarted | Client detected assignment, requests details | SessionInProgress |
| SessionInProgress | Client formatting, downloading, applying | SessionCompleted / SessionFailed |
| SessionCompleted | Imaging done, device reboots | Terminal (purged after 24h) |
| SessionFailed | Unrecoverable error during imaging | Terminal (purged after 24h) |

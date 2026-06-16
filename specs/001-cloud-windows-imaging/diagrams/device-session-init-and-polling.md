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
    
    ClientWinPE->>DeviceGW: POST /sessions/register
    Note over DeviceGW: Unauthenticated request
    DeviceGW->>ImagingCore: Create session record
    ImagingCore->>Storage: Persist SessionInit record
    Note over ImagingCore: State: SessionInit<br/>Passcode: randomly generated
    
    ImagingCore-->>DeviceGW: session-id + device-session token
    DeviceGW-->>ClientWinPE: device-session token
    ClientWinPE->>ClientWinPE: Display passcode on screen
    Tech->>Tech: Read passcode from device
    
    loop Every 30 seconds
        ClientWinPE->>DeviceGW: GET /sessions/{session-id}/status<br/>(device-session token)
        Note over DeviceGW: Validate token + session-id
        DeviceGW->>ImagingCore: Fetch session state
        ImagingCore->>Storage: Query session record
        
        alt Session state = SessionAllowed
            Storage-->>ImagingCore: Session record
            Note over ImagingCore: State: SessionAllowed<br/>Pre-flight checks passed
            ImagingCore-->>DeviceGW: Session state
            DeviceGW-->>ClientWinPE: ready for assignment
        else Session state = SessionAssigned
            Storage-->>ImagingCore: Session + image assignment
            Note over ImagingCore: State: SessionAssigned<br/>Image: OS-image-v2<br/>SHA256Hash: {hash}<br/>SAS URL + expiry
            ImagingCore-->>DeviceGW: Session state + SAS + hash
            DeviceGW-->>ClientWinPE: image-id + SAS URL + sha256Hash
            Note over ClientWinPE: Use hash for pre-download cache validation
        else Session not yet coupled
            Storage-->>ImagingCore: Session record
            Note over ImagingCore: State: SessionInit<br/>Waiting for technician
            ImagingCore-->>DeviceGW: awaiting assignment
            DeviceGW-->>ClientWinPE: awaiting assignment
        end
    end
```

## Session State Transitions

| State | Condition | Next State |
|-------|-----------|-----------|
| SessionInit | Session registered, awaiting passcode | SessionAllowed (after couple) |
| SessionAllowed | Pre-flight checks passed | SessionAssigned (after image select) |
| SessionAssigned | Image assigned, SAS issued | SessionInProgress (on client poll) |
| SessionInProgress | Client downloading/applying | SessionCompleted (on finish) |
| SessionCompleted | Imaging done, device reboots | (end) |

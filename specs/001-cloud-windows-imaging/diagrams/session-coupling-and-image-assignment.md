# Session Coupling and Image Assignment

Technician enters passcode in portal, portal couples the session via Operator API, and assigns an OS image for device download.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant Portal as Cloud Imaging<br/>Portal (frontend)
    participant PortalBackend as Cloud Imaging<br/>Portal (backend)
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Table<br/>Storage

    Tech->>Portal: Enter passcode in portal UI
    Portal->>PortalBackend: POST /sessions/couple<br/>(passcode)
    Note over PortalBackend: Entra ID authenticated
    
    PortalBackend->>OperatorAPI: POST /sessions/couple<br/>(Entra ID token + passcode)
    Note over OperatorAPI: Validate app roles
    OperatorAPI->>ImagingCore: Couple session by passcode
    ImagingCore->>Storage: Query session by passcode
    
    alt Passcode matches
        Storage-->>ImagingCore: Session record (session-id)
        ImagingCore->>Storage: Update session state = SessionAllowed
        Note over ImagingCore: Pre-flight checks passed<br/>Session now allowed
        ImagingCore-->>OperatorAPI: session-id
        OperatorAPI-->>PortalBackend: session-id + status
        PortalBackend-->>Portal: session-id
        Portal->>Tech: Display "Session coupled"
    else Passcode not found
        Storage-->>ImagingCore: No match
        ImagingCore-->>OperatorAPI: Error: passcode not found
        OperatorAPI-->>PortalBackend: Error (4xx)
        PortalBackend-->>Portal: Error message
        Portal->>Tech: Display "Invalid passcode"
    end
    
    Tech->>Portal: Select OS image to assign
    Portal->>PortalBackend: PUT /sessions/{session-id}/assign<br/>(image-id)
    Note over PortalBackend: Entra ID authenticated
    
    PortalBackend->>OperatorAPI: PUT /sessions/{session-id}/assign<br/>(Entra ID token + image-id)
    OperatorAPI->>ImagingCore: Assign image to session
    ImagingCore->>Storage: Fetch OS image metadata
    
    alt Image exists and is valid
        Storage-->>ImagingCore: Image record
        ImagingCore->>Storage: Query storage for image blob
        Storage-->>ImagingCore: Blob location confirmed
        ImagingCore->>ImagingCore: Generate SAS URL (15 min expiry)
        ImagingCore->>Storage: Update session state = SessionAssigned
        Note over ImagingCore: State: SessionAssigned<br/>Image: {image-id}<br/>SAS: {URL}<br/>Assigned by: {user}
        ImagingCore-->>OperatorAPI: assignment-id + SAS URL
        OperatorAPI-->>PortalBackend: success
        PortalBackend-->>Portal: assignment confirmed
        Portal->>Tech: Display "Image assigned, device will download on next poll"
    else Image not found
        Storage-->>ImagingCore: Image not found
        ImagingCore-->>OperatorAPI: Error: image not found
        OperatorAPI-->>PortalBackend: Error (4xx)
        PortalBackend-->>Portal: Error message
        Portal->>Tech: Display "Image not available"
    end
```

## Authorization Checks

| Operation | Role Required | Check |
|-----------|---|---|
| Couple session | Technician or Admin | Passcode must be valid |
| View uncoupled sessions | Technician or Admin | Must have session query scope |
| Assign image | Technician or Admin | Image must exist and be published |
| Manage images | Admin only | Requires admin app role |

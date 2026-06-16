# Bulk Assignment and Dashboard Updates

Portal bulk-selects sessions, assigns one OS image to multiple devices, and displays per-device progress in real-time.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant Portal as Cloud Imaging<br/>Portal (frontend)
    participant PortalBackend as Cloud Imaging<br/>Portal (backend)
    participant DeviceGW as Device Gateway API
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Table<br/>Storage
    participant Client1 as Cloud Imaging<br/>Client 1 (WinPE)
    participant Client2 as Cloud Imaging<br/>Client 2 (WinPE)
    participant ClientN as Cloud Imaging<br/>Client N (WinPE)

    Tech->>Portal: Select multiple coupled sessions
    Portal->>PortalBackend: PUT /sessions/bulk-assign<br/>(session-ids, image-id)
    Note over PortalBackend: Entra ID authenticated
    
    PortalBackend->>OperatorAPI: PUT /sessions/bulk-assign<br/>(Entra ID token, session list, image-id)
    OperatorAPI->>ImagingCore: Validate image exists
    
    loop For each session in list
        ImagingCore->>ImagingCore: Generate unique SAS for session
        ImagingCore->>Storage: Update session state = SessionAssigned
        Note over Storage: State: SessionAssigned<br/>Image: {image-id}<br/>SAS: {URL}
    end
    
    ImagingCore-->>OperatorAPI: bulk-assignment-id
    OperatorAPI-->>PortalBackend: success, count = N
    PortalBackend-->>Portal: 'N sessions assigned'
    Portal->>Tech: Display "Assigned to 3 devices"
    
    Tech->>Portal: Open session progress dashboard
    Portal->>PortalBackend: GET /sessions/progress?bulk-id={id}
    PortalBackend->>OperatorAPI: GET /sessions with filter
    OperatorAPI->>ImagingCore: Fetch all session states
    ImagingCore->>Storage: Query latest session records
    Storage-->>ImagingCore: Session records with state/progress
    ImagingCore-->>OperatorAPI: session list
    OperatorAPI-->>PortalBackend: session list
    PortalBackend-->>Portal: JSON with per-device state
    Portal->>Tech: Display progress: (3 downloading, 0 applied, 0 completed)
    
    par Client 1 Polling
        loop Every 30 sec
            Client1->>DeviceGW: GET /sessions/{id}/status
            DeviceGW->>ImagingCore: GET /api/internal/sessions/{id}/status
            ImagingCore-->>DeviceGW: state + SAS + image-id
            DeviceGW-->>Client1: state + SAS + image-id
            Client1->>Client1: Download...
        end
    and Client 2 Polling
        loop Every 30 sec
            Client2->>DeviceGW: GET /sessions/{id}/status
            DeviceGW->>ImagingCore: GET /api/internal/sessions/{id}/status
            ImagingCore-->>DeviceGW: state + SAS + image-id
            DeviceGW-->>Client2: state + SAS + image-id
            Client2->>Client2: Download...
        end
    and Client N Polling
        loop Every 30 sec
            ClientN->>DeviceGW: GET /sessions/{id}/status
            DeviceGW->>ImagingCore: GET /api/internal/sessions/{id}/status
            ImagingCore-->>DeviceGW: state + SAS + image-id
            DeviceGW-->>ClientN: state + SAS + image-id
            ClientN->>ClientN: Download...
        end
    and Portal Refresh
        loop Every 5 sec
            Portal->>PortalBackend: GET /sessions/progress
            PortalBackend->>ImagingCore: Fetch current states
            ImagingCore-->>PortalBackend: state update
            PortalBackend-->>Portal: progress update
            Portal->>Tech: Update dashboard (1 downloading, 1 applied, 1 completed)
        end
    end
    
    Client1->>DeviceGW: POST /sessions/{id}/progress (Complete)
    DeviceGW->>ImagingCore: POST /api/internal/sessions/{id}/progress
    Note over ImagingCore: Session 1: SessionCompleted
    Client2->>DeviceGW: POST /sessions/{id}/progress (Complete)
    DeviceGW->>ImagingCore: POST /api/internal/sessions/{id}/progress
    Note over ImagingCore: Session 2: SessionCompleted
    ClientN->>DeviceGW: POST /sessions/{id}/progress (Complete)
    DeviceGW->>ImagingCore: POST /api/internal/sessions/{id}/progress
    Note over ImagingCore: Session N: SessionCompleted
    
    Portal->>PortalBackend: GET /sessions/progress
    PortalBackend->>ImagingCore: Fetch final states
    ImagingCore-->>PortalBackend: all completed
    PortalBackend-->>Portal: '3 of 3 completed'
    Portal->>Tech: Display "All devices completed imaging"
```

## Bulk Operations

- **Atomic assignment**: All N sessions updated in single transaction
- **Per-device SAS**: Each device gets unique SAS URL
- **Dashboard refresh**: 5-second polling for real-time progress
- **Concurrent polling**: All clients poll independently every 30 sec
- **Progress states**: Downloading, Applied, Completed tracked per-device

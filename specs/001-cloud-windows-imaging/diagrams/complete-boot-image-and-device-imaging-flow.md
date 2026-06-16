# Boot Image Generation, Upload, and Device Imaging

Complete sequence showing boot image creation, manifest signing, portal upload (chunked with progress/retry), finalization, device deployment, and USB preparation.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant MediaBuilder as Cloud Imaging<br/>Media Builder
    participant Portal as Cloud Imaging<br/>Portal
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant ClientWinPE as Cloud Imaging<br/>Client (WinPE)
    participant DeviceGW as Device Gateway<br/>API

    Tech->>MediaBuilder: Generate Boot Image
    Note over MediaBuilder: Bundle WinPE + Client + Config
    MediaBuilder->>MediaBuilder: Create signed boot image manifest
    
    MediaBuilder->>Portal: Upload/register boot image metadata
    Portal->>OperatorAPI: POST /api/boot-images/upload-session
    OperatorAPI->>ImagingCore: Create staged upload session
    ImagingCore-->>OperatorAPI: bootImageId + write SAS URL
    OperatorAPI-->>Portal: bootImageId + write SAS URL
    Portal->>Tech: Upload session created, get SAS
    Tech->>Storage: Upload WIM in chunks (progress, retry)
    Note over Storage: Staged blob (unpublished)
    Storage-->>Portal: Chunk received
    
    Portal->>Portal: Validate checksum & manifest
    Portal->>OperatorAPI: POST /api/boot-images/{bootImageId}/upload/complete
    OperatorAPI->>ImagingCore: Finalize publish
    Note over ImagingCore: Boot image now visible in catalog
    ImagingCore->>Storage: Mark blob published
    
    Tech->>Tech: Prepare USB Storage Device
    MediaBuilder->>OperatorAPI: GET /api/boot-images (Entra ID)
    OperatorAPI->>ImagingCore: Query boot image catalog
    Note over ImagingCore: Latest user image metadata
    ImagingCore-->>OperatorAPI: Boot image list
    OperatorAPI-->>MediaBuilder: Boot image list
    
    MediaBuilder->>OperatorAPI: POST /api/boot-images/{bootImageId}/sas
    OperatorAPI->>ImagingCore: Generate boot image SAS
    ImagingCore->>Storage: Create SAS URL
    Storage-->>ImagingCore: SAS URL + expiry
    ImagingCore-->>OperatorAPI: SAS URL
    OperatorAPI-->>MediaBuilder: SAS URL (+ expiry)
    
    MediaBuilder->>Storage: Download boot image via SAS
    Storage-->>MediaBuilder: Boot image downloaded
    MediaBuilder->>MediaBuilder: Validate removable disk
    MediaBuilder->>MediaBuilder: Configure UEFI partition
    MediaBuilder->>MediaBuilder: Deploy boot image
    MediaBuilder->>MediaBuilder: Configure auto-start (client config)
    Tech->>Tech: Boot device from USB
    
    ClientWinPE->>DeviceGW: POST /api/sessions (register)
    DeviceGW->>ImagingCore: Create session record
    Note over ImagingCore: SessionInit state
    ImagingCore-->>DeviceGW: device-session token
    DeviceGW-->>ClientWinPE: Token + passcode
    ClientWinPE->>Tech: Display passcode on screen
    
    Tech->>Portal: Enter passcode + select OS image
    Portal->>OperatorAPI: POST /api/sessions/couple
    OperatorAPI->>ImagingCore: Couple and assign by passcode + imageId
    Note over ImagingCore: SessionAssigned state
    
    loop Every 30 sec
        ClientWinPE->>DeviceGW: GET /api/sessions/{id}/status
        DeviceGW->>ImagingCore: GET /api/internal/sessions/{id}/status
        Note over ImagingCore: Returns SAS + image details
        ImagingCore-->>DeviceGW: Session state
        DeviceGW-->>ClientWinPE: SAS URL for OS image
    end
    
    ClientWinPE->>Storage: Download OS image via SAS
    Note over ClientWinPE: Refresh SAS if < 15min remaining
    Storage-->>ClientWinPE: OS image chunks
    ClientWinPE->>ClientWinPE: Apply image to disk
    ClientWinPE->>DeviceGW: POST /api/sessions/{id}/progress (Complete)
    DeviceGW->>ImagingCore: POST /api/internal/sessions/{id}/progress
    Note over ImagingCore: SessionCompleted state
    ClientWinPE->>ClientWinPE: Reboot into deployed OS
```

## Key Flows

- **Boot Image Lifecycle**: Generation → metadata registration → staged upload → validation → publish commit
- **Device Imaging**: Register session → couple → assign image → poll → download → apply → reboot
- **USB Preparation**: Query boot images → get SAS → download → validate disk → deploy
- **Staged Visibility**: Boot image remains unpublished until finalize succeeds

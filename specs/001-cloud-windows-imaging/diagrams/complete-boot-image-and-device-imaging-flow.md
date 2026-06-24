# Boot Image Generation, Upload, and Device Imaging

Complete sequence showing Media Builder sign-in, boot image creation with branding embed, manual portal upload, USB preparation, device session registration, coupling, and imaging.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant MediaBuilder as Cloud Imaging<br/>Media Builder
    participant EntraID as Microsoft<br/>Entra ID
    participant GitHub as MSEndpointMgr<br/>GitHub Releases
    participant Portal as Cloud Imaging<br/>Portal
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant USB as USB Storage Device
    participant ClientWinPE as Cloud Imaging<br/>Client (WinPE)
    participant DeviceGW as Device Gateway<br/>API

    Note over Tech,MediaBuilder: Generate Boot Image Workflow
    Tech->>MediaBuilder: Launch Media Builder
    MediaBuilder->>EntraID: Sign in with Entra ID (FR-052)
    EntraID-->>MediaBuilder: Access token (Operator scope)
    MediaBuilder->>MediaBuilder: SignInView -> OperationSelectionView
    Note over MediaBuilder: Check copype.cmd + makewinpemedia (FR-050a)<br/>Both workflow cards disabled if ADK or WinPE add-on absent

    Tech->>MediaBuilder: Select Generate Boot Image
    MediaBuilder->>MediaBuilder: Show GenerateBootImageView (FR-051a)
    Note over MediaBuilder: Choose: GitHub auto-download or custom local path

    alt GitHub auto-download
        MediaBuilder->>GitHub: GET /repos/MSEndpointMgr/CloudImaging/releases/latest
        GitHub-->>MediaBuilder: Latest release tag + asset URL
        MediaBuilder->>GitHub: Download Cloud Imaging Client binaries (real-time progress)
        GitHub-->>MediaBuilder: Client binaries
    else Custom local path
        Tech->>MediaBuilder: Specify path to pre-downloaded Client binaries
    end

    Tech->>MediaBuilder: Select output folder

    MediaBuilder->>OperatorAPI: GET /api/branding/logo/sas (Entra ID token)
    OperatorAPI->>ImagingCore: Issue read SAS token URL for branding logo blob
    ImagingCore-->>OperatorAPI: SAS token URL (or no branding configured)
    OperatorAPI-->>MediaBuilder: SAS token URL

    alt Branding logo configured
        MediaBuilder->>Storage: Download branding logo via SAS token URL
        Storage-->>MediaBuilder: Logo asset
    else No branding configured
        Note over MediaBuilder: Embed MSEndpointMgr default logo as fallback (FR-051)
    end

    MediaBuilder->>MediaBuilder: Assemble WinPE + Client binaries + branding logo + config
    MediaBuilder->>MediaBuilder: Create signed manifest (version, checksums, timestamp)
    MediaBuilder->>MediaBuilder: Write boot image WIM to output folder
    Note over MediaBuilder: WIM contains: WinPE + Client executable + branding logo
    MediaBuilder->>Tech: Completion notification -- output path + portal upload instructions (FR-051b)

    Note over Tech,Portal: Technician uploads WIM manually via Cloud Imaging Portal
    Tech->>Portal: Upload boot image WIM via boot image catalog
    Portal->>OperatorAPI: POST /api/boot-images/upload-session
    OperatorAPI->>ImagingCore: Create staged upload session
    ImagingCore-->>OperatorAPI: bootImageId + write SAS token URL
    OperatorAPI-->>Portal: bootImageId + write SAS token URL
    Tech->>Storage: Upload WIM in chunks (progress, retry)
    Note over Storage: Staged blob (unpublished)

    Portal->>Portal: Validate checksum and manifest
    Portal->>OperatorAPI: POST /api/boot-images/{bootImageId}/upload/complete
    OperatorAPI->>ImagingCore: Finalize publish
    Note over ImagingCore: Boot image now visible in catalog
    ImagingCore->>Storage: Mark blob published

    Note over Tech,USB: Prepare USB Storage Device Workflow
    Tech->>MediaBuilder: Select Prepare USB Storage Device
    MediaBuilder->>OperatorAPI: GET /api/boot-images (Entra ID)
    OperatorAPI->>ImagingCore: Query boot image catalog
    ImagingCore-->>OperatorAPI: Boot image list
    OperatorAPI-->>MediaBuilder: Boot image list

    MediaBuilder->>OperatorAPI: POST /api/boot-images/{bootImageId}/sas
    OperatorAPI->>ImagingCore: Generate boot image SAS
    ImagingCore->>Storage: Create SAS token URL + expiry
    Storage-->>ImagingCore: SAS token URL
    ImagingCore-->>OperatorAPI: SAS token URL
    OperatorAPI-->>MediaBuilder: SAS token URL

    MediaBuilder->>Storage: Download boot image WIM via SAS token URL
    Storage-->>MediaBuilder: Boot image WIM downloaded

    Note over MediaBuilder,USB: Qualify USB: bus type = USB, removable flag = true (FR-054)
    MediaBuilder->>USB: Create two partitions (cache + bootable)
    MediaBuilder->>USB: Deploy boot image WIM to bootable partition
    Note over USB: WIM contains WinPE + Cloud Imaging Client + branding logo
    MediaBuilder->>USB: Configure UEFI auto-start
    MediaBuilder->>USB: Write preparation manifest

    Tech->>ClientWinPE: Boot device from USB

    Note over ClientWinPE,DeviceGW: Device Session Registration and Imaging
    ClientWinPE->>ClientWinPE: Read branding logo from executable directory (FR-002a)
    ClientWinPE->>DeviceGW: POST /api/v1/sessions (register)
    DeviceGW->>ImagingCore: Create session record + pre-flight authorization
    Note over ImagingCore: SessionInit -> SessionAllowed (if authorized)<br/>SessionInit -> SessionNotAuthorized (terminal, if no match)
    ImagingCore-->>DeviceGW: device-session token + passcode (or SessionNotAuthorized)
    DeviceGW-->>ClientWinPE: Token + passcode
    ClientWinPE->>Tech: Display passcode on screen

    Tech->>Portal: Enter passcode in coupling modal
    Portal->>OperatorAPI: POST /api/sessions/couple (passcode)
    OperatorAPI->>ImagingCore: Couple session by passcode
    Note over ImagingCore: SessionAssigned state (passcode consumed)
    Tech->>Portal: Select OS image and click Assign
    Portal->>OperatorAPI: POST /api/sessions/{sessionId}/assign (image-id)
    OperatorAPI->>ImagingCore: Assign image to session
    Note over ImagingCore: SessionAssigned with image reference + SAS token URL generated

    loop Every 30 sec
        ClientWinPE->>DeviceGW: GET /api/v1/sessions/{id}/status
        DeviceGW->>ImagingCore: GET status
        ImagingCore-->>DeviceGW: state + SAS token URL + image details + currentStep + overallProgressPercent
        DeviceGW-->>ClientWinPE: SAS token URL for OS image + currentStep + overallProgressPercent
    end

    ClientWinPE->>ClientWinPE: Format target disk (format-disk step)
    ClientWinPE->>Storage: Download OS image via SAS token URL (download-image step)
    Note over ClientWinPE: Refresh SAS token URL if < 15 min remaining
    Storage-->>ClientWinPE: OS image chunks
    ClientWinPE->>ClientWinPE: Apply image to disk via DISM (apply-image step)
    ClientWinPE->>DeviceGW: POST /api/v1/sessions/{id}/progress (SessionCompleted)
    DeviceGW->>ImagingCore: Update session state = SessionCompleted
    ClientWinPE->>ClientWinPE: Reboot into deployed OS
```

## Key Flows

- **Generate Boot Image**: Sign in -> ADK check -> source selection (GitHub or custom path) -> branding retrieval from Operator API -> WIM assembly with branding logo -> completion notification with output path
- **Portal Upload**: Manual step by technician via Cloud Imaging Portal boot image catalog (Media Builder does not upload)
- **Prepare USB**: Query boot images -> get SAS -> download WIM -> qualify USB (bus type = USB + removable flag) -> deploy WIM
- **Device Session**: Register -> pre-flight authorization -> passcode display -> couple -> assign -> poll (with currentStep + overallProgressPercent) -> format -> download -> apply -> reboot
- **Branding**: Retrieved from Operator API and embedded in boot image WIM during Generate Boot Image; Client reads from executable directory at startup (FR-002a, FR-051)

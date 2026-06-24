# Media Builder: Generate Boot Image Workflow

Detailed sequence for the GenerateBootImageView: sign-in, ADK prerequisite check, Client source selection (GitHub auto-download or custom local path), branding logo retrieval from Operator API, WIM assembly with embedded branding, and completion notification directing the technician to upload manually via the portal.

```mermaid
sequenceDiagram
    actor Tech as Technician
    participant MediaBuilder as Cloud Imaging<br/>Media Builder (WPF)
    participant EntraID as Microsoft<br/>Entra ID
    participant GitHub as MSEndpointMgr<br/>GitHub Releases
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage

    Tech->>MediaBuilder: Launch Media Builder
    MediaBuilder->>EntraID: Entra ID sign-in (FR-052)
    Note over MediaBuilder: SignInView: welcome screen with branding logo,<br/>app title, and short capability description
    EntraID-->>MediaBuilder: Access token (Operator scope)

    MediaBuilder->>MediaBuilder: Show OperationSelectionView
    MediaBuilder->>MediaBuilder: Detect copype.cmd + makewinpemedia (FR-050a)
    Note over MediaBuilder: Windows ADK + WinPE add-on prerequisite check

    alt ADK and WinPE add-on installed
        MediaBuilder->>MediaBuilder: Enable both workflow cards
        Tech->>MediaBuilder: Select Generate Boot Image
    else ADK or WinPE add-on missing
        MediaBuilder->>MediaBuilder: Disable both workflow cards
        MediaBuilder->>Tech: Display installation guidance message
        Note over Tech: Install Windows ADK + WinPE add-on then relaunch
    end

    MediaBuilder->>MediaBuilder: Show GenerateBootImageView (FR-051a)

    alt GitHub auto-download (internet access required)
        Tech->>MediaBuilder: Select "Automatic download" option
        MediaBuilder->>GitHub: GET /repos/MSEndpointMgr/CloudImaging/releases/latest
        GitHub-->>MediaBuilder: Latest release tag + asset metadata
        MediaBuilder->>GitHub: Download Cloud Imaging Client binaries
        Note over MediaBuilder: Real-time progress display during download
        GitHub-->>MediaBuilder: Client binaries downloaded
    else Custom local path (offline/airgap)
        Tech->>MediaBuilder: Select "Custom local path" option
        Tech->>MediaBuilder: Specify path to pre-downloaded Client binaries
        MediaBuilder->>MediaBuilder: Validate local path exists and contains binaries
    end

    Tech->>MediaBuilder: Select output folder for boot image WIM
    Note over MediaBuilder: Generation blocked until source and output folder confirmed (FR-051a)
    Tech->>MediaBuilder: Start generation

    MediaBuilder->>OperatorAPI: GET /api/branding/logo/sas (Entra ID token; FR-062)
    OperatorAPI->>ImagingCore: Issue read SAS token URL for branding logo blob
    ImagingCore-->>OperatorAPI: SAS token URL for logo asset (or no branding configured)
    OperatorAPI-->>MediaBuilder: SAS token URL

    alt Branding logo configured in portal
        MediaBuilder->>Storage: Download branding logo via SAS token URL
        Storage-->>MediaBuilder: Logo asset
    else No branding configured
        Note over MediaBuilder: Use MSEndpointMgr default logo as fallback (FR-051)
    end

    MediaBuilder->>MediaBuilder: Assemble WinPE environment using Windows ADK
    MediaBuilder->>MediaBuilder: Bundle Client binaries + branding logo into WIM
    Note over MediaBuilder: Client reads logo from its executable directory at runtime (FR-002a)
    MediaBuilder->>MediaBuilder: Create signed manifest (image version, WinPE version,<br/>component checksums, timestamp)
    MediaBuilder->>MediaBuilder: Write boot image WIM to output folder (FR-051)
    Note over MediaBuilder: WIM contains: WinPE + Client executable + branding logo + manifest

    MediaBuilder->>Tech: Completion notification (FR-051b)
    Note over Tech: Output folder path displayed<br/>Next step: upload WIM via Cloud Imaging Portal boot image catalog<br/>Media Builder does NOT perform the portal upload
```

## Flow Notes

- **Sign-in scope**: Entra ID sign-in at application launch is required before OperationSelectionView or any workflow (FR-052)
- **ADK prerequisite**: Both Generate Boot Image and Prepare USB Storage Device cards are blocked until copype.cmd and makewinpemedia are detected on the workstation (FR-050a)
- **Source exclusivity**: GitHub auto-download (internet required) and custom local path are mutually exclusive options (FR-051a)
- **Branding embed**: Logo retrieved via Operator API SAS token URL and embedded into the WIM alongside the Client executable; Client reads from its own executable directory at WinPE startup (FR-002a, FR-051, FR-062)
- **No portal upload**: Media Builder writes the WIM to a local output folder only; uploading to the Cloud Imaging Portal boot image catalog is a manual step performed by the technician (FR-051b)
- **Default logo fallback**: If no branding logo is configured in the portal at generation time, the MSEndpointMgr default logo is embedded; re-generation is required to update branding on already-prepared media (FR-051)

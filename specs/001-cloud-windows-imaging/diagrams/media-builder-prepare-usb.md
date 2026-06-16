# Media Builder USB Preparation

Media Builder signs in with Entra ID, queries published boot images, downloads via SAS, validates removable disk, and deploys boot image to USB with auto-start config.

```mermaid
sequenceDiagram
    actor MediaOp as Media Operations
    participant MediaBuilder as Cloud Imaging<br/>Media Builder (WPF)
    participant EntraID as Microsoft Entra ID
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant MetadataStore as Azure Table<br/>Storage
    participant USB as USB Storage Device

    MediaOp->>MediaBuilder: Launch Media Builder
    MediaBuilder->>EntraID: Entra ID device flow
    Note over MediaBuilder: User signs in with credentials
    EntraID-->>MediaBuilder: Access token (Operator scope)
    
    MediaBuilder->>MediaBuilder: Store access token (secure)
    MediaBuilder->>OperatorAPI: GET /boot-images<br/>(Entra ID token)
    Note over OperatorAPI: Validate app role = Media Builder
    OperatorAPI->>ImagingCore: Query published boot images
    ImagingCore->>MetadataStore: List boot images where published=true
    MetadataStore-->>ImagingCore: boot image records
    Note over ImagingCore: Include: id, name, checksum, blob-uri, published-at
    ImagingCore-->>OperatorAPI: boot image list
    OperatorAPI-->>MediaBuilder: boot image list
    MediaBuilder->>MediaBuilder: Display boot images in UI (dropdown)
    
    MediaOp->>MediaBuilder: Select boot image from dropdown
    MediaOp->>MediaBuilder: Click 'Prepare USB'
    MediaBuilder->>MediaBuilder: Display USB device selector
    MediaOp->>USB: Insert USB drive
    
    MediaBuilder->>MediaBuilder: Detect removable devices
    MediaBuilder->>USB: Query device properties
    Note over USB: Size, removable flag, file system
    USB-->>MediaBuilder: device info
    MediaBuilder->>MediaBuilder: Validate: is removable, size >= 8GB
    
    alt Device is valid
        MediaOp->>MediaBuilder: Confirm preparation (irreversible)
        MediaBuilder->>OperatorAPI: Request SAS for boot image<br/>(image-id, Entra ID token)
        OperatorAPI->>ImagingCore: Generate SAS
        ImagingCore->>Storage: Create SAS URL (1 hour expiry)
        Storage-->>ImagingCore: SAS URL
        ImagingCore-->>OperatorAPI: SAS URL
        OperatorAPI-->>MediaBuilder: SAS URL
        
        MediaBuilder->>Storage: Download boot image via SAS<br/>(chunked, resume-capable)
        Note over Storage: Stream WIM to staging area
        Storage-->>MediaBuilder: boot image chunks
        MediaBuilder->>MediaBuilder: Validate checksum
        
        alt Checksum matches
            MediaBuilder->>USB: Create two partitions on USB
            Note over USB: Partition 1: Cache (min 20GB recommended for 5-10GB OS images)<br/>Partition 2: Bootable (UEFI format for WinPE)
            USB-->>MediaBuilder: two partitions created
            
            MediaBuilder->>USB: Deploy boot image to bootable partition
            Note over USB: Write WIM to bootable partition, UEFI firmware config
            USB-->>MediaBuilder: boot image deployed
            
            MediaBuilder->>USB: Configure UEFI boot order (auto-start)
            Note over USB: Set USB as boot device, launch Cloud Imaging Client on WinPE boot
            USB-->>MediaBuilder: configured
            
            MediaBuilder->>MediaBuilder: Write client config (session-init endpoint, cache partition ref, etc.)
            MediaBuilder->>USB: Copy Cloud Imaging Client binary to bootable partition
            MediaBuilder->>USB: Copy client config file to bootable partition
            MediaBuilder->>USB: Write USB preparation manifest
            Note over USB: Timestamp, Media Builder version, boot image version, partition layout, validation status
            
            MediaBuilder->>MediaBuilder: Display success message
            MediaOp->>MediaBuilder: Eject USB
            Note over MediaOp: USB is ready for device imaging deployment<br/>Boot partition: WinPE + Cloud Imaging Client<br/>Cache partition: empty, ready for OS image caching
        else Checksum mismatch
            MediaBuilder->>MediaBuilder: Error: checksum validation failed
            MediaOp->>MediaBuilder: Retry download
        end
    else Device not valid
        MediaBuilder->>MediaBuilder: Error: device is not removable or too small
        MediaOp->>USB: Use different USB drive
    end
```

## USB Preparation Steps

1. **Device validation**: Removable flag, size >= 8GB, sufficient capacity (32GB+ recommended)
2. **SAS request**: 1-hour expiry for download session
3. **Download**: Chunked, resume-capable transfer
4. **Checksum**: Validate against manifest
5. **Create partitions**: Two partitions on USB (Cache: min 20GB for OS image caching + Bootable: UEFI format for WinPE)
6. **Deploy**: Write WIM to bootable partition
7. **Configure**: UEFI auto-start, client config, preparation manifest
8. **Eject**: Ready for device deployment (boot partition has WinPE + Cloud Imaging Client, cache partition empty)

## Authorization

- **Media Builder app role**: Required to list and download boot images
- **SAS generation**: Operator API validates role before issuing SAS
- **Download**: SAS token limits access to single image for fixed time

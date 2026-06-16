# OS Image Download, Apply, and Session Completion

Device checks cache partition for matching image, validates hash, and either uses cached image or downloads new image. Then formats disk, applies image, and completes session with reboot.

```mermaid
sequenceDiagram
    participant ClientWinPE as Cloud Imaging<br/>Client (WinPE)
    participant DeviceGW as Device Gateway<br/>API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant Cache as USB Cache<br/>Partition
    participant Disk as Local Disk

    Note over ClientWinPE: Previous poll returned SAS + image-id + sha256Hash
    
    ClientWinPE->>ClientWinPE: Check USB cache for image-id + version
    alt Cached image exists
        ClientWinPE->>Cache: Read cached image metadata + hash
        Cache-->>ClientWinPE: cached image hash
        ClientWinPE->>DeviceGW: POST /sessions/{session-id}/cache/validate<br/>(image-id, cached-hash)
        DeviceGW->>DeviceGW: Lookup API hash for image-id
        DeviceGW-->>ClientWinPE: isValid=true, or current hash if mismatch
        
        alt Cache hash matches API hash
            Note over ClientWinPE: Cache HIT - skip download
            ClientWinPE->>Cache: Load cached image into memory
            Cache-->>ClientWinPE: cached image ready
            Note over ClientWinPE: Proceed to format + apply with cached image
        else Cache hash mismatch or corrupted
            Note over ClientWinPE: Cache MISS - delete stale entry
            ClientWinPE->>Cache: Delete corrupted cached image
            Cache-->>ClientWinPE: deleted
            Note over ClientWinPE: Proceed to download fresh copy
        end
    else No cached image
        Note over ClientWinPE: No cache entry - download fresh
    end
    
    alt Need to download fresh image
        ClientWinPE->>Storage: GET /images/{image-id}<br/>(SAS URL, Range header for resume)
        Note over ClientWinPE: Resume-capable, chunked transfer
        Storage-->>ClientWinPE: Image chunk + progress
        
        loop Download with progress reporting
            ClientWinPE->>ClientWinPE: Write chunk to staging area
            ClientWinPE->>ClientWinPE: Compute running checksum
            ClientWinPE->>ClientWinPE: Update progress UI
            alt SAS expires in < 15 minutes
                ClientWinPE->>DeviceGW: GET /sessions/{session-id}/refresh-sas<br/>(device-session token)
                DeviceGW->>ImagingCore: Refresh SAS
                ImagingCore->>ImagingCore: Generate new SAS URL (15 min buffer)
                ImagingCore-->>DeviceGW: new SAS URL
                DeviceGW-->>ClientWinPE: new SAS URL
                Note over ClientWinPE: Resume download with new SAS
            end
        end
        
        ClientWinPE->>ClientWinPE: Download complete
        ClientWinPE->>ClientWinPE: Validate checksum vs API hash
        
        alt Checksum matches
            ClientWinPE->>Cache: Store downloaded image + hash + metadata
            Cache-->>ClientWinPE: cached
            Note over ClientWinPE: Image ready for apply
        else Checksum mismatch
            ClientWinPE->>DeviceGW: POST /sessions/{session-id}/error<br/>(error: checksum-mismatch)
            DeviceGW->>ImagingCore: Mark session error
            Note over ImagingCore: State: SessionError
            ClientWinPE->>ClientWinPE: Retry download from beginning
        end
    end
    
    Note over ClientWinPE: Proceed with remaining workflow steps
    ClientWinPE->>Disk: Format disk for Windows image applicability
    Note over Disk: NTFS formatting, partition layout
    Disk-->>ClientWinPE: format complete
    
    ClientWinPE->>Disk: Apply image to disk (via DISM)
    Note over Disk: Write image blocks, boot config
    ClientWinPE->>ClientWinPE: Polling UI: show apply progress
    Disk-->>ClientWinPE: Apply complete
    
    ClientWinPE->>DeviceGW: POST /sessions/{session-id}/complete<br/>(device-session token)
    Note over DeviceGW: Mark session done
    DeviceGW->>ImagingCore: Update session state = SessionCompleted
    ImagingCore->>Storage: Persist session record + completion time
    Note over ImagingCore: State: SessionCompleted<br/>Completed at: {timestamp}
    ImagingCore-->>DeviceGW: acknowledged
    DeviceGW-->>ClientWinPE: acknowledged
    
    ClientWinPE->>ClientWinPE: Reboot into deployed OS
    Note over ClientWinPE: Windows starts from deployed image
```

## Download & Retry Semantics

- **Resume-capable**: Ranges supported, client tracks offset
- **SAS refresh**: Auto-refresh if < 15 min remaining
- **Checksum validation**: Against known image manifest
- **Error handling**: On failure, session marked SessionError, client can retry download
- **Progress reporting**: UI updated every chunk (for technician visibility)

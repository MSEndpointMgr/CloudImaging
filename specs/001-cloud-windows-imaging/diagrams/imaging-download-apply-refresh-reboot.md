# OS Image Format, Download, Apply, and Session Completion

Device formats the target disk first (format-disk step), then checks cache partition for a matching image and validates its hash. Either uses the cached image or downloads a fresh copy. Then applies the image to the formatted disk and completes the session with a reboot.

```mermaid
sequenceDiagram
    participant ClientWinPE as Cloud Imaging<br/>Client (WinPE)
    participant DeviceGW as Device Gateway<br/>API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant Cache as USB Cache<br/>Partition
    participant Disk as Local Disk

    Note over ClientWinPE: Previous poll returned SAS + image-id + sha256Hash

    Note over ClientWinPE: format-disk step
    ClientWinPE->>Disk: Format target system disk (partition + NTFS)
    Disk-->>ClientWinPE: Format complete
    ClientWinPE->>DeviceGW: POST /api/v1/sessions/{id}/progress (format-disk: Completed)
    DeviceGW->>ImagingCore: Persist step progress

    Note over ClientWinPE: download-image step
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
        ClientWinPE->>Storage: GET /images/{image-id}<br/>(SAS token URL, Range header for resume)
        Note over ClientWinPE: Resume-capable, chunked transfer
        Storage-->>ClientWinPE: Image chunk + progress
        
        loop Download with progress reporting
            ClientWinPE->>ClientWinPE: Write chunk to staging area
            ClientWinPE->>ClientWinPE: Compute running checksum
            ClientWinPE->>ClientWinPE: Update progress UI
            alt SAS expires in < 15 minutes
                ClientWinPE->>DeviceGW: POST /api/v1/sessions/{sessionId}/sas/refresh<br/>(device-session token)
                DeviceGW->>ImagingCore: Refresh SAS
                ImagingCore->>ImagingCore: Generate new SAS token URL (15 min buffer)
                ImagingCore-->>DeviceGW: new SAS token URL
                DeviceGW-->>ClientWinPE: new SAS token URL
                Note over ClientWinPE: Resume download with new SAS token URL
            end
        end
        
        ClientWinPE->>ClientWinPE: Download complete
        ClientWinPE->>ClientWinPE: Validate checksum vs API hash
        
        alt Checksum matches
            ClientWinPE->>ClientWinPE: Check space on USB cache partition after 30-day purge
            alt Sufficient cache space available
                ClientWinPE->>Cache: Store downloaded image + hash + metadata
                Cache-->>ClientWinPE: Cached successfully
            else Insufficient space after purge (FR-009d)
                Note over ClientWinPE: Skip cache write -- proceed with in-memory staging<br/>No LRU eviction of existing valid cache entries
            end
            Note over ClientWinPE: Image ready for apply
        else Checksum mismatch
            ClientWinPE->>DeviceGW: POST /api/v1/sessions/{session-id}/progress<br/>(error: checksum-mismatch)
            DeviceGW->>ImagingCore: Mark session error
            Note over ImagingCore: State: SessionFailed
            ClientWinPE->>ClientWinPE: Retry download from beginning
        end
    end

    Note over ClientWinPE: apply-image step
    ClientWinPE->>Disk: Apply image to disk (via DISM)
    Note over Disk: Write image blocks, boot config
    ClientWinPE->>ClientWinPE: Polling UI: show apply progress
    Disk-->>ClientWinPE: Apply complete
    
    ClientWinPE->>DeviceGW: POST /api/v1/sessions/{sessionId}/progress<br/>(apply-image: Completed, device-session token)
    Note over DeviceGW: Final step reported -- Imaging Core auto-transitions to SessionCompleted
    DeviceGW->>ImagingCore: Persist final step + transition session to SessionCompleted
    ImagingCore->>Storage: Persist session record + completion time
    Note over ImagingCore: State: SessionCompleted<br/>Completed at: {timestamp}
    ImagingCore-->>DeviceGW: acknowledged
    DeviceGW-->>ClientWinPE: acknowledged
    
    ClientWinPE->>ClientWinPE: Reboot into deployed OS
    Note over ClientWinPE: Windows starts from deployed image
```

## Step Order and Semantics

- **Step order**: format-disk -> download-image -> apply-image (matches ProgressView label order: Format, Download, Apply)
- **Format target**: System disk (device internal drive) formatted before download/apply
- **Download staging**: OS image downloaded to USB cache partition or in-memory staging; not written to the system disk
- **Cache skip**: If insufficient space on USB cache partition after 30-day auto-purge, cache write is skipped and direct staging used; no LRU eviction of valid cache entries (FR-009d)
- **Resume-capable download**: Range headers supported; client tracks byte offset across SAS refreshes
- **SAS refresh**: Auto-refresh if < 15 min remaining on current token
- **Checksum validation**: Downloaded image validated against API-provided SHA256 hash before apply
- **Failure state**: SessionFailed (not SessionError) is the terminal error state; support reference code shown in ResultsView

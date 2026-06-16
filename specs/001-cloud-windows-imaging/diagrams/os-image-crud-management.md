# OS Image Management (CRUD)

Administrator uploads, lists, updates, deletes OS images with in-use guards.

```mermaid
sequenceDiagram
    actor Admin as Administrator
    participant Portal as Cloud Imaging<br/>Portal (frontend)
    participant PortalBackend as Cloud Imaging<br/>Portal (backend)
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant Storage as Azure Blob<br/>Storage
    participant MetadataStore as Azure Table<br/>Storage

    Admin->>Portal: Upload new OS image (WIM file 5-10 GB)
    Portal->>PortalBackend: POST /images/upload<br/>(multipart: file, name, description)
    Note over PortalBackend: Entra ID admin role required
    
    PortalBackend->>OperatorAPI: POST /api/internal/images/upload-session<br/>(file size, content-type)
    OperatorAPI->>ImagingCore: Create chunked upload session
    ImagingCore->>ImagingCore: Generate session-id, calculate chunks (4MB each)
    ImagingCore->>Storage: Create staging blob
    ImagingCore->>Storage: Generate SAS (24hr TTL)
    ImagingCore-->>OperatorAPI: upload-session: {id, chunk-size: 4MB, total-chunks, SAS-url}
    OperatorAPI-->>PortalBackend: upload-session details
    PortalBackend-->>Portal: begin chunked upload UI
    
    Note over Portal: Upload dialog: progress bar, pause/resume, retry
    loop For each 4MB chunk
        Portal->>PortalBackend: Send chunk data
        Note over PortalBackend: Buffer chunk, compute SHA256
        PortalBackend->>PortalBackend: Check previous chunks integrity
        
        PortalBackend->>Storage: PUT /chunks/{chunk-index}<br/>(Range header, SAS, 3x retry with backoff)
        alt Upload succeeds
            Storage-->>PortalBackend: 201 Created, next-chunk-offset
            PortalBackend-->>Portal: chunk #{N} complete, progress {X}%
        else Network failure / timeout
            Note over PortalBackend: Retry up to 3x with 1s-10s exponential backoff
            PortalBackend->>Storage: Retry PUT with Range header
            alt Retry succeeds
                Storage-->>PortalBackend: 201 Created
                PortalBackend-->>Portal: resumed from offset {Y}
            else Retry exhausted
                Portal-->>Portal: Show error, user can pause/resume later
            end
        end
    end
    
    Portal->>PortalBackend: All chunks uploaded, finalize
    PortalBackend->>OperatorAPI: POST /api/internal/images/{id}/upload/complete<br/>(full-file SHA256)
    OperatorAPI->>ImagingCore: Validate & publish image
    ImagingCore->>ImagingCore: Verify uploaded chunk hashes + full-file SHA256
    
    alt Checksum validation passes
        ImagingCore->>Storage: Mark blob published (staging -> production)
        ImagingCore->>MetadataStore: Create image record
        Note over MetadataStore: ImageId, name, version, SHA256, created-by, created-at, size
        ImagingCore-->>OperatorAPI: image-id + published blob-uri
        OperatorAPI-->>PortalBackend: success
        PortalBackend-->>Portal: 'Image uploaded'
        Portal->>Admin: Show success banner
    else Checksum validation fails
        ImagingCore->>Storage: Delete staging blob
        ImagingCore-->>OperatorAPI: Error: checksum mismatch
        OperatorAPI-->>PortalBackend: Error (422 Unprocessable Entity)
        PortalBackend-->>Portal: Error: re-upload required
    end
    
    Admin->>Portal: View OS images list
    Portal->>PortalBackend: GET /images
    PortalBackend->>OperatorAPI: GET /images
    OperatorAPI->>ImagingCore: Query published images
    ImagingCore->>MetadataStore: List all image records
    MetadataStore-->>ImagingCore: images
    ImagingCore-->>OperatorAPI: image list with metadata
    OperatorAPI-->>PortalBackend: image list
    PortalBackend-->>Portal: display grid
    Portal->>Admin: Show [Image1, Image2, Image3...]
    
    Admin->>Portal: Edit image metadata (name, description)
    Portal->>PortalBackend: PATCH /images/{image-id}<br/>(name, description)
    PortalBackend->>OperatorAPI: PATCH /images/{image-id}
    OperatorAPI->>ImagingCore: Update image record
    ImagingCore->>MetadataStore: Update name/description
    Note over MetadataStore: Name, Description (only metadata changes, blob unchanged)
    ImagingCore-->>OperatorAPI: updated record
    OperatorAPI-->>PortalBackend: success
    PortalBackend-->>Portal: confirmed
    Portal->>Admin: Show updated image
    
    Admin->>Portal: Delete OS image
    Portal->>PortalBackend: DELETE /images/{image-id}
    PortalBackend->>OperatorAPI: DELETE /images/{image-id}
    OperatorAPI->>ImagingCore: Check image in-use
    ImagingCore->>MetadataStore: Query active sessions using this image
    
    alt No active sessions
        MetadataStore-->>ImagingCore: no sessions
        ImagingCore->>MetadataStore: Delete image record
        ImagingCore->>Storage: Delete blob
        ImagingCore-->>OperatorAPI: deleted
        OperatorAPI-->>PortalBackend: success
        PortalBackend-->>Portal: 'Image deleted'
        Portal->>Admin: Remove from list
    else Active sessions exist
        MetadataStore-->>ImagingCore: found 2 active sessions
        ImagingCore-->>OperatorAPI: Error: image in use by 2 sessions
        OperatorAPI-->>PortalBackend: Error (409 Conflict)
        PortalBackend-->>Portal: Error message
        Portal->>Admin: Display 'Cannot delete: in use by 2 devices'
    end
```

## Image State & Guards

| Operation | Guard | Condition |
|-----------|-------|-----------|
| Create | – | Always allowed |
| Read/List | Published | Only published images visible |
| Update | – | Metadata only, blob immutable |
| Delete | In-use check | Blocked if active sessions reference image |

## Authorization

- **Read/List images**: Any authenticated user
- **Upload**: Admin role only
- **Update**: Admin role only
- **Delete**: Admin role only + in-use guard

# Imaging Core API Contract

**Service**: CloudImaging.ImagingCoreApi  
**Type**: Azure Functions v4 isolated worker (.NET 10)  
**Visibility**: Private Link only (no public endpoint)  
**Primary Consumers**: Device Gateway API, Operator API  
**Auth**: trusted service-to-service identity + app-role authorization

## Purpose

Private source-of-truth API for lifecycle orchestration, SAS issuance, and metadata persistence.

## Core Responsibilities

- Device session lifecycle state machine and enforcement.
- One-time passcode generation/validation/consume rules.
- Device-session token association and heartbeat rules.
- OS image and boot image metadata persistence.
- Boot image artifact persistence in Storage Account using staged upload and publish semantics.
- SAS token issuance/refresh for image downloads.
- Branding configuration persistence.
- Session and step telemetry persistence.

## Base Path

`/api/internal`

## Device Workflow Endpoints (called by Device Gateway API)

- `POST /api/internal/sessions`
  - Create session, generate passcode, persist hashed passcode state.
- `GET /api/internal/sessions/{sessionId}/status`
  - Return state, assignment, SAS context for polling, and overall imaging progress data.
- `POST /api/internal/sessions/{sessionId}/progress`
  - Record step status and progression.
- `POST /api/internal/sessions/{sessionId}/sas/refresh`
  - Issue refreshed SAS token for active download session.

## Operator Workflow Endpoints (called by Operator API)

### Sessions

- `GET /api/internal/sessions`
- `GET /api/internal/sessions/{sessionId}`
- `POST /api/internal/sessions/couple`
- `POST /api/internal/sessions/bulk-assign`

### OS Images

- `GET /api/internal/images`
- `POST /api/internal/images`
- `PATCH /api/internal/images/{imageId}`
- `DELETE /api/internal/images/{imageId}`

### Branding

- `GET /api/internal/branding`
- `PUT /api/internal/branding`

### Boot Images

- `POST /api/internal/boot-images/upload-session`
  - Create a staged boot image upload session and issue write authorization for a blob that is not yet published.
- `POST /api/internal/boot-images/{bootImageId}/upload/complete`
  - Validate the uploaded WIM, record the final blob reference, and mark the boot image as published in the catalog.
- `GET /api/internal/boot-images`
- `POST /api/internal/boot-images`
- `PATCH /api/internal/boot-images/{bootImageId}`
- `DELETE /api/internal/boot-images/{bootImageId}`
- `POST /api/internal/boot-images/{bootImageId}/sas`

### Upload lifecycle

- A staged boot image upload is not visible to catalog consumers until the finalize publish call succeeds.
- The final publish step MUST verify manifest and checksum metadata before exposing the new boot image.
- If the upload fails or is abandoned, the staged blob remains unpublished and may be cleaned up by retention policy.

## OS Image Hash Management

- **Hash computation**: When an OS image is uploaded and cataloged, Imaging Core API MUST compute and persist the SHA256 hash of the blob.
- **Hash exposure**: GET /api/internal/images/{imageId} and GET /api/internal/sessions/{sessionId}/status responses MUST include `sha256Hash` field for each assigned image.
- **Progress exposure**: GET /api/internal/sessions/{sessionId}/status responses MUST include overall imaging completion percentage and the current imaging step for portal display.
- **Hash immutability**: OS image blobs are immutable; hash does not change after initial upload and publication.
- **Cache validation**: Device Gateway API forwards hash to client via GET /api/sessions/{sessionId}/status; client uses hash to validate cached copies (see device-gateway-api.md cache validation semantics).

## Lifecycle Policy Notes

- **Session states**: `SessionInit -> SessionAllowed -> SessionAssigned -> SessionStarted -> SessionInProgress -> SessionCompleted | SessionFailed`.
- **State auto-transitions**: `SessionAssigned -> SessionStarted` occurs automatically on device's next poll after successful assignment retrieval.

### Session Timeout Semantics (FR-021 detailed)

- **Pre-imaging states** (SessionInit, SessionAllowed, SessionAssigned): Expire after 30 minutes idle (no GET poll from Device Gateway). Idle timer resets on each successful poll.
- **Active imaging states** (SessionStarted, SessionInProgress): Auto-fail after 4 hours without poll heartbeat. Heartbeat = successful GET /status every 30s; two consecutive missed intervals trigger failure.
- **One-time passcode**: Stored as SHA256 hash; invalidated on successful couple or 30 min after SessionInit, whichever first.
- **Terminal states** (SessionCompleted, SessionFailed): Persisted 24 hours, then auto-purged.

### Boot Image Upload Publish Semantics

- Staged upload not visible to catalog until finalize publish succeeds.
- Final publish step MUST validate manifest integrity and SHA256 checksum before catalog exposure.
- Abandoned uploads auto-cleaned by retention policy (default 7 days).

## Non-Responsibilities

- No direct public-client authentication flows.
- No direct browser/UI serving.
- No public network exposure.

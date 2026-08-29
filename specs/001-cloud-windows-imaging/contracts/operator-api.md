# Operator API Contract

> **Contract Revision — 2026-06-26** (post-freeze): Added boot media client certificate PFX endpoint introduced by FR-068, FR-069, FR-070, FR-071. Per contract freeze policy this constitutes a versioned revision; all implementation MUST target these updated semantics.

**Service**: CloudImaging.OperatorApi  
**Type**: Azure Functions v4 isolated worker (.NET 10)  
**Visibility**: Public HTTPS endpoint  
**Primary Consumers**: Cloud Imaging Portal backend, Cloud Imaging Media Builder  
**Auth**: Entra ID bearer token required on all endpoints + app-role authorization

## Purpose

Authenticated operator boundary for administrative and technician operations. This API enforces RBAC and hides private core orchestration from external tools.

## Core Responsibilities

- Authorize calls using Entra ID and app roles.
- Broker session operations for portal workflows.
- Broker OS image and branding operations.
- Provide boot image operations for Media Builder and portal.
- Broker all requests to Imaging Core API over private connectivity.

## App Roles

> These are **service-level** app roles for service-to-service authorization on the Operator API. User-level access control (`CloudImaging.Administrator`, `CloudImaging.Technician`) is enforced upstream: at the Portal backend (FR-040a) and the Media Builder UI (FR-050b), using roles from the shared Entra ID enterprise app registration (FR-040b).

- `CloudImaging.PortalAccess`: assigned to the Portal backend managed identity; grants full operator access (sessions, OS images, branding, boot image lifecycle).
- `CloudImaging.MediaBuilderAccess`: assigned to the Media Builder service principal; grants read-only access to boot images and SAS token URL generation.

## App Role Authorization Matrix

| Endpoint | CloudImaging.PortalAccess | CloudImaging.MediaBuilderAccess |
|----------|-------------------|--------------------------|
| GET /api/sessions | YES | NO |
| POST /api/sessions/couple | YES | NO |
| POST /api/sessions/{sessionId}/assign | YES | NO |
| POST /api/sessions/bulk-assign | YES | NO |
| GET /api/images | YES | NO |
| POST /api/images/upload-session | YES | NO |
| POST /api/images/{imageId}/upload/complete | YES | NO |
| PATCH /api/images/{imageId} | YES | NO |
| DELETE /api/images/{imageId} | YES | NO |
| GET /api/boot-images | YES | YES |
| POST /api/boot-images/{bootImageId}/sas | YES | YES |
| POST /api/boot-images/upload-session | YES | NO |
| POST /api/boot-images/{bootImageId}/upload/complete | YES | NO |
| PATCH /api/boot-images/{bootImageId} | YES | NO |
| DELETE /api/boot-images/{bootImageId} | YES | NO |
| GET /api/branding | YES | NO |
| PUT /api/branding | YES | NO |
| GET /api/branding/logo/sas | YES | YES |
| GET /api/configuration | YES | NO |
| PATCH /api/configuration | YES | NO |
| GET /api/bootmedia/certificate/pfx | NO | YES |
| GET /api/bootmedia/certificate/metadata | NO | YES |

**Key distinction**: Media Builder role has read-only access to boot image queries and SAS generation. Portal role has full lifecycle (create/update/delete) for boot images and OS images.

## Base Path

`/api`

## Session Endpoints (Portal role)

- `GET /api/sessions`
  - List sessions with status group filter and pagination. Filter query parameter aligns with Portal UI filter tabs:
    - `filter=active` (default): sessions in `SessionAllowed`, `SessionAssigned`, `SessionStarted`, `SessionInProgress`.
    - `filter=completed`: sessions in `SessionCompleted`.
    - `filter=failed`: sessions in `SessionFailed` and `SessionNotAuthorized`.
    - `filter=all`: all sessions regardless of state.
  - Response includes per-group counts for Portal tab badge display.
- `GET /api/sessions/{sessionId}`
  - Get full session detail and imaging steps.
- `POST /api/sessions/couple`
  - Couple one session by passcode. Transitions session to `SessionAssigned` state. Does NOT assign an OS image; image assignment is a separate subsequent step.
- `POST /api/sessions/{sessionId}/assign`
  - Assign an OS image to a single coupled (`SessionAssigned`) session. Triggers transition to `SessionStarted` on next device poll.
- `POST /api/sessions/bulk-assign`
  - Assign one OS image to multiple `SessionAssigned` sessions simultaneously.

## OS Image Endpoints (Portal role)

- `GET /api/images`
  - List OS image catalog.
- `POST /api/images/upload-session`
  - Create a staged upload session for an OS image artifact; returns authorized SAS token URL for direct browser-to-blob upload and a chunk size.
- `POST /api/images/{imageId}/upload/complete`
  - Finalize a staged OS image upload after the blob is fully uploaded and SHA256 hash verified; makes the image available in the catalog.
- `PATCH /api/images/{imageId}`
  - Update OS image metadata.
- `DELETE /api/images/{imageId}`
  - Delete OS image metadata (blocked if in active use).

## Branding Endpoints (Portal role)

- `GET /api/branding`
  - Get branding configuration.
- `PUT /api/branding`
  - Update branding configuration.
- `GET /api/branding/logo/sas`
  - Get a time-limited read SAS token URL for the current branding logo asset in Storage. Used by the Cloud Imaging Media Builder to embed the logo during boot image generation (FR-062). Accessible by both `CloudImaging.PortalAccess` and `CloudImaging.MediaBuilderAccess` roles.

## Portal Configuration Endpoints (Portal role only)

- `GET /api/configuration`
  - Return current portal deployment configuration (`devicePreFlightAuthorizationEnabled`, `sasTokenUrlExpiryMinutes`). Available to all authenticated portal service callers; user-level read restriction is not applied at this layer.
- `PATCH /api/configuration`
  - Update portal configuration settings. The Portal backend MUST verify the calling user holds the `CloudImaging.Administrator` role before forwarding this request. Changes to `sasTokenUrlExpiryMinutes` take effect immediately for all newly issued SAS token URLs.

- `GET /api/boot-images`
  - List all active boot images and metadata. Response items include: `bootImageId`, `version`, `createdAt`, `sizeBytes`, `manifestVersion`, `sha256Hash` (of the WIM blob), and `isLatestPublished` (boolean; `true` for exactly one entry — the most recently published boot image). All active entries are eligible for USB preparation. The `isLatestPublished=true` entry is the recommended default and MUST be pre-selected in the Media Builder PrepareStorageDeviceView; the technician may choose a different active entry to use a previously known-good boot image.
- `POST /api/boot-images/{bootImageId}/sas`
  - Get time-limited SAS token URL for boot image download. Response includes `downloadUrl`, `expiresAt`, and `sha256Hash` (SHA256 hash of the WIM blob; Media Builder MUST verify the downloaded file against this value before deploying to USB partition, per FR-056).

### Portal role only (boot image lifecycle)

- `POST /api/boot-images/upload-session`
  - Create a staged upload session for a boot image artifact and return authorization for browser-to-blob upload.
- `POST /api/boot-images/{bootImageId}/upload/complete`
  - Finalize a staged boot image upload after the WIM has been fully uploaded and validated.
- `POST /api/boot-images`
  - Create/register boot image metadata.
- `PATCH /api/boot-images/{bootImageId}`
  - Update boot image metadata (display name, active state, notes).
- `DELETE /api/boot-images/{bootImageId}`
  - Delete boot image metadata/artifact references.

### Upload semantics

#### OS Image Chunked Upload Protocol

OS image uploads support chunked transfer for large files (up to 20 GB). This protocol enables resumable uploads and progress tracking:

- **Session creation**: POST /api/images/upload-session returns `uploadSessionId`, authorized SAS token URL for a temporary blob, and `chunkSize` (default: 4 MB, configurable).
- **Chunk upload**: Client uploads each 4 MB chunk via PUT with byte-range header (e.g., `Content-Range: bytes 0-4194303/*`).
- **Chunk acknowledgment**: Server responds 201 Created with `nextChunkOffset` for resume capability.
- **Upload session TTL**: 24 hours from creation. Incomplete uploads are auto-purged after TTL expiry.
- **Retry policy**: 3 exponential backoff retries per chunk (initial 1s, max 10s).
- **Progress tracking**: Client can query GET /api/images/upload-session/{uploadSessionId} to check `uploadedBytes` and `totalBytes`.
- **Integrity validation**: After all chunks uploaded, server computes SHA256 checksum and validates against client-provided hash before marking upload complete.
- **Partial upload cleanup**: Incomplete uploads are detected on server-side and not added to catalog until finalize-publish succeeds.
- **Finalize publish**: POST /api/images/{imageId}/upload/complete commits metadata and marks image active in catalog.

#### Boot Image Upload Semantics

- Boot image uploads are staged and remain unpublished until the final commit completes.
- The browser uploads the WIM directly to Blob Storage using write authorization issued through the Operator API.
- The Operator API forwards the blob write and publish commit to Imaging Core API, which owns Storage Account permissions and catalog persistence.
- The portal/backend only exposes a boot image to catalog queries after finalize publish succeeds.
- Cloud Imaging Media Builder does not upload boot images; it only generates local boot image artifacts and later downloads the most recent published boot image for USB preparation.

## Non-Responsibilities

- No direct client bootstrap for WinPE devices.
- No direct public access to Imaging Core API.
- No bypass of app-role authorization.

---

## Boot Media Certificate Endpoint (MediaBuilder role only)

> **Added 2026-06-26** — post-freeze revision for FR-068, FR-070.

- `GET /api/bootmedia/certificate/pfx`
  - Returns the current active boot media client certificate PFX bytes (certificate + private key) from Azure Key Vault via the Imaging Core API.
  - **Role**: `CloudImaging.MediaBuilderAccess` only. `CloudImaging.PortalAccess` is explicitly excluded to prevent portal code from accessing private key material.
  - **Consumer**: Media Builder Generate Boot Image workflow exclusively. The Media Builder embeds the PFX at `certificates\bootmedia.pfx` relative to the Cloud Imaging Client executable directory within the boot image WIM (FR-070).
  - **Failure behaviour**: If no active boot media certificate is configured in the Portal, the endpoint returns HTTP 404. The Media Builder MUST abort boot image generation with a clear error instructing the technician to generate a certificate first in Portal Configuration.
  - **Response**: `application/octet-stream` — raw PFX bytes. No JSON envelope; the entire response body is the PFX.
  - **Security**: Transport is Entra ID bearer token + TLS. The PFX is never logged, never included in error responses, and is not cached by the Operator API layer.

- `GET /api/bootmedia/certificate/metadata`
  - Returns non-sensitive metadata for the current active boot media certificate without PFX bytes.
  - **Role**: `CloudImaging.MediaBuilderAccess` only.
  - **Consumer**: Media Builder OperationSelectionView certificate existence check (FR-050a, FR-062). Called at startup after sign-in to determine whether the Generate Boot Image card should be enabled or disabled.
  - **Response** `200 OK`:
    - `thumbprintDisplay` (last 8 characters of SHA-256 thumbprint, for display only)
    - `subject`
    - `issuedAt`
    - `expiresAt`
  - **HTTP 404**: No active certificate is configured. The Media Builder MUST show the Generate Boot Image card as disabled with a message directing the administrator to generate a certificate in Portal Configuration.

# Operator API Contract

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

- `CloudImagingPortal`: full operator/admin operations (sessions, OS images, branding, boot image lifecycle).
- `CloudImagingMediaBuilder`: read boot images and request boot-image SAS URLs.

## App Role Authorization Matrix

| Endpoint | CloudImagingPortal | CloudImagingMediaBuilder |
|----------|-------------------|--------------------------|
| GET /api/sessions | YES | NO |
| POST /api/sessions/couple | YES | NO |
| POST /api/sessions/bulk-assign | YES | NO |
| GET /api/images | YES | NO |
| POST /api/images | YES | NO |
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

**Key distinction**: Media Builder role has read-only access to boot image queries and SAS generation. Portal role has full lifecycle (create/update/delete) for boot images and OS images.

## Base Path

`/api`

## Session Endpoints (Portal role)

- `GET /api/sessions`
  - List sessions with status filters and pagination.
- `GET /api/sessions/{sessionId}`
  - Get full session detail and imaging steps.
- `POST /api/sessions/couple`
  - Couple one session by passcode and assign OS image.
- `POST /api/sessions/bulk-assign`
  - Bulk assign image to multiple sessions.

## OS Image Endpoints (Portal role)

- `GET /api/images`
  - List OS image catalog.
- `POST /api/images`
  - Register uploaded OS image metadata.
- `PATCH /api/images/{imageId}`
  - Update OS image metadata.
- `DELETE /api/images/{imageId}`
  - Delete OS image metadata (blocked if in active use).

## Branding Endpoints (Portal role)

- `GET /api/branding`
  - Get branding configuration.
- `PUT /api/branding`
  - Update branding configuration.

## Boot Image Endpoints

- `GET /api/boot-images`
  - List available boot images and metadata.
- `POST /api/boot-images/{bootImageId}/sas`
  - Get time-limited SAS URL for boot image download.

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

OS image uploads support chunked transfer for large files (5-10 GB typical). This protocol enables resumable uploads and progress tracking:

- **Session creation**: POST /api/images/upload-session returns `uploadSessionId`, authorized SAS URL for a temporary blob, and `chunkSize` (default: 4 MB, configurable).
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

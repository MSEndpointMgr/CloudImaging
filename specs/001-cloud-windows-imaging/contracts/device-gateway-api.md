# Device Gateway API Contract

**Service**: CloudImaging.DeviceGatewayApi  
**Type**: Azure Functions v4 isolated worker (.NET 10)  
**Visibility**: Public HTTPS endpoint  
**Primary Consumer**: Cloud Imaging Client (WinPE)  
**Auth**:
- `POST /api/sessions`: no auth (bootstrap)
- All other endpoints: device-session bearer token issued at bootstrap

## Purpose

Device-facing gateway that supports WinPE clients without direct Entra ID sign-in while preserving a secure boundary to private orchestration.

## Core Responsibilities

- Session bootstrap for device clients.
- Device-session token issuance and validation.
- Poll/status relay to Imaging Core API.
- Progress relay to Imaging Core API.
- SAS token URL refresh relay to Imaging Core API.
- ProblemDetails error responses for device clients.

## Base Path

`/api/v1`

## Endpoints

### 1) Create Session

- `POST /api/v1/sessions`
- Purpose: Register a device session and return one-time pairing passcode + device-session token.
- Request body:
  - `deviceInfo.manufacturer` (sourced from Win32_ComputerSystem.Manufacturer)
  - `deviceInfo.model` (sourced from Win32_ComputerSystem.Model)
  - `deviceInfo.serialNumber` (sourced from Win32_BIOS.SerialNumber)
  - `deviceInfo.macAddress`
  - `deviceInfo.hardware` (hardware metadata collected silently at session init for audit; all sub-fields nullable if unavailable in WinPE)
    - `motherboardManufacturer`
    - `motherboardModel`
    - `biosVersion`
    - `nicIdentifiers` (array of strings)
    - `storageLayout` (array of storage device descriptor strings)
- Response `201 Created`:
  - `sessionId`
  - `status` (`SessionInit`, `SessionAllowed`, or `SessionNotAuthorized`)
  - `passcode` (one-time pairing code; `null` when `status` is `SessionNotAuthorized`)
  - `deviceSessionToken` (`null` when `status` is `SessionNotAuthorized`)
  - `deviceSessionTokenExpiresAt` (`null` when `status` is `SessionNotAuthorized`)
  - `errorDetail` (`null` unless `status` is `SessionNotAuthorized`; describes authorization failure)

  When `status` is `SessionNotAuthorized`, the session is in a terminal not-authorized state. The Cloud Imaging Client MUST transition immediately to ResultsView with the Not Authorized outcome; no further polling is required or permitted.

### 2) Get Session Status

- `GET /api/v1/sessions/{sessionId}/status`
- Purpose: Poll lifecycle and assignment status.
- Auth: device-session bearer token (must match sessionId).
- Response `200 OK`:
  - `sessionId`
  - `status`
  - `overallProgressPercent` (0-100; session-level imaging completion percentage)
  - `currentStep` (nullable; current imaging step name)
  - `assignedImage` (nullable; when present includes):
    - `imageId`
    - `downloadUrl`
    - `sha256Hash` (for cache validation against locally stored images)
    - `size` (in bytes)
    - `version`
  - `sasTokenUrl` (nullable; refreshed SAS token URL when a renewed credential is available from polling)
  - `errorDetail` (nullable)

### 2a) Validate Cached Image Hash

- `POST /api/v1/sessions/{sessionId}/cache/validate`
- Purpose: Report cached image hash for validation; endpoint confirms whether cached image matches API version.
- Auth: device-session bearer token.
- Request body:
  - `imageId`
  - `cachedSha256Hash`
- Response `200 OK`:
  - `isValid` (boolean; true if cached hash matches API record for imageId)
  - `currentSha256Hash` (if isValid=false, contains current hash to download)

### 3) Report Progress

- `POST /api/v1/sessions/{sessionId}/progress`
- Purpose: Persist client step transitions and progress percentages.
- Auth: device-session bearer token.
- Request body:
  - `stepName`
  - `status`
  - `progressPercent` (optional)
  - `errorDetail` (optional)
- Response: `204 No Content`

### 4) Refresh SAS Token URL

- `POST /api/v1/sessions/{sessionId}/sas/refresh`
- Purpose: Request refreshed SAS token URL during long-running downloads.
- Auth: device-session bearer token.
- Response `200 OK`:
  - `downloadUrl`
  - `expiresAt`

## Session Timeout Semantics

- **Pre-imaging states** (SessionInit, SessionAllowed, SessionAssigned): Expire after 30 minutes of **idle time** (no GET /status poll).
- **Active imaging states** (SessionStarted, SessionInProgress): Fail and auto-transition to SessionFailed after more than 2 hours **without poll heartbeat** (30-second polling window).
  - Definition of poll heartbeat: Successfully received GET /status request with valid device-session token; response sent within 30 seconds.
  - Missed heartbeat threshold: Two consecutive 30-second polling windows with no valid GET /status request.
- **Terminal states** (SessionCompleted, SessionFailed): Persisted for 24 hours, then auto-purged from storage.
- **One-time pairing passcode**: Invalidated immediately upon successful couple operation or 30 minutes after SessionInit creation, whichever occurs first.

## SAS Token URL Refresh Behavior

- **Refresh threshold**: Mandatory refresh requested when remaining SAS token URL lifetime < 15 minutes.
- **Client obligation**: Client MUST call POST /api/v1/sessions/{sessionId}/sas/refresh when SAS token URL expiry is detected at <= 15 minutes remaining.
- **Refresh endpoint duty**: Device Gateway API MUST forward the refresh request to Imaging Core API and return a refreshed SAS token URL with new expiry time.
- **Expired SAS handling**: If client receives 401/403 on blob download (SAS token URL expired), client MUST request refresh immediately and retry download.
- **No SAS token URL reuse**: Once a SAS token URL is issued, it is single-use for the download operation; expired SAS token URLs are not re-issued.

## OS Image Cache Validation Semantics

- **Cache metadata**: Each cached OS image on the USB cache partition MUST store: image ID, version, SHA256 hash, cached timestamp, size in bytes, and last-accessed timestamp.
- **Pre-download cache check**: Client MUST check for cached image matching the assigned image ID and version **before** requesting download. If found, client MUST call POST /api/v1/sessions/{sessionId}/cache/validate with cached hash.
- **Cache hit**: If POST /cache/validate returns `isValid=true`, download is **skipped**; client proceeds with remaining imaging workflow steps (format, apply, etc.) using cached image. No SAS token URL is needed.
- **Cache miss / hash mismatch**: If POST /cache/validate returns `isValid=false`, or cached image is unreadable, cached entry is deleted; client requests a fresh SAS token URL and downloads from API.
- **Hash storage**: SHA256 hash returned in GET /status response (assignedImage.sha256Hash) MUST be persisted with cached image for future cache-hit validation.
- **Cache eviction**: If insufficient space is available to store a new OS image after cache cleanup, the Client MUST skip cache storage for that session and proceed with a direct download without error; no LRU eviction of existing valid cache entries is performed (FR-009d).
- **Cache expiry**: Cached images older than 30 days MUST be auto-purged on next boot. Catalog-ID-based orphan detection (removing entries for image IDs no longer in the active catalog) is deferred to v2.

## Rate Limiting (FR-018)

Per-session rate limiting is enforced on all authenticated Device Gateway API endpoints. The public bootstrap endpoint (`POST /api/v1/sessions`) is exempt.

- **Limit**: 10 API calls per 30-second sliding window per device-session token.
- **Exceeded**: `429 Too Many Requests` with `Retry-After` header (value in seconds).
- **Scope**: Limit is per device-session token; separate sessions have independent counters.

## Non-Responsibilities

- No direct Storage Account blob read/write.
- No operator-facing image or branding management.
- No public exposure of Imaging Core API details.

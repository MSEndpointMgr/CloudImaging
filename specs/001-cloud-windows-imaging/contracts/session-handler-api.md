# SessionHandler API Contract

> Legacy / Superseded
>
> This document describes the pre-refactor SessionHandler design and is kept
> only for migration context. It is not the source of truth for current
> implementation.
>
> Its responsibilities are now split across:
> - `operator-api.md`
> - `imaging-core-api.md`
> - `api-overview.md`

**Service**: `CloudImaging.SessionHandler`
**Type**: Azure Function App v4, isolated worker, .NET 10
**Visibility**: Private -- accessible only via Private Endpoint (no public network access, FR-020)
**Consumers**: SessionBroker (session and progress relay), Admin Portal Express backend (coupling, assignment, image CRUD, branding)
**Auth**: Entra ID bearer token; caller's managed identity must hold the relevant App Role:
  - `SessionBroker` role: session relay and progress endpoints
  - `AdminPortal` role: coupling, assignment, image management, branding endpoints
**Base path**: `https://{sessionhandler-private-hostname}/api`

All request and response bodies are `application/json`.
All datetime fields are ISO 8601 UTC strings.
Error responses use RFC 7807 `ProblemDetails`.

---

## Session Endpoints

### POST /sessions

Called by SessionBroker to create a new session record. SessionBroker forwards
the result (passcode, sessionId) back to the WPF client.

**Required role**: `SessionBroker`

**Request body**

```json
{
  "deviceInfo": {
    "model": "string | null",
    "serialNumber": "string | null",
    "macAddress": "string | null"
  }
}
```

**Response 201 Created**

```json
{
  "sessionId": "uuid",
  "passcode": "string (6 chars)"
}
```

Passcode uniqueness is enforced here. Returns `409 Conflict` if a unique
passcode cannot be generated within 5 attempts (extremely unlikely).

---

### GET /sessions

List sessions. Supports filtering by status. Used by the Admin Portal to
populate the session dashboard.

**Required role**: `AdminPortal`

**Query parameters**:
- `status` (optional): one of `Waiting`, `Coupled`, `Imaging`, `Completed`, `Failed`
- `limit` (optional, default 100, max 500)
- `continuationToken` (optional): for pagination via Table Storage continuation

**Response 200 OK**

```json
{
  "sessions": [
    {
      "sessionId": "uuid",
      "passcode": "string",
      "status": "Waiting",
      "assignedImageId": "uuid | null",
      "assignedImageName": "string | null",
      "createdAt": "2026-06-14T10:00:00Z",
      "coupledAt": "2026-06-14T10:05:00Z | null",
      "imagingStartedAt": "2026-06-14T10:06:00Z | null",
      "completedAt": "2026-06-14T10:20:00Z | null",
      "lastProgressAt": "2026-06-14T10:12:00Z | null",
      "currentStep": "string | null",
      "errorDetail": "string | null",
      "deviceInfo": { "model": "string | null", "serialNumber": "string | null", "macAddress": "string | null" }
    }
  ],
  "continuationToken": "string | null"
}
```

---

### GET /sessions/{sessionId}

Get full session detail including all imaging steps.

**Required role**: `SessionBroker` or `AdminPortal`

**Response 200 OK**

```json
{
  "sessionId": "uuid",
  "passcode": "string",
  "status": "string",
  "assignedImageId": "uuid | null",
  "assignedImageName": "string | null",
  "createdAt": "datetime",
  "coupledAt": "datetime | null",
  "imagingStartedAt": "datetime | null",
  "completedAt": "datetime | null",
  "lastProgressAt": "datetime | null",
  "errorDetail": "string | null",
  "deviceInfo": {},
  "steps": [
    {
      "stepName": "string",
      "status": "Pending | InProgress | Completed | Failed",
      "startedAt": "datetime | null",
      "completedAt": "datetime | null",
      "errorDetail": "string | null"
    }
  ]
}
```

---

### PATCH /sessions/{sessionId}/couple

Couple an uncoupled session to a technician-entered passcode and assign an OS
image. Called by Admin Portal Express backend.

**Required role**: `AdminPortal`

**Request body**

```json
{
  "passcode": "string",
  "imageId": "uuid"
}
```

Passcode must match the session's stored passcode and the session must be in
`Waiting` status. This is an optimistic-lock operation: if two requests arrive
simultaneously for the same passcode, the first succeeds and the second returns
`409 Conflict`.

**Response 200 OK**

```json
{
  "sessionId": "uuid",
  "status": "Coupled",
  "credential": {
    "downloadUrl": "string (SAS token URL)",
    "expiresAt": "datetime",
    "fileSizeBytes": 0
  }
}
```

**Errors**
- `404 Not Found` -- passcode does not match any Waiting session
- `409 Conflict` -- session already coupled or passcode claimed by concurrent request
- `422 Unprocessable Entity` -- imageId does not reference an active OSImage

---

### POST /sessions/{sessionId}/credential/refresh

Regenerate a SAS token URL for an active imaging session. Called by SessionBroker
on behalf of the WPF client.

**Required role**: `SessionBroker`

**Response 200 OK** -- same shape as the `credential` object in PATCH /couple.

**Errors**
- `404 Not Found`
- `409 Conflict` -- session not in Imaging status

---

### POST /sessions/{sessionId}/progress

Record a step status update relayed from the WPF client via SessionBroker.

**Required role**: `SessionBroker`

**Request body**

```json
{
  "stepName": "string",
  "status": "InProgress | Completed | Failed",
  "errorDetail": "string | null"
}
```

**Response 204 No Content**

**Errors**
- `404 Not Found`
- `422 Unprocessable Entity` -- invalid step transition

---

## OS Image Endpoints

### GET /images

List all OS images in the catalog.

**Required role**: `AdminPortal`

**Query parameters**:
- `activeOnly` (bool, default `true`)

**Response 200 OK**

```json
{
  "images": [
    {
      "imageId": "uuid",
      "name": "string",
      "version": "string",
      "description": "string",
      "fileSizeBytes": 0,
      "uploadedAt": "datetime",
      "uploadedBy": "string",
      "activeSessionCount": 0,
      "isActive": true
    }
  ]
}
```

---

### POST /images

Register a new OS image after the blob has been uploaded to Blob Storage.
The Admin Portal backend uploads the blob directly using its managed identity,
then calls this endpoint to register the metadata.

**Required role**: `AdminPortal`

**Request body**

```json
{
  "name": "string",
  "version": "string",
  "description": "string",
  "blobContainerName": "string",
  "blobName": "string",
  "fileSizeBytes": 0
}
```

**Response 201 Created** -- returns the full OSImageMetadata object.

---

### PATCH /images/{imageId}

Update OS image metadata (name, version, description, isActive).

**Required role**: `AdminPortal`

**Request body** (all fields optional; send only fields to update)

```json
{
  "name": "string",
  "version": "string",
  "description": "string",
  "isActive": true
}
```

**Response 200 OK** -- returns updated OSImageMetadata.

---

### DELETE /images/{imageId}

Delete an OS image. Blocked when `activeSessionCount > 0`.

**Required role**: `AdminPortal`

**Response 204 No Content**

**Errors**
- `404 Not Found`
- `409 Conflict` -- image is assigned to one or more active sessions

---

## Branding Endpoints

All colour values are 6-digit hex strings (e.g. `#0078D4`), not HSL -- this
matches `CloudImaging.Contracts.Models.BrandingConfiguration` and the CSS
custom-property scheme used by the portal client (RGB triples derived from
hex at runtime). Logo blob paths are managed exclusively by the dedicated
upload/delete endpoints below and are never accepted on `PUT /branding`.

### GET /branding

Return current branding configuration.

**Required role**: `CloudImaging.PortalAccess`

**Response 200 OK**

```json
{
  "logoBlobPath": "string | null",
  "portalLogoBlobPath": "string | null",
  "primaryColor": "#0078d4",
  "accentColor": "#005a9e",
  "applicationName": "Cloud Imaging",
  "sidebarBackgroundLight": "#f8fafc",
  "sidebarBackgroundDark": "#0d1321",
  "cardBackgroundLight": "#ffffff",
  "cardBackgroundDark": "#0c121f",
  "pageBackgroundLight": "#ffffff",
  "pageBackgroundDark": "#080c16",
  "headerBackgroundLight": "#ffffff",
  "headerBackgroundDark": "#080c16"
}
```

---

### PUT /branding

Replace the colour/name portion of the branding configuration. `logoBlobPath`
and `portalLogoBlobPath` are preserved server-side from the existing record --
a colour/name save can never clear a configured logo.

**Required role**: `CloudImaging.Administrator`

**Request body** -- all 11 fields are required; each colour is validated
against `^#[0-9a-fA-F]{6}$` and `applicationName` must be 1-100 characters
after trimming.

```json
{
  "primaryColor": "#0078d4",
  "accentColor": "#005a9e",
  "applicationName": "Cloud Imaging",
  "sidebarBackgroundLight": "#f8fafc",
  "sidebarBackgroundDark": "#0d1321",
  "cardBackgroundLight": "#ffffff",
  "cardBackgroundDark": "#0c121f",
  "pageBackgroundLight": "#ffffff",
  "pageBackgroundDark": "#080c16",
  "headerBackgroundLight": "#ffffff",
  "headerBackgroundDark": "#080c16"
}
```

**Response 204 No Content**

**Errors**
- `400 Bad Request` -- a colour is not a valid 6-digit hex string, or
  `applicationName` is out of range

---

### Logo management

Two independent logos are supported: the **boot image logo** (embedded into
boot media by the Media Builder, downloaded via a time-limited SAS URL) and
the **portal logo** (shown in the portal header/sidebar, streamed directly
through the backend with no SAS).

- `PUT /branding/logo` / `PUT /branding/portal-logo` -- body
  `{ "fileName": "string", "contentType": "string", "dataBase64": "string" }`
  (max 512 KB decoded); raster images are normalized server-side to a
  256px-longest-side canonical size. Returns the updated branding object.
- `GET /branding/logo/content` / `GET /branding/portal-logo/content` -- streams
  the current logo bytes.
- `GET /branding/logo/sas` -- issues a SAS URL for the boot image logo
  (consumed by the Media Builder); `404 Not Found` if no logo is configured.
- `DELETE /branding/logo` / `DELETE /branding/portal-logo` -- clears the
  respective logo, reverting to the built-in default artwork.

**Required role**: `CloudImaging.Administrator` for all logo mutations;
`CloudImaging.PortalAccess` for reads.

# Admin Portal API Contract

**Service**: `admin-portal/server` (Node.js + Express + TypeScript)
**Visibility**: Private -- served only to the React frontend via Azure Static Web Apps linked backend, or direct App Service URL for development
**Consumers**: Admin Portal React frontend (`client/`)
**Auth**: MSAL bearer token (Entra ID); validated in Express middleware on every endpoint except `GET /api/health` and `GET /api/branding/public`
**Base path**: `/api`

All request and response bodies are `application/json`.
All datetime fields are ISO 8601 UTC strings.
Errors follow the same RFC 7807 `ProblemDetails` shape used by the .NET APIs,
re-serialized by the Express error handler.

---

## Auth Endpoints

### GET /api/auth/me

Return the authenticated user's profile (name, email, roles).

**Response 200 OK**

```json
{
  "userId": "string (Entra OID)",
  "displayName": "string",
  "email": "string"
}
```

---

## Session Endpoints

### GET /api/sessions

List sessions. Proxies to SessionHandler `GET /sessions`.
Responses are cached server-side for 1 second to reduce load under concurrent
portal users polling simultaneously.

**Query parameters**: same as SessionHandler (`status`, `limit`, `continuationToken`)

**Response 200 OK** -- same shape as SessionHandler `GET /sessions`.

---

### GET /api/sessions/{sessionId}

Get full session detail including steps. Proxies to SessionHandler `GET /sessions/{sessionId}`.

**Response 200 OK** -- same shape as SessionHandler `GET /sessions/{sessionId}`.

---

### POST /api/sessions/couple

Couple a session by entering a passcode and assigning an OS image.
Proxies to SessionHandler `PATCH /sessions/{sessionId}/couple` after resolving
the sessionId from the passcode lookup.

**Request body**

```json
{
  "passcode": "string",
  "imageId": "uuid"
}
```

**Response 200 OK**

```json
{
  "sessionId": "uuid",
  "status": "Coupled"
}
```

**Errors**
- `404 Not Found` -- passcode not found
- `409 Conflict` -- passcode already coupled
- `422 Unprocessable Entity` -- imageId invalid

---

### POST /api/sessions/bulk-assign

Assign an OS image to multiple sessions simultaneously (bulk operation).

**Request body**

```json
{
  "passcodes": ["A3K7PX", "B9M2QR"],
  "imageId": "uuid"
}
```

Each passcode is coupled independently. Partial success is supported: the
response reports per-passcode results.

**Response 200 OK**

```json
{
  "results": [
    {
      "passcode": "A3K7PX",
      "sessionId": "uuid | null",
      "status": "Coupled | Failed",
      "error": "string | null"
    }
  ]
}
```

---

## Image Management Endpoints

### GET /api/images

List all OS images. Proxies to SessionHandler `GET /images`.

**Query parameters**: `activeOnly` (bool, default `true`)

**Response 200 OK** -- same shape as SessionHandler `GET /images`.

---

### POST /api/images/upload

Upload a new OS image. Two-phase operation:
1. Express backend streams the upload to Azure Blob Storage using its own
   managed identity (server-side upload; no SAS token exposed to the browser).
2. On upload completion, Express calls SessionHandler `POST /images` to register
   the metadata.

**Request**: `multipart/form-data`
- `file`: the image file (.wim or .esd, max 10 GB)
- `name`: string
- `version`: string
- `description`: string

**Response 201 Created** -- returns the registered OSImageMetadata object.

**Errors**
- `413 Content Too Large` -- file exceeds 10 GB
- `415 Unsupported Media Type` -- file extension not `.wim` or `.esd`

---

### PATCH /api/images/{imageId}

Update image metadata. Proxies to SessionHandler `PATCH /images/{imageId}`.

**Request body**: same as SessionHandler.

**Response 200 OK**

---

### DELETE /api/images/{imageId}

Delete an image. Proxies to SessionHandler `DELETE /images/{imageId}`.
If SessionHandler returns 204, Express also deletes the blob from Blob Storage.

**Response 204 No Content**

**Errors**
- `409 Conflict` -- image in active use

---

## Branding Endpoints

### GET /api/branding/public

Return the current branding configuration. **No authentication required.**
Called by the React frontend on app initialisation before auth to set CSS
variables and application name.

**Response 200 OK** -- same shape as SessionHandler `GET /branding`.

---

### PUT /api/branding

Update branding. If a logo file is provided it is uploaded to Blob Storage
server-side before the metadata is persisted.

**Request**: `multipart/form-data`
- `applicationName`: string
- `primaryColorHsl`: string (e.g., `"210 100% 50%"`)
- `accentColorHsl`: string
- `logoFile` (optional): image file (PNG or SVG, max 2 MB)

**Response 200 OK** -- returns updated branding object.

**Errors**
- `415 Unsupported Media Type` -- logo file not PNG or SVG
- `413 Content Too Large` -- logo exceeds 2 MB

---

## Health Endpoint

### GET /api/health

Returns service health. **No authentication required.** Used by Azure App
Service health probes.

**Response 200 OK**

```json
{
  "status": "healthy",
  "timestamp": "datetime"
}
```

---

## Error Response Shape

Express error middleware re-serializes all errors in RFC 7807 ProblemDetails
format for consistency with the .NET API contracts:

```json
{
  "type": "https://cloudimaging.io/errors/{error-code}",
  "title": "string",
  "status": 404,
  "detail": "string",
  "instance": "/api/sessions/couple"
}
```

Frontend code MUST parse error responses by reading `status` and `detail`
from the ProblemDetails shape, not by assuming any other error structure.

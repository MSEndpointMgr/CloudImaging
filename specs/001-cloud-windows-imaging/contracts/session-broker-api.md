# SessionBroker API Contract

**Service**: `CloudImaging.SessionBroker`
**Type**: Azure Function App v4, isolated worker, .NET 10
**Visibility**: Public HTTPS endpoint
**Consumers**: WPF Client (all endpoints), Admin Portal Express backend (none directly -- admin ops go via Admin Portal API to SessionHandler)
**Auth**: Session Bearer token (JWT, issued by this service) on all endpoints except `POST /sessions`
**Base path**: `https://{sessionbroker-hostname}/api`

All request and response bodies are `application/json`.
All datetime fields are ISO 8601 UTC strings.
Error responses use RFC 7807 `ProblemDetails`.

---

## Endpoints

### POST /sessions

Register a new imaging session. Called by the WPF client on startup before any
passcode is shown. No authentication required.

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

`deviceInfo` is optional; all sub-fields are optional. WPF client sends
whatever it can read from the WinPE environment.

**Response 201 Created**

```json
{
  "sessionId": "uuid",
  "passcode": "string (6 chars, e.g. 'A3K7PX')",
  "sessionToken": "string (Bearer JWT)",
  "sessionTokenExpiresAt": "2026-06-14T14:00:00Z",
  "status": "Waiting"
}
```

`sessionToken` must be stored by the WPF client and sent as
`Authorization: Bearer {token}` on all subsequent requests.

**Errors**
- `503 Service Unavailable` -- SessionHandler unreachable

---

### GET /sessions/{sessionId}/status

Poll for the current status of a session. WPF client calls this on a
configurable interval (default 3 seconds) while waiting for coupling and
assignment.

**Path parameters**: `sessionId` (UUID)

**Auth**: Bearer session token (must match `sessionId`)

**Response 200 OK**

```json
{
  "sessionId": "uuid",
  "status": "Waiting | Coupled | Imaging | Completed | Failed",
  "assignedImage": {
    "imageId": "uuid",
    "name": "string",
    "fileSizeBytes": 0
  },
  "credential": {
    "downloadUrl": "string (SAS URL)",
    "expiresAt": "2026-06-14T18:00:00Z",
    "fileSizeBytes": 0
  },
  "errorDetail": "string | null"
}
```

`assignedImage` and `credential` are `null` when status is `Waiting`.
`credential` is populated once status transitions to `Coupled`.

**Errors**
- `401 Unauthorized` -- missing or invalid session token
- `403 Forbidden` -- token sessionId does not match path sessionId
- `404 Not Found` -- session does not exist or has expired

---

### POST /sessions/{sessionId}/progress

Report an imaging step status update. Called by the WPF client at each step
transition. All calls are off the WPF UI thread.

**Path parameters**: `sessionId` (UUID)

**Auth**: Bearer session token

**Request body**

```json
{
  "stepName": "DownloadStarted | DownloadCompleted | ApplyStarted | ApplyCompleted | Finalizing | Complete | Failed",
  "status": "InProgress | Completed | Failed",
  "errorDetail": "string | null"
}
```

**Response 204 No Content** on success.

**Errors**
- `401 Unauthorized`
- `403 Forbidden`
- `404 Not Found` -- session not found or not in Imaging status
- `422 Unprocessable Entity` -- invalid stepName or status transition

---

### POST /sessions/{sessionId}/credential/refresh

Request a fresh SAS token when the current credential is approaching expiry.
WPF client calls this when the credential has less than 30 minutes remaining.

**Path parameters**: `sessionId` (UUID)

**Auth**: Bearer session token

**Response 200 OK**

```json
{
  "downloadUrl": "string (new SAS URL)",
  "expiresAt": "2026-06-14T22:00:00Z",
  "fileSizeBytes": 0
}
```

**Errors**
- `401 Unauthorized`
- `403 Forbidden`
- `404 Not Found`
- `409 Conflict` -- session is not in Imaging status (credential refresh only valid during active imaging)

---

## Error Response Shape (ProblemDetails)

All error responses follow RFC 7807:

```json
{
  "type": "https://cloudimaging.io/errors/{error-code}",
  "title": "Human-readable error title",
  "status": 404,
  "detail": "Detailed message safe to show to a developer",
  "instance": "/api/sessions/{sessionId}/status"
}
```

---

## OpenAPI

SessionBroker exposes an OpenAPI v3 document at `GET /api/openapi.json`.
A CI step runs `npx openapi-typescript` against this endpoint to generate
`client/src/types/api.generated.ts` and `server/src/types/api.generated.ts`
in the admin-portal tree. Do not edit the generated files manually.

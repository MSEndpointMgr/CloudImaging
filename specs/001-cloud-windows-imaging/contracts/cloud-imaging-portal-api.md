# Cloud Imaging Portal API Contract

> Current authoritative portal-backend contract.
>
> **Contract Revision — 2026-06-26** (post-freeze): Added boot media client certificate management endpoints introduced by FR-068, FR-069, FR-070, FR-071. Per contract freeze policy this constitutes a versioned revision; all implementation MUST target these updated semantics.

**Service**: cloud-imaging-portal/server (Node.js + Express + TypeScript)  
**Visibility**: Private backend for portal frontend  
**Primary Consumer**: cloud-imaging-portal/client  
**Auth**: Entra ID session/token validation on protected routes

## Purpose

Defines frontend-to-portal-backend endpoints. The backend then calls Operator API for business operations.

## Core Responsibilities

- Validate portal user authentication context.
- Enforce backend-side authorization checks before calling Operator API.
- Normalize and return ProblemDetails-compatible error responses.
- Keep browser clients isolated from direct calls to Imaging Core API.

## Base Path

`/api`

## Auth and Health

- `GET /api/auth/me`
  - Return authenticated user profile and role claims.
- `GET /api/health`
  - Health probe endpoint.

## Session Operations

- `GET /api/sessions`
- `GET /api/sessions/{sessionId}`
- `POST /api/sessions/couple`
- `POST /api/sessions/{sessionId}/assign`
- `POST /api/sessions/bulk-assign`

These routes proxy to Operator API session endpoints and surface the current session state, per-step status, and overall imaging completion percentage in the portal UI.

`GET /api/sessions` supports a `filter` query parameter corresponding to the Portal UI filter tabs:
- `filter=active` (default): `SessionAllowed`, `SessionAssigned`, `SessionStarted`, `SessionInProgress`
- `filter=completed`: `SessionCompleted`
- `filter=failed`: `SessionFailed`, `SessionNotAuthorized`
- `filter=all`: all sessions regardless of state

Response includes per-group counts for real-time tab badge display.

## OS Image Operations

- `GET /api/images`
- `POST /api/images/upload`
- `PATCH /api/images/{imageId}`
- `DELETE /api/images/{imageId}`

Metadata operations proxy through Operator API.

Upload flow expectations:

- `POST /api/images/upload` creates a staged upload session for an OS image artifact.
- The browser uploads the OS image to Blob Storage in chunks through the portal backend, with progress and retry state visible in the UI.
- The upload session uses a 4 MB default chunk size, 24-hour session TTL, and exponential retry for interrupted chunks.
- The backend validates the final SHA256 checksum and only then calls the publish finalize step through Operator API.
- The uploaded OS image remains unpublished and hidden from catalog queries until finalize publish completes.

## Boot Image Operations

- `GET /api/boot-images`
- `POST /api/boot-images/upload`
- `PATCH /api/boot-images/{bootImageId}`
- `DELETE /api/boot-images/{bootImageId}`

These routes support portal-side boot image lifecycle management and align with FR-063.

Upload flow expectations:

- `POST /api/boot-images/upload` creates a staged upload session for a WIM artifact.
- The browser uploads the WIM directly to Blob Storage in chunks and shows progress and retry state.
- The upload session uses the same resumable chunked-transfer expectations as OS image uploads.
- The backend only calls the publish finalize step after the upload commits and validation succeeds.
- The uploaded boot image remains unpublished and hidden from catalog queries until finalize publish completes.

## Branding Operations

- `GET /api/branding/public` (public read)
- `GET /api/branding`
- `PUT /api/branding`

## Portal Configuration Operations

- `GET /api/configuration`
  - Return current portal deployment configuration (`devicePreFlightAuthorizationEnabled`, `sasTokenUrlExpiryMinutes`). Available to all authenticated portal users.
- `PATCH /api/configuration`
  - Update portal configuration settings. Restricted to users with the `CloudImaging.Administrator` role; backend enforces role check before calling Operator API.

## Error Shape

All error payloads return ProblemDetails-compatible structure:

```json
{
  "type": "https://cloudimaging.io/errors/{error-code}",
  "title": "string",
  "status": 400,
  "detail": "string",
  "instance": "/api/..."
}
```

## Non-Responsibilities

- No direct browser call path to Imaging Core API.
- No device bootstrap operations.
- No bypass of Operator API authorization boundaries.

---

## Boot Media Certificate Management Operations

> **Added 2026-06-26** — post-freeze revision for FR-068.

All three endpoints are restricted to users holding the `CloudImaging.Administrator` role. The portal backend MUST enforce this role check before proxying to Operator API. No `CloudImaging.Technician` access is permitted.

- `GET /api/cert/active`
  - Return active boot media certificate metadata: `thumbprint` (last 8 characters for display), `subject`, `issuedAt`, `expiresAt`, `issuedBy` (portal user identity).
  - **Does not return PFX bytes or private key material under any circumstance.**
  - Returns HTTP 404 with a descriptive message when no active certificate is configured, prompting the administrator to generate one.

- `POST /api/cert/generate`
  - Generate a new self-signed boot media client certificate with the validity period from `PortalConfiguration.certValidityPeriodDays` (default: 365 days).
  - Atomically activates the new certificate and invalidates the prior active certificate in a single operation; there is no grace period or overlap window.
  - **Warning**: all USB boot media prepared with the previous certificate will stop working immediately after this call completes. The portal frontend MUST display this confirmation warning and require explicit user acknowledgement before calling this endpoint.
  - Request body: `{ "confirmReplacement": true }` — the backend MUST validate this flag is present and `true` before forwarding.
  - Returns the metadata of the newly activated certificate (same shape as `GET /api/cert/active`).

- `POST /api/cert/rotate`
  - Identical semantics to `POST /api/cert/generate`. Exposed as a separate path to distinguish intentional rotation (admin workflow) from initial generation (first-time setup) in audit logs and UI labeling.
  - Subject to the same confirmation-flag requirement and the same immediate-invalidation behaviour.
  - Returns the metadata of the newly activated certificate.

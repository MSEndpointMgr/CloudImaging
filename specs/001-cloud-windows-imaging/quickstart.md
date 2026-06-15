# Quickstart Validation Guide: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging
**Date**: 2026-06-14
**Purpose**: Runnable scenarios that prove the feature works end-to-end.
  This is a validation guide, not an implementation guide.
  For data shapes see [data-model.md](data-model.md).
  For API endpoints see [contracts/](contracts/).

---

## Prerequisites

### Infrastructure

The following Azure resources must be provisioned before running any scenario:

| Resource | Purpose |
|----------|---------|
| Azure Storage Account (Standard LRS) | OS image blobs + Table Storage |
| Azure Function App -- SessionBroker | Flex Consumption, .NET 10, public endpoint |
| Azure Function App -- SessionHandler | Premium EP1, .NET 10, VNet-integrated |
| Azure App Service (Linux, Node.js 22) | Admin Portal backend, VNet-integrated |
| Azure Static Web Apps | Admin Portal frontend |
| Azure Virtual Network | Shared VNet for Private Link topology |
| Private Endpoint (SessionHandler) | Restrict SessionHandler to VNet only |
| Entra ID App Registrations (x2) | SessionBroker app + Admin Portal app |
| Managed Identities (x2) | SessionBroker identity + Admin Portal backend identity |

A `README.md` in the repository root will document the one-time Terraform/Bicep
provisioning steps for self-hosters.

### Local Development Setup

```bash
# .NET projects (SessionBroker, SessionHandler, WpfClient)
dotnet restore CloudImaging.sln
dotnet build CloudImaging.sln -c Release

# Admin Portal
cd src/admin-portal
npm install --workspaces

# Environment variables
# Copy and fill: src/admin-portal/server/.env.example -> .env
# Copy and fill: src/admin-portal/client/.env.example -> .env
```

Local Function App development uses Azurite for Table Storage and Blob Storage
emulation. Local Admin Portal development uses `vite dev` + `nodemon`.

---

## Validation Scenario 1: Device Session Registration

**Validates**: FR-001, FR-002, FR-010, US1

**Setup**: SessionBroker and SessionHandler running (local or staging).

**Steps**:

1. Send a `POST /api/sessions` to SessionBroker with an empty `deviceInfo`:
   ```bash
   curl -s -X POST https://{sessionbroker}/api/sessions \
     -H "Content-Type: application/json" \
     -d '{"deviceInfo": {}}'
   ```

2. Confirm the response contains:
   - `sessionId` (UUID)
   - `passcode` (exactly 6 uppercase alphanumeric characters, no O/0/I/1/L)
   - `sessionToken` (JWT, decodable -- `sub` claim equals `sessionId`)
   - `status: "Waiting"`

3. Query SessionHandler directly (from a VNet-connected machine or via the
   Admin Portal API) for `GET /api/sessions?status=Waiting` and confirm the
   new session appears.

**Expected outcome**: Session record exists in `DeviceSessions` Table Storage
with status `Waiting`.

---

## Validation Scenario 2: Portal Session Dashboard

**Validates**: FR-030, FR-031, US2 (partial)

**Setup**: At least one `Waiting` session exists (run Scenario 1 first).
Admin Portal frontend + backend running.

**Steps**:

1. Open the Admin Portal in a browser. Authenticate with an Entra ID account.
2. Navigate to the Sessions dashboard.
3. Confirm the `Waiting` session from Scenario 1 appears with its passcode
   visible (masked if necessary) and status `Waiting`.
4. Confirm the list updates within 2-3 seconds of a new session being created
   (React Query polling).

**Expected outcome**: Session list shows real-time data without a page reload.

---

## Validation Scenario 3: Device Coupling via Passcode

**Validates**: FR-032, FR-033, FR-020, FR-021, FR-022, US2

**Setup**: One `Waiting` session with a known passcode. At least one OS image
in the catalog.

**Steps**:

1. In the Admin Portal, open the "Couple Device" flow.
2. Enter the passcode shown in Scenario 1.
3. Select an OS image from the catalog dropdown.
4. Confirm the action.
5. Observe: session transitions to `Coupled` in the dashboard.
6. Poll the WPF client side: `GET /api/sessions/{sessionId}/status` -- confirm
   response `status: "Coupled"` and `credential.downloadUrl` is a non-empty
   SAS URL.
7. Decode the SAS URL and confirm `se` (expiry) is approximately 4 hours in
   the future.

**Expected outcome**: Session is `Coupled`; SAS token is valid and time-limited.

---

## Validation Scenario 4: Concurrent Coupling Race Condition

**Validates**: Edge case -- two technicians attempt to couple the same passcode.

**Steps**:

1. Obtain a `Waiting` session passcode.
2. Send two simultaneous `POST /api/sessions/couple` requests with the same
   passcode to the Admin Portal backend (use `curl --parallel` or a test script).
3. Confirm exactly one request returns `200 OK` and the other returns
   `409 Conflict`.
4. Confirm the session is `Coupled` exactly once in Table Storage.

**Expected outcome**: No duplicate coupling; second request fails gracefully.

---

## Validation Scenario 5: WPF Client Progress Reporting

**Validates**: FR-005, FR-006, FR-007, FR-034, US3

**Setup**: One session in `Coupled` status with an assigned image and a valid
SAS credential.

**Steps**:

1. Simulate the WPF client beginning imaging by calling:
   ```bash
   curl -s -X POST https://{sessionbroker}/api/sessions/{sessionId}/progress \
     -H "Authorization: Bearer {sessionToken}" \
     -H "Content-Type: application/json" \
     -d '{"stepName": "DownloadStarted", "status": "InProgress", "errorDetail": null}'
   ```
2. In the Admin Portal, observe the session transitions to `Imaging` and the
   `DownloadStarted` step shows as `InProgress`.
3. Send subsequent step reports: `DownloadCompleted`, `ApplyStarted`,
   `ApplyCompleted`, `Finalizing`, `Complete` -- each with `status: "Completed"`.
4. Confirm each step updates in the portal dashboard in near-real time (within
   ~2 seconds of the POST).
5. After `Complete`, confirm session status transitions to `Completed`.

**Expected outcome**: All 6 steps shown as completed in the portal; session
is `Completed` in Table Storage.

---

## Validation Scenario 6: Bulk Device Assignment

**Validates**: FR-035, US4

**Setup**: Three `Waiting` sessions. One OS image in the catalog.

**Steps**:

1. In the Admin Portal, select all three sessions in the Sessions dashboard.
2. Open the bulk assign panel. Enter a passcode for each device (or use the
   multi-select bulk-couple workflow if implemented).
3. Assign the same OS image to all three.
4. Confirm all three sessions transition to `Coupled` within 5 seconds.
5. Refresh the dashboard and confirm all three show `Coupled` with the correct
   image name.

**Expected outcome**: All three sessions coupled in a single operation; no
partial failures under normal conditions.

---

## Validation Scenario 7: OS Image Upload and CRUD

**Validates**: FR-036, FR-037, US5

**Steps**:

1. In the Admin Portal Image Management view, upload a test file (any `.wim`
   or `.esd` file, even a stub for local testing).
2. Confirm the image appears in the catalog within 30 seconds.
3. Edit the image name and description. Confirm changes are reflected immediately.
4. Attempt to delete the image. Confirm the operation succeeds (no active sessions
   using it).
5. Confirm the image is absent from the catalog and the blob is deleted from
   Blob Storage.

**Expected outcome**: Full CRUD cycle completes; blob is cleaned up on delete.

---

## Validation Scenario 8: Branding Configuration

**Validates**: FR-038, FR-039, US6

**Steps**:

1. In the Admin Portal Settings view, upload a test logo (PNG, < 2 MB).
2. Set a custom application name, primary color (`210 100% 50%`), accent color.
3. Save the branding configuration.
4. Open the Admin Portal in a new browser tab (force a fresh load).
5. Confirm:
   - The browser tab title shows the new application name.
   - The portal header shows the custom logo.
   - The primary color in the UI reflects the configured HSL value.

**Expected outcome**: Branding changes visible on next page load, no redeployment required.

---

## Validation Scenario 9: WPF Non-Blocking UI Validation

**Validates**: Constitution Principle IV, FR-003, FR-005

**Steps**:

1. Launch the WPF client in a WinPE VM or a standard Windows VM.
2. While the client is polling for session status (every 3 seconds), rapidly
   click through all visible UI elements.
3. While a simulated download is in progress (mock a slow download by throttling
   the network), continue interacting with the UI.
4. Observe no UI freeze, no "not responding" state, and no white/grey window flash.

**Automated gate**: Run the UI Responsiveness Gate in CI -- dispatcher block
time must remain < 100 ms in instrumentation traces for the download and apply
phases.

**Expected outcome**: UI remains interactive at all times.

---

## CI Execution Order

```
1. dotnet build -c Release                          (Build Gate)
2. dotnet test -c Release --collect:"XPlat Code Coverage" (Unit Test Gate)
3. Deploy to Azure staging environment
4. Run integration test suite (dotnet test integration/)  (Integration Gate)
5. npm run build (admin-portal/client + server)     (Build Gate -- Node.js)
6. npx vitest run (admin-portal/client + server)    (Unit Test Gate -- Node.js)
7. npx playwright test                              (E2E -- Scenarios 2-8 above)
8. dotnet format --verify-no-changes
   npm run lint (ESLint + Prettier)                 (Lint Gate)
9. Performance baseline comparison                  (Performance Gate)
10. UI dispatcher trace analysis                    (UI Responsiveness Gate)
```

All 10 steps must pass before any PR is eligible for merge.

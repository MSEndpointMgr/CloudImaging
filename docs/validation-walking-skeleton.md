# Walking Skeleton Validation — SC-017

**Spec reference**: SC-017, T026a  
**Milestone**: SC-017 gates the start of full-feature iteration on all components

---

## Purpose

Validate the minimum end-to-end path across all six deployed components:

1. **Cloud Imaging Client** registers a session through **Device Gateway API**
2. **Imaging Core API** creates the session and runs pre-flight authorization
3. An operator **couples and assigns** via **Cloud Imaging Portal**
4. **Operator API** and **Imaging Core API** process the assignment
5. **Cloud Imaging Client** receives the assignment and displays a terminal `ResultsView`

---

## Prerequisites

All six Azure-hosted components deployed to the shared dev environment (`mse-az-cloud-imaging-dev`):

| Component | URL / Resource |
|---|---|
| Device Gateway API | `mse-dev-func-gateway.azurewebsites.net` |
| Operator API | `mse-dev-func-operator.azurewebsites.net` |
| Imaging Core API | Private Link (via Device Gateway and Operator API) |
| Portal Backend | `mse-dev-app-portal.azurewebsites.net` |
| Portal Frontend | `mse-dev-swa-portal.azurestaticapps.net` |

**Required also**:
- At least one OS image registered in the catalog
- At least one boot image published
- Valid boot media certificate generated and active
- Test device with WinPE USB prepared from latest boot image

---

## Validation Steps

### Step 1 — Device Registration

**On test device (WinPE USB)**:
1. Boot from prepared USB drive
2. Cloud Imaging Client should auto-launch (SC-014)
3. Select **Imaging** operation
4. Client calls POST /api/v1/sessions on Device Gateway API
5. Verify: passcode and session ID displayed within 10 seconds

**Expected**: ✅ Passcode displayed, session state = `SessionAllowed`

---

### Step 2 — Portal Authentication

**On operator workstation**:
1. Navigate to the portal URL
2. Sign in with Entra ID (CloudImaging.Technician or Administrator role)
3. Navigate to **Sessions** tab
4. Verify the new session appears in the Active filter

**Expected**: ✅ Session visible with device serial number

---

### Step 3 — Coupling

1. Click **Couple Device**
2. Enter the passcode from the device screen
3. Click **Couple Device** in the modal

**Expected**: ✅ Session transitions to `SessionAssigned`; modal closes

---

### Step 4 — Image Assignment

1. Find the coupled session row (shows **Assign Image** button)
2. Click **Assign Image**
3. Select the OS image from the list
4. Click **Assign Image** in the dialog

**Expected**: ✅ Session transitions to `SessionStarted`; SAS URL issued

---

### Step 5 — Client Assignment Detection

**On test device**:
1. Client is polling every 5 seconds (session in `SessionStarted`)
2. Within one polling cycle (≤ 30 s), client detects assignment
3. Client navigates to ProgressView
4. ProgressView shows step indicator and starts download

**Expected**: ✅ Download begins; step indicator updates

---

### Step 6 — Terminal State

For a quick smoke test (no full download):
- Manually transition session to `SessionCompleted` via Table Storage, or
- Let the client timeout into `SessionFailed` (30-minute inactivity)

**Expected**: ✅ Client displays ResultsView with appropriate terminal outcome

---

## Validation Results

| Step | Result | Date | Notes |
|---|---|---|---|
| 1 — Device Registration | ⏳ Pending | — | Requires physical hardware + deployed environment |
| 2 — Portal Authentication | ⏳ Pending | — | |
| 3 — Coupling | ⏳ Pending | — | |
| 4 — Image Assignment | ⏳ Pending | — | |
| 5 — Client Assignment Detection | ⏳ Pending | — | |
| 6 — Terminal State | ⏳ Pending | — | |
| **Overall SC-017 Gate** | ⏳ Pending | — | Must be PASS before full-feature iteration begins |

---

## Automated Proxy

While the full end-to-end validation requires physical hardware, the automated test suite validates each component contract individually:

- **T027** — CreateSession contract test (DeviceGateway)
- **T035/T035a** — Couple + assign contract tests (OperatorApi)
- **T036/T036a** — Passcode consume + assign integration tests (ImagingCore)
- **T043** — Status polling response contract (DeviceGateway)
- **T044** — Assignment poller transition logic (Client)

**Current automated proxy status**: ✅ All 280 tests passing

---

## SC-017 Gate Decision

SC-017 is considered **PASSED** when:
- [ ] All 6 manual validation steps complete with ✅ result
- [ ] End-to-end timing ≤ 45 minutes (download/apply) + ≤ 2 minutes (session/coupling API calls)
- [ ] No manual Azure Portal intervention required during the test

*Update this document with results when hardware validation is completed.*

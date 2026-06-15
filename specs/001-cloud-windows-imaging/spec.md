# Feature Specification: Cloud Windows Imaging

**Feature Branch**: `001-cloud-windows-imaging`

**Created**: 2026-06-14

**Status**: Draft

**Input**: User description: "Build a solution that applies a Windows operating system image from the cloud involving a Client WPF application running in WinPE..."

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Device Boot and Session Initiation (Priority: P1)

A technician inserts WinPE boot media and powers on a target device. The WPF
client launches automatically, registers a new imaging session with the public
API, and displays a short alphanumeric passcode on screen. The device is now
waiting to be coupled with an admin portal session before imaging can proceed.

**Why this priority**: This is the entry point of the entire workflow. Nothing
else can happen until a device session exists and the client is running.

**Independent Test**: Can be fully tested by booting the WPF client in a WinPE
environment, observing that a passcode appears and a session is registered in
the backend, without any Admin Portal involvement.

**Acceptance Scenarios**:

1. **Given** a device booted into WinPE with network connectivity, **When** the WPF client starts, **Then** it registers a new session and displays a unique session passcode within 10 seconds.
2. **Given** the WPF client has registered a session, **When** the passcode is shown, **Then** the client enters a waiting state and shows a clear status message that it is awaiting coupling.
3. **Given** the WinPE environment has no network connectivity at startup, **When** the WPF client attempts to register, **Then** it displays a user-friendly error with a retry option, and no frozen UI state occurs.

---

### User Story 2 - Technician Couples Device and Assigns Image (Priority: P2)

A technician opens the Admin Portal, views a list of uncoupled device sessions,
enters the passcode shown on the target device's WPF screen, selects a Windows
OS image from the catalog, and initiates imaging. The device WPF client is
notified and begins downloading and applying the image.

**Why this priority**: This is the core orchestration step. It connects the
physical device to the cloud workflow and triggers the actual imaging.

**Independent Test**: Can be fully tested with a single device session already
registered (US1 complete), by entering a passcode in the portal, assigning an
image, and confirming the WPF client transitions from the waiting state to
imaging.

**Acceptance Scenarios**:

1. **Given** an uncoupled device session exists, **When** a technician enters the correct passcode in the portal, **Then** the session is coupled and the device appears in the active sessions list.
2. **Given** a coupled session, **When** the technician assigns a Windows OS image, **Then** the WPF client receives a download URL and time-limited access credential without further manual action.
3. **Given** an incorrect passcode is entered, **When** the technician submits, **Then** an error is shown, no session is coupled, and the attempt is logged.
4. **Given** a device session has expired, **When** the technician attempts to couple it, **Then** the portal clearly indicates the session is no longer valid.

---

### User Story 3 - WPF Client Downloads and Applies Windows Image (Priority: P3)

Once coupled and assigned, the WPF client downloads the OS image from the
Storage Account using a time-limited access credential and applies it to the
device disk. The client reports step-by-step progress back to the backend in
real time. The entire UI remains fully responsive throughout.

**Why this priority**: This is the primary deliverable of the product - the
actual imaging of the device.

**Independent Test**: Can be fully tested by simulating a coupled session with
an assigned image and verifying the download, apply, and progress-reporting
pipeline end-to-end.

**Acceptance Scenarios**:

1. **Given** the WPF client has received a valid download URL and access credential, **When** imaging begins, **Then** the download executes in a background thread with real-time progress displayed in the UI.
2. **Given** imaging is in progress, **When** each imaging step completes, **Then** the client reports that step's status to the API and the Admin Portal updates accordingly.
3. **Given** a download fails partway through, **When** the failure occurs, **Then** the WPF client displays an error with a support reference code and offers a retry option; the UI does not freeze.
4. **Given** imaging completes successfully, **When** the final step is confirmed, **Then** the WPF client shows a success screen and the session is marked complete in the portal.

---

### User Story 4 - Admin Portal Bulk Imaging Operations (Priority: P4)

A technician manages multiple devices being imaged simultaneously from the
Admin Portal. They can select multiple uncoupled sessions, batch-couple them,
assign the same image to all, and monitor per-device progress in a unified
dashboard view.

**Why this priority**: High-value for production deployments where many devices
are imaged at the same time; delivers operational efficiency beyond single-device
workflows.

**Independent Test**: Can be tested independently of US1-3 logic by simulating
multiple concurrent registered sessions in the portal and validating that bulk
assignment and progress monitoring work without single-session flows being
disrupted.

**Acceptance Scenarios**:

1. **Given** multiple uncoupled sessions exist, **When** the technician selects several and assigns a single image in bulk, **Then** all selected devices transition to imaging status simultaneously.
2. **Given** bulk imaging is active, **When** the technician views the session dashboard, **Then** per-device progress is visible and updates in real time for all active sessions.
3. **Given** one device in a bulk operation fails, **When** the failure occurs, **Then** the other devices continue unaffected and the failed device is flagged clearly in the dashboard.

---

### User Story 5 - Admin Portal OS Image Management (Priority: P5)

An administrator can upload new Windows OS images to the Storage Account from
the Admin Portal, view all existing images, edit their metadata, and delete
obsolete ones. Images are available in the assignment catalog immediately after
upload.

**Why this priority**: Necessary for keeping the image catalog current, but
does not block any imaging workflow once at least one image exists.

**Independent Test**: Can be fully tested by uploading a test image, verifying
it appears in the catalog, editing its name/metadata, and deleting it without
affecting any other system component.

**Acceptance Scenarios**:

1. **Given** an administrator uploads a new OS image, **When** the upload completes, **Then** the image appears in the assignment catalog within 30 seconds.
2. **Given** an existing OS image in the catalog, **When** an administrator edits its metadata, **Then** the updated details are reflected immediately.
3. **Given** an OS image is in use by an active imaging session, **When** an administrator attempts to delete it, **Then** the deletion is blocked and a clear reason is shown.
4. **Given** an administrator deletes an image not in use, **When** the deletion is confirmed, **Then** the image is removed from the catalog and from Storage.

---

### User Story 6 - Admin Portal Branding Configuration (Priority: P6)

An administrator configures the Admin Portal's visual branding: uploading a
logo, setting primary and accent colors, and setting the application display
name. Changes take effect without redeployment and are visible to all portal
users on next page load.

**Why this priority**: Important for the community-distribution model of this
solution, but does not affect core imaging functionality.

**Independent Test**: Can be fully tested by updating branding settings and
confirming the visual changes appear across the portal without any code change
or redeployment.

**Acceptance Scenarios**:

1. **Given** an administrator uploads a logo and changes the color scheme, **When** they save settings, **Then** the portal reflects the new branding without redeployment.
2. **Given** branding configuration exists, **When** any user accesses the portal, **Then** they see the custom branding rather than default placeholder assets.

---

### Edge Cases

- What happens when a WPF client loses network connectivity mid-download? (Expected: resume or retry with fresh credentials if the access token has expired)
- What happens when a SAS token expires before the download completes? (Expected: client requests a token refresh via the public API)
- What happens when two technicians attempt to couple the same passcode simultaneously? (Expected: first claim succeeds, second receives a conflict error)
- What happens when the private API (SessionHandler) is unreachable from the public API (SessionBroker)? (Expected: SessionBroker returns a service unavailable response; client displays a retry-able error)
- What happens when an OS image upload is interrupted? (Expected: the portal detects an incomplete upload and does not add the image to the catalog)

---

## Requirements *(mandatory)*

### Functional Requirements

#### WPF Client

- **FR-001**: The WPF client MUST register a new device session with the public API on startup and receive a session passcode.
- **FR-002**: The WPF client MUST display the session passcode prominently so the technician can read it easily from the device screen.
- **FR-003**: The WPF client MUST poll the public API for imaging assignment without freezing or degrading the UI.
- **FR-004**: The WPF client MUST receive the OS image download URL and a time-limited access credential upon assignment.
- **FR-005**: The WPF client MUST download the OS image entirely off the UI thread and display real-time download progress.
- **FR-006**: The WPF client MUST apply the downloaded image to the target disk.
- **FR-007**: The WPF client MUST report each imaging step's status back to the public API in real time.
- **FR-008**: The WPF client MUST request a credential refresh from the public API if the access credential approaches expiry before the download completes.
- **FR-009**: The WPF client MUST display a clear error message with a support reference code and a retry option on any failure, without any UI freeze.

#### Public API (SessionBroker)

- **FR-010**: The public API MUST accept session registration requests from WPF clients and return a unique session passcode.
- **FR-011**: The public API MUST operate with the minimum set of Azure permissions required - it MUST NOT have direct read or write access to the Storage Account.
- **FR-012**: The public API MUST forward authenticated session and progress requests to the private API over a Private Link connection.
- **FR-013**: The public API MUST return OS image download URLs and time-limited access credentials to the WPF client, sourced from the private API.
- **FR-014**: The public API MUST accept progress reports from the WPF client and relay them to the private API for persistence.
- **FR-015**: The public API MUST issue refreshed access credentials to the WPF client when requested, provided the session is still valid.

#### Private API (SessionHandler)

- **FR-020**: The private API MUST be accessible only via Private Link; it MUST have no publicly reachable endpoint.
- **FR-021**: The private API MUST manage the full lifecycle of a device session: created, waiting, coupled, imaging, completed, failed.
- **FR-022**: The private API MUST generate time-limited SAS tokens for OS image downloads, with a configurable expiry window (default: 4 hours).
- **FR-023**: The private API MUST expose an endpoint for the Admin Portal to couple a session to a passcode and assign an OS image.
- **FR-024**: The private API MUST store and expose per-device imaging progress for consumption by the Admin Portal.
- **FR-025**: The private API MUST have the permissions necessary to read OS images from the Storage Account and generate SAS tokens.

#### Admin Portal

- **FR-030**: The Admin Portal MUST require authentication before any operation is accessible.
- **FR-031**: The Admin Portal MUST display all active and recent device sessions with their current state and real-time imaging progress.
- **FR-032**: The Admin Portal MUST allow a technician to enter a device passcode to couple an uncoupled session.
- **FR-033**: The Admin Portal MUST allow assignment of an OS image to a coupled session to initiate imaging.
- **FR-034**: The Admin Portal MUST display per-step imaging progress for each device in real time.
- **FR-035**: The Admin Portal MUST support bulk operations: selecting multiple sessions and assigning an OS image to all simultaneously.
- **FR-036**: The Admin Portal MUST allow upload of new OS images to the Storage Account with metadata (name, version, description).
- **FR-037**: The Admin Portal MUST support full CRUD operations on OS images: list, view, edit metadata, delete (with guard against deleting active images).
- **FR-038**: The Admin Portal MUST support configurable branding: custom logo, primary color, accent color, and application display name.
- **FR-039**: Branding changes MUST take effect without redeployment or application restart.
- **FR-040**: The Admin Portal MUST be built using a modern component framework and adhere to WCAG 2.1 AA accessibility standards.

### Key Entities

- **DeviceSession**: Represents a single imaging session for one device. Attributes: session ID, passcode, state (waiting/coupled/imaging/completed/failed), assigned OS image reference, progress steps, timestamps.
- **OSImage**: A Windows OS image stored in the Storage Account. Attributes: image ID, name, version, description, size, storage path, upload date, in-use flag.
- **ImagingStep**: An individual step within an imaging job. Attributes: step name, status, started-at, completed-at, error detail.
- **SASCredential**: A time-limited access credential for an OS image download. Attributes: URL, expiry time, associated session.
- **BrandingConfiguration**: Portal branding settings. Attributes: logo URL, primary color, accent color, application name.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A technician can boot a device, couple it via passcode, and have OS imaging fully complete within 15 minutes for a standard-tier deployment.
- **SC-002**: The WPF client UI remains fully responsive (no perceptible freeze) throughout the entire imaging workflow, including during download and apply phases.
- **SC-003**: The Admin Portal supports at least 50 simultaneous device imaging sessions with real-time progress updates without degradation.
- **SC-004**: OS image access credentials expire within a configurable window with a default of 4 hours; expired credentials are not accepted by the Storage Account.
- **SC-005**: Bulk assignment of an OS image to 20 or more devices completes initiation within 5 seconds of the technician confirming the operation.
- **SC-006**: Admin Portal branding changes are reflected to all users within one page reload, with no redeployment required.
- **SC-007**: The private API has no publicly reachable network endpoint; all reachability is exclusively through the designated private channel from the public API.
- **SC-008**: All CRUD operations on OS images (catalog of up to 500 items) complete within 3 seconds.

---

## Assumptions

- Devices are running a WinPE environment with outbound network connectivity (wired or Wi-Fi) to reach the public API endpoint.
- Azure infrastructure (Storage Account, both Function Apps, App Service, Private Link, Entra ID) is pre-provisioned prior to first use.
- Admin Portal authentication is handled via Azure Entra ID (enterprise identity); device-level WPF client authentication relies solely on the session passcode mechanism.
- The WPF client operates in a minimal-trust, ephemeral environment: no pre-shared secrets, certificates, or credentials are embedded in the WinPE image.
- One active imaging session per device at a time; concurrent sessions on the same device are not supported in v1.
- OS images stored in the Storage Account are in a format compatible with the Windows imaging toolchain (e.g., WIM or ESD); format validation is out of scope for v1.
- Mobile-optimized Admin Portal layout is out of scope for v1 (desktop browser is the primary target).
- SAS token expiry defaults to 4 hours and is configurable per deployment by an administrator.
- The Admin Portal is a community-distributed solution; all configurable defaults MUST be documented clearly for self-hosters.

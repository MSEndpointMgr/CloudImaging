# Feature Specification: Cloud Windows Imaging

**Feature Branch**: `001-cloud-windows-imaging`

**Created**: 2026-06-14

**Status**: Draft

**Input**: User description: "Build a solution that applies a Windows operating system image from the cloud involving a Client WPF application running in WinPE..."

---

## Clarifications *(notes from specification refinement)*

### Session YYYY-MM-DD (June 15, 2026)

- **Q: SAS Token Refresh Trigger (FR-008)** → **A: Cloud Imaging Client MUST poll the Device Gateway API every 30 seconds for state updates, which include a SAS token for Storage Account access. When the SAS token expiry time is less than 15 minutes away, the client MUST request a fresh token from the Device Gateway API before continuing the download. This provides a 15-minute safety buffer for token refresh and network resilience.**
- **Q: Session State Lifecycle** → **A: SessionInit: Client starts and initiates a session after technician selects "Imaging" on launch. SessionAllowed: Pre-flight checks pass (for example, Autopilot device hash verification). SessionAssigned: Session is coupled from Cloud Imaging Portal and client is authorized to request SAS token and Storage Account download details. SessionStarted: Automatically transitions from SessionAssigned on the next 30-second polling interval when the client detects Assigned and requests details; no additional local technician action is required on the device. SessionInProgress: Client relays per-step imaging status (format disk, retrieve image metadata, download image, apply image with DISM, and related steps) to the Imaging Core API for real-time portal visibility. SessionCompleted/SessionFailed: Terminal outcomes for successful completion or unrecoverable failure.**
- **Q: Device Session Passcode Format** → **A: 6-character alphanumeric passcode format: uppercase letters (A–Z) and digits (0–9) only, with ambiguous characters excluded (I, O, 1, 0). This ensures high readability on WinPE screens, minimizes typos during manual portal entry, and provides ~1 billion unique codes (32^6 ≈ 1B). Passcode MUST be case-insensitive for portal input validation.**
- **Q: Passcode Security Model** → **A: The session passcode is a short-lived, one-time pairing code generated server-side (Imaging Core API via Device Gateway API), returned to the client only for local display, and never re-used as an ongoing API credential. Ongoing client polling and progress/report operations use a separate high-entropy device-session token issued at SessionInit. The passcode is invalidated immediately after successful coupling or expiry and is stored only as a hash at rest.**
- **Q: Device-Session Token Properties** → **A: The device-session token is a server-generated, opaque bearer credential issued at SessionInit by the Device Gateway API on behalf of the Imaging Core API. It is distinct from both the one-time pairing passcode and any Entra ID access token, is never shown to technicians, is bound to the specific device session, and is used only for ongoing client polling, progress reporting, and SAS refresh requests until the session ends or the token expires.**
- **Q: Overall Imaging Completion Percentage Calculation (FR-007, FR-024)** → **A: Overall imaging completion percentage is computed using uniform step-milestone weighting: (completed steps / total steps for the session) x 100, truncated to integer (0-100). The set of steps is fixed at the point the session transitions to SessionStarted and includes all defined imaging steps (format-disk, retrieve-metadata, download-image, apply-image, and any additional session-specific steps). The hyphenated forms (format-disk, retrieve-metadata, download-image, apply-image) are the machine-readable step identifiers used in API payloads and DTO values; the natural-language forms in FR-007 are for human readability only. The download step is a single step unit; it may expose sub-progress via the optional stepProgressPercent field on its ImagingStep record but contributes fractional value to overallProgressPercent only upon completion of that step. This rule ensures a deterministic, reproducible percentage across all implementations and API layers.**

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Device Boot and Session Initiation (Priority: P1)

A technician inserts WinPE boot media and powers on a target device. The Cloud
Imaging Client launches automatically, registers a new imaging session with the
Device Gateway API, and displays a short alphanumeric passcode on screen. The
device is now waiting to be coupled with a Cloud Imaging Portal session before
imaging can proceed.

**Why this priority**: This is the entry point of the entire workflow. Nothing
else can happen until a device session exists and the client is running.

**Independent Test**: Can be fully tested by booting the Cloud Imaging Client in a WinPE
environment, observing that a passcode appears and a session is registered in
the backend, without any Cloud Imaging Portal involvement.

**Acceptance Scenarios**:

1. **Given** a device booted into WinPE with network connectivity, **When** the Cloud Imaging Client starts, **Then** it registers a new session and displays a unique session passcode within 10 seconds.
2. **Given** the Cloud Imaging Client has registered a session, **When** the passcode is shown, **Then** the client enters a waiting state and shows a clear status message that it is awaiting coupling.
3. **Given** the WinPE environment has no network connectivity at startup, **When** the Cloud Imaging Client attempts to register, **Then** it displays a user-friendly error with a retry option, and no frozen UI state occurs.

---

### User Story 2 - Technician Couples Device and Assigns Image (Priority: P2)

A technician opens the Cloud Imaging Portal, views a list of uncoupled device sessions,
enters the passcode shown on the target device's WPF screen, selects a Windows
OS image from the catalog, and initiates imaging. The device Cloud Imaging Client is
notified and begins downloading and applying the image.

**Why this priority**: This is the core orchestration step. It connects the
physical device to the cloud workflow and triggers the actual imaging.

**Independent Test**: Can be fully tested with a single device session already
registered (US1 complete), by entering a passcode in the portal, assigning an
image, and confirming the Cloud Imaging Client transitions from the waiting state to
imaging.

**Acceptance Scenarios**:

1. **Given** an uncoupled device session exists, **When** a technician enters the correct passcode in the portal, **Then** the session is coupled and the device appears in the active sessions list.
2. **Given** a coupled session, **When** the technician assigns a Windows OS image, **Then** the Cloud Imaging Client receives a download URL and time-limited SAS token without further manual action.
3. **Given** an incorrect passcode is entered, **When** the technician submits, **Then** an error is shown, no session is coupled, and the attempt is logged.
4. **Given** a device session has expired, **When** the technician attempts to couple it, **Then** the portal clearly indicates the session is no longer valid.

---

### User Story 3 - Cloud Imaging Client Downloads and Applies Windows Image (Priority: P3)

Once coupled and assigned, the Cloud Imaging Client downloads the OS image from the
Storage Account using a time-limited SAS token and applies it to the
device disk. The client reports step-by-step progress back to the backend in
real time. The entire UI remains fully responsive throughout.

**Why this priority**: This is the primary deliverable of the product - the
actual imaging of the device.

**Independent Test**: Can be fully tested by simulating a coupled session with
an assigned image and verifying the download, apply, and progress-reporting
pipeline end-to-end.

**Acceptance Scenarios**:

1. **Given** the Cloud Imaging Client has received a valid download URL and SAS token, **When** imaging begins, **Then** the download executes in a background thread with real-time progress displayed in the UI.
2. **Given** imaging is in progress, **When** each imaging step completes, **Then** the client reports that step's status to the API and the Cloud Imaging Portal updates accordingly.
3. **Given** a download fails partway through, **When** the failure occurs, **Then** the Cloud Imaging Client displays an error with a support reference code and offers a retry option; the UI does not freeze.
4. **Given** imaging completes successfully, **When** the final step is confirmed, **Then** the Cloud Imaging Client shows a success screen and the session is marked complete in the portal.

---

### User Story 4 - Cloud Imaging Portal Bulk Imaging Operations (Priority: P4)

A technician manages multiple devices being imaged simultaneously from the
Cloud Imaging Portal. They can select multiple uncoupled sessions, batch-couple them,
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

### User Story 5 - Cloud Imaging Portal OS Image Management (Priority: P5)

An administrator can upload new Windows OS images to the Storage Account from
the Cloud Imaging Portal, view all existing images, edit their metadata, and delete
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

### User Story 6 - Cloud Imaging Portal Branding Configuration (Priority: P6)

An administrator configures the Cloud Imaging Portal's visual branding: uploading a
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

### User Story 7 - Cloud Imaging Media Builder for WinPE Boot (Priority: P3)

A technician runs a dedicated WPF Cloud Imaging Media Builder application on their own
Windows workstation to produce bootable USB media for imaging operations. The app
supports two independent workflows:

**Function 1: Generate Boot Image**
The technician uses the app to bundle WinPE, all required configurations and
addons, and the Cloud Imaging Client into a relocatable boot image artifact.
This image is then uploaded to the Cloud Imaging Portal's boot image catalog for reuse
across multiple USB preparations.

**Function 2: Prepare USB Storage Device**
The technician uses the app to download the latest boot image from the backend
API (after signing in with Entra ID credentials), provision a removable USB
device with exactly two partitions (cache + bootable), and deploy the boot image
to the bootable partition. WinPE is configured to auto-start the Cloud Imaging
Client on boot.

**Why this priority**: Standardized boot image generation and reproducible USB
media preparation are prerequisites for field imaging operations. The two-workflow
model separates media authoring (done rarely) from media preparation (done
frequently).

**Independent Test**: Generate Boot Image workflow can be tested by building a
boot image locally and inspecting its contents. Prepare USB Storage Device can
be tested on a technician workstation by preparing a blank USB device, validating
partition layout and boot image payload, and booting a test machine to confirm
WinPE auto-starts the Cloud Imaging Client app.

**Acceptance Scenarios**:

**Generate Boot Image**:
1. **Given** a technician opens the Cloud Imaging Media Builder app, **When** they select the "Generate Boot Image" function, **Then** they are guided through selecting WinPE source, client payload, and output location with clear progress indicators.
2. **Given** boot image generation completes, **When** the technician inspects the output, **Then** it contains a signed manifest with image version, build timestamp, WinPE contents, and client app payload.
3. **Given** boot image generation fails, **When** the error occurs, **Then** the app displays a support reference code and offers cleanup/retry options.

**Prepare USB Storage Device**:
1. **Given** a technician opens the Cloud Imaging Media Builder app and has not yet signed in, **When** they select the "Prepare USB Storage Device" function, **Then** they are prompted to sign in with their Entra ID credentials to access the boot image catalog.
2. **Given** the technician has signed in, **When** they query available boot images, **Then** the app downloads the latest boot image metadata from the backend via the Operator API.
3. **Given** a technician selects a removable USB device and initiates preparation, **When** they confirm the destructive action, **Then** the app creates exactly two partitions: one cache partition and one bootable WinPE partition.
4. **Given** USB preparation begins, **When** the app downloads the boot image via SAS URL, **Then** the download executes off the UI thread with real-time progress displayed.
5. **Given** USB preparation completes, **When** the technician inspects the boot partition, **Then** it contains the boot image payload and WinPE is configured to auto-start the Cloud Imaging Client app.
6. **Given** a machine boots from the prepared USB, **When** WinPE starts, **Then** the Cloud Imaging Client app launches automatically without manual intervention.
7. **Given** the technician accidentally selects a non-removable or system disk, **When** they attempt to continue, **Then** the app blocks execution and requires explicit re-selection of a valid removable USB device.

---

### Edge Cases

- What happens when a Cloud Imaging Client loses network connectivity mid-download? (Expected: resume or retry with a refreshed SAS token if the current token has expired, or use cached image if available and hash matches)
- What happens when a SAS token expires before the download completes? (Expected: client requests a token refresh via the Device Gateway API, or uses cached image if available)
- What happens when a cached OS image's hash does not match the API-provided hash? (Expected: cached image is deleted, client downloads fresh copy from API)
- What happens when the USB cache partition is full and a new image is being cached? (Expected: client evicts oldest/least-recently-used cache entries to make space)
- What happens when two technicians attempt to couple the same passcode simultaneously? (Expected: first claim succeeds, second receives a conflict error)
- What happens when the Imaging Core API is unreachable from the Device Gateway API? (Expected: Device Gateway API returns a service unavailable response; client displays a retry-able error)
- What happens when the Imaging Core API is unreachable from the Operator API? (Expected: Operator API returns a service unavailable response; Cloud Imaging Portal and Cloud Imaging Media Builder show retry-able errors)
- What happens when an OS image upload is interrupted? (Expected: the portal detects an incomplete upload and does not add the image to the catalog)
- What happens when the USB media preparation app is asked to use a disk that contains the current Windows system volume? (Expected: operation is blocked with a hard safety error)
- What happens when partitioning fails after the cache partition is created but before boot files are applied? (Expected: app reports failure, emits support reference code, and offers cleanup/retry guidance)
- What happens when WinPE boot files are copied but auto-start registration for the client app fails? (Expected: app marks media invalid for release and provides remediation steps)

---

## Requirements *(mandatory)*

### Functional Requirements

#### Cloud Imaging Client

- **FR-001**: The Cloud Imaging Client MUST register a new device session with the Device Gateway API on startup and receive both: (1) a unique 6-character alphanumeric one-time pairing passcode (uppercase letters and digits only, excluding I, O, 1, 0), and (2) a high-entropy, opaque device-session token bound to that session for subsequent authenticated polling and status operations.
- **FR-002**: The Cloud Imaging Client MUST display the session passcode prominently in a clear, large font so the technician can read it easily from the WinPE device screen and enter it into the Cloud Imaging Portal without error.
- **FR-003**: The Cloud Imaging Client MUST poll the Device Gateway API every 30 seconds for state changes using the device-session token (not the pairing passcode or an Entra ID token) without freezing or degrading the UI and, when the state is SessionAssigned, MUST automatically request imaging details and continue the workflow without additional local technician action.
- **FR-004**: The Cloud Imaging Client MUST receive the OS image download URL and a time-limited SAS token for Storage Account blob download access upon assignment; the SAS token is issued by the Imaging Core API and included in polling responses from the Device Gateway API. The SAS token MUST NOT be used as an API authentication credential.
- **FR-005**: The Cloud Imaging Client MUST download the OS image entirely off the UI thread and display real-time download progress.
- **FR-006**: The Cloud Imaging Client MUST apply the downloaded image to the target disk.
- **FR-007**: The Cloud Imaging Client MUST report each imaging step's status back to the Device Gateway API in real time, and MUST provide an overall imaging progress value so the Portal can display both step-level status and a session-level completion percentage.
- **FR-008**: The Cloud Imaging Client MUST request a SAS token refresh from the Device Gateway API if the current token's expiry time is less than 15 minutes away and the download is still in progress; the Device Gateway API will source the refreshed token from the Imaging Core API.
- **FR-009**: The Cloud Imaging Client MUST display a clear error message with a support reference code and a retry option on any failure, without any UI freeze.
- **FR-009a**: The Cloud Imaging Client MUST compute and store the SHA256 hash of each downloaded OS image and persist it alongside the image on the USB cache partition with metadata (image ID, version, hash, timestamp, size).
- **FR-009b**: The Cloud Imaging Client MUST, before downloading an assigned OS image, check the USB cache partition for a cached image matching the assignment's image ID and version. If a matching cached image exists, the client MUST validate the cached image's SHA256 hash against the value returned by the Device Gateway API. If the hashes match, the client MUST skip the download step and proceed with the remaining imaging workflow steps (format, apply, etc.) using the cached image.
- **FR-009c**: The Cloud Imaging Client MUST implement cache invalidation: if the cached image's hash does not match the API-provided hash, or if the cached image is corrupted/unreadable, the client MUST delete the invalid cache entry, request a fresh download from the Device Gateway API, and retry with the newly downloaded image.
- **FR-009d**: The Cloud Imaging Client MUST implement cache cleanup: cache entries older than 30 days MUST be automatically removed on next boot to prevent cache partition exhaustion. Catalog-ID-based orphan detection (removing entries for image IDs no longer in the active catalog) is deferred to v2, as it requires a Device Gateway API catalog-query endpoint not in scope for v1.

#### Device Gateway API

- **FR-010**: The Device Gateway API MUST accept session registration requests from Cloud Imaging Client instances and return a server-generated unique 6-character alphanumeric one-time pairing passcode (uppercase letters and digits only, excluding I, O, 1, 0) and a high-entropy, opaque device-session token; the passcode MUST be stored only as a hash at rest and the token MUST be associated with the specific session record.
- **FR-011**: The Device Gateway API MUST be publicly reachable and MUST accept unauthenticated inbound requests from WinPE Cloud Imaging Client instances.
- **FR-012**: The Device Gateway API MUST operate with the minimum set of Azure permissions required - it MUST NOT have direct read or write access to the Storage Account.
- **FR-013**: The Device Gateway API MUST forward authenticated session and progress requests to the Imaging Core API over a Private Link connection.
- **FR-014**: The Device Gateway API MUST return OS image download URLs and SAS tokens to the Cloud Imaging Client, sourced from the Imaging Core API, on polling responses authenticated by device-session token; the pairing passcode MUST NOT be returned after SessionInit and MUST NOT be required for ongoing client polling, and the device-session token MUST be the only credential accepted for those device-originated authenticated calls.
- **FR-015**: The Device Gateway API MUST accept progress reports from the Cloud Imaging Client and relay them to the Imaging Core API for persistence.
- **FR-016**: The Device Gateway API MUST issue refreshed SAS tokens to the Cloud Imaging Client when requested (e.g., when remaining token lifetime < 15 minutes), provided the session is still valid and within the allowed assignment window.

#### Imaging Core API

- **FR-020**: The Imaging Core API MUST be accessible only via Private Link; it MUST have no publicly reachable endpoint.
- **FR-021**: The Imaging Core API MUST manage the lifecycle of a device session with explicit states and transition rules: SessionInit -> SessionAllowed -> SessionAssigned -> SessionStarted -> SessionInProgress -> SessionCompleted/SessionFailed. Transition from SessionAssigned to SessionStarted MUST occur automatically on the client's next polling interval after assignment details are requested. Pre-imaging states (SessionInit, SessionAllowed, SessionAssigned) MUST expire after 30 minutes of inactivity. SessionStarted and SessionInProgress MUST remain active while 30-second client polling heartbeats continue, and MUST fail after more than 4 hours without heartbeat. SessionCompleted and SessionFailed MUST be auto-purged after 24 hours, requiring a new device boot/session to restart imaging. The one-time pairing passcode MUST be invalidated immediately after successful coupling or on expiry, whichever occurs first.
- **FR-022**: The Imaging Core API MUST generate time-limited SAS tokens for OS image downloads, with a configurable expiry window (default: 4 hours).
- **FR-023**: The Imaging Core API MUST expose endpoints for Device Gateway API and Operator API operations, including session coupling and image assignment.
- **FR-024**: The Imaging Core API MUST store and expose per-device imaging progress for consumption by the Operator API, including step-level state, timestamps, error detail, and an overall imaging completion percentage for each active session.
- **FR-025**: The Imaging Core API MUST have the permissions necessary to read OS images and boot images from the Storage Account and generate SAS tokens.

#### Cloud Imaging Portal

- **FR-030**: The Cloud Imaging Portal MUST require Entra ID authentication before any operation is accessible, and unauthenticated requests MUST be denied.
- **FR-031**: The Cloud Imaging Portal MUST display all active and recent device sessions with their current state, real-time imaging progress, and overall completion percentage.
- **FR-032**: The Cloud Imaging Portal MUST allow a technician to enter a device passcode (case-insensitive input) to couple an uncoupled session; the passcode is a pairing code only and MUST NOT be treated as an API authentication credential. Invalid, expired, or already-consumed passcodes MUST be rejected with clear error messages.
- **FR-033**: The Cloud Imaging Portal MUST allow assignment of an OS image to a coupled session to initiate imaging.
- **FR-034**: The Cloud Imaging Portal MUST display per-step imaging progress for each device in real time, including the current step name, per-step status, and session-level completion percentage.
- **FR-035**: The Cloud Imaging Portal MUST support bulk operations: selecting multiple sessions and assigning an OS image to all simultaneously.
- **FR-036**: The Cloud Imaging Portal MUST allow upload of new OS images to the Storage Account with metadata (name, version, description).
- **FR-037**: The Cloud Imaging Portal MUST support full CRUD operations on OS images: list, view, edit metadata, delete (with guard against deleting active images).
- **FR-038**: The Cloud Imaging Portal MUST support configurable branding: custom logo, primary color, accent color, and application display name.
- **FR-039**: Branding changes MUST take effect without redeployment or application restart.
- **FR-040**: The Cloud Imaging Portal MUST be built using a modern component framework and adhere to WCAG 2.1 AA accessibility standards. Its backend MUST call the Operator API for all session, image, boot image, and branding operations using Entra ID-authenticated access; browser code MUST NOT call the Imaging Core API directly.

#### Deployment and Self-Hosting Packaging

- **FR-041**: The solution MUST provide a reproducible infrastructure deployment package (IaC templates + parameter files) that provisions all required Azure resources in a customer-owned tenant.
- **FR-042**: The solution MUST externalize all environment-specific settings (tenant IDs, client IDs, URLs, storage names, network ranges, branding defaults) into deployment parameters or environment variables with no hardcoded environment values in source.
- **FR-043**: The solution MUST be deployable to dev, test, and production environments using only configuration changes; source-code edits between environments are not allowed.
- **FR-044**: The solution MUST include Cloud Imaging Media Builder-generated boot image output and instructions so organizations can generate WinPE boot media that embeds the Cloud Imaging Client without modifying application source.
- **FR-045**: The solution MUST include a deployment package document that describes prerequisites, deployment order, rollback steps, and post-deployment validation for self-hosting organizations.

#### Cloud Imaging Media Builder WPF Application

- **FR-050**: The solution MUST include a dedicated WPF Cloud Imaging Media Builder application that runs on technician-managed Windows devices and provides two independent workflows: Generate Boot Image and Prepare USB Storage Device.
- **FR-051**: The Generate Boot Image workflow MUST bundle WinPE environment, configurations, addons, and the Cloud Imaging Client app into a relocatable boot image artifact with a signed manifest recording image version, timestamp, component checksums, and deployment metadata; the resulting WIM MUST be published through the portal using a staged direct-to-blob upload flow, and MUST remain unavailable in the catalog until the upload is fully committed and validated.
- **FR-052**: The Cloud Imaging Media Builder application MUST require Entra ID sign-in to unlock the "Prepare USB Storage Device" workflow, enabling the app to obtain an access token for authenticated API calls to the Operator API.
- **FR-053**: The Prepare USB Storage Device workflow MUST query the Operator API to retrieve available boot images and download the latest boot image via a time-limited SAS URL issued from the most recently published boot image artifact in Storage.
- **FR-054**: The Cloud Imaging Media Builder application MUST validate that the selected target disk is removable and not the host OS/system disk before any destructive action is allowed.
- **FR-055**: The Cloud Imaging Media Builder application MUST create exactly two partitions on the selected USB media: one cache partition and one bootable partition for the downloaded boot image.
- **FR-056**: The Cloud Imaging Media Builder application MUST download and deploy the boot image to the bootable partition entirely off the UI thread, with real-time progress displayed and ability to resume interrupted downloads.
- **FR-057**: The Cloud Imaging Media Builder application MUST configure WinPE startup so the Cloud Imaging Client application launches automatically on boot.
- **FR-058**: The Cloud Imaging Media Builder application MUST provide operator-visible progress, validation output, and clear failure diagnostics with support reference codes for all stages: boot image generation, disk validation, partitioning, download, deployment, and boot configuration.
- **FR-059**: The Cloud Imaging Media Builder application MUST persist a preparation manifest on the USB media that records preparation timestamp, app version, boot image version, partition layout, and validation status.

#### Operator API

- **FR-060**: The solution MUST include a dedicated Operator API that serves authenticated operator workloads for Cloud Imaging Portal and Cloud Imaging Media Builder.
- **FR-061**: The Operator API MUST require Entra ID authentication for all endpoints, with RBAC roles distinguishing Cloud Imaging Media Builder (read-only for boot image retrieval) and Cloud Imaging Portal backend (write/admin).
- **FR-062**: The Operator API MUST provide authenticated endpoints for Cloud Imaging Media Builder apps to query available boot images and retrieve download URLs with time-limited SAS tokens.
- **FR-063**: The Operator API MUST provide authenticated endpoints for the Cloud Imaging Portal backend to manage boot image lifecycle (create upload session, upload artifact, finalize publish, list, update metadata, delete) separately from OS images, and MUST broker the staged boot image blob write and publish commit to the Imaging Core API over Private Link.
- **FR-064**: The Operator API MUST broker operator-facing requests to the Imaging Core API over Private Link and MUST NOT directly expose Imaging Core API endpoints.

### Key Entities

- **DeviceSession**: Represents a single imaging session for one device. Attributes: session ID, pairing passcode hash (derived from 6-character code format uppercase A-Z and 0-9 excluding I/O/1/0), one-time passcode expiry/consumed flags, opaque device-session token reference, token expiry, state (SessionInit -> SessionAllowed -> SessionAssigned -> SessionStarted -> SessionInProgress -> SessionCompleted/SessionFailed), assigned OS image reference, current SAS token and expiry, last heartbeat timestamp, per-step progress details keyed by step identifier (format-disk, retrieve-metadata, download-image, apply-image, and any additional session-specific steps), overall imaging completion percentage, created timestamp, terminal timestamp, and purge-at timestamp (24-hour terminal retention).
- **OSImage**: A Windows OS image stored in the Storage Account. Attributes: image ID, name, version, description, size, storage path, upload date, in-use flag, SHA256 hash (for cache validation).
- **CachedOSImage**: A cached OS image on the USB cache partition. Attributes: cache ID, source image ID, version, SHA256 hash, cached timestamp, size, last-accessed timestamp.
- **ImagingStep**: An individual step within an imaging job. Attributes: step name, status, started-at, completed-at, error detail, and stepProgressPercent (optional, integer 0-100) when a step can report sub-progress.
- **SASToken**: A time-limited SAS token for an OS image download. Attributes: URL, expiry time, associated session.
- **BrandingConfiguration**: Portal branding settings. Attributes: logo URL, primary color, accent color, application name.
- **BootImage**: A prepared WinPE + Cloud Imaging Client application package suitable for deployment to USB media. Attributes: image ID, version, created-at timestamp, boot image size, manifest content, storage path in blob.
- **BootImageManifest**: Metadata embedded in a boot image. Attributes: manifest version, image version, created timestamp, WinPE version, client app version, component checksums, deployment instructions.
- **UsbPreparationProfile**: Input configuration used by the Cloud Imaging Media Builder app. Attributes: profile ID, WinPE source path, client payload path, cache partition size, boot partition label, cache partition label.
- **UsbPreparationManifest**: Output manifest generated after USB media preparation. Attributes: manifest version, preparation timestamp, tool version, boot image version, selected disk identifier, partition schema, validation results, auto-start configuration status.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A technician can boot a device, couple it via passcode, and have OS imaging fully complete within 15 minutes for a standard-tier deployment. With a cached image matching API hash, imaging completes within 5 minutes.
- **SC-002**: The Cloud Imaging Client UI remains fully responsive (no perceptible freeze) throughout the entire imaging workflow, including during download and apply phases.
- **SC-003**: The Cloud Imaging Portal supports at least 50 simultaneous device imaging sessions with real-time progress updates without degradation.
- **SC-004**: OS image SAS tokens expire within a configurable window with a default of 4 hours; expired tokens are not accepted by the Storage Account.
- **SC-005**: Bulk assignment of an OS image to 20 or more devices completes initiation within 5 seconds of the technician confirming the operation.
- **SC-006**: Cloud Imaging Portal branding changes are reflected to all users within one page reload, with no redeployment required.
- **SC-007**: The Imaging Core API has no publicly reachable network endpoint; all reachability is exclusively through designated private channels from the Device Gateway API and Operator API.
- **SC-008**: All CRUD operations on OS images (catalog of up to 500 items) complete within 3 seconds.
- **SC-009**: A new customer environment (clean Azure subscription + tenant) can be deployed end-to-end from the packaged artifacts in <= 120 minutes without source-code changes.
- **SC-010**: Promotion from dev to test and test to production requires only parameter/environment value changes and results in zero application code differences.
- **SC-011**: A WinPE client package generated from documented steps can be produced and validated by an operator following documentation only, with no undocumented manual intervention.
- **SC-012**: Boot image generation completes within 15 minutes on a standard Windows 10/11 technician workstation with typical network connectivity and I/O performance.
- **SC-013**: USB media preparation completes within 10 minutes for a standard 32 GB USB 3.x device, including partitioning, boot image download via SAS URL, and payload deployment.
- **SC-014**: In validation runs of 20 prepared USB devices, 100% boot into WinPE and auto-launch the Cloud Imaging Client application without manual launch steps.
- **SC-015**: The Operator API retrieves boot image metadata and generates SAS URLs with p95 latency <= 300 ms under 10 concurrent authenticated Cloud Imaging Media Builder requests.

---

## Assumptions

- Devices are running a WinPE environment with outbound network connectivity (wired or Wi-Fi) to reach the Device Gateway API endpoint.
- Technicians run the Cloud Imaging Media Builder application on Windows 10/11 devices with administrative rights required for disk partitioning and boot file operations.
- Technicians have valid Entra ID credentials with permissions to access the Operator API (granted via RBAC roles defined at deployment time).
- The organization's Entra ID tenant has been configured in the deployment for Cloud Imaging Media Builder and Cloud Imaging Portal authentication.
- Azure infrastructure (Storage Account, Device Gateway API, Operator API, Imaging Core API, App Service, Private Link, Entra ID) is pre-provisioned prior to first use.
- Cloud Imaging Portal authentication is handled via Azure Entra ID (enterprise identity); device-level pairing uses the one-time session passcode, while ongoing Cloud Imaging Client API calls use a separate device-session token.
- The Cloud Imaging Client operates in a minimal-trust, ephemeral environment: no pre-shared secrets, certificates, or credentials are embedded in the WinPE image.
- One active imaging session per device at a time; concurrent sessions on the same device are not supported in v1.
- OS images stored in the Storage Account are in a format compatible with the Windows imaging toolchain (e.g., WIM or ESD); format validation is out of scope for v1.
- Mobile-optimized Cloud Imaging Portal layout is out of scope for v1 (desktop browser is the primary target).
- SAS token expiry defaults to 4 hours and is configurable per deployment by an administrator.
- The Cloud Imaging Portal is a community-distributed solution; all configurable defaults MUST be documented clearly for self-hosters.
- USB media prepared by Cloud Imaging Media Builder includes exactly two partitions: a cache partition for OS image caching (sizing per deployment, minimum 20 GB recommended for caching typical 5-10 GB images) and a bootable partition for WinPE boot image deployment.

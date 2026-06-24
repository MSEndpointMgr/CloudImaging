# Tasks: Cloud Windows Imaging

**Input**: Design documents from `/specs/001-cloud-windows-imaging/`

**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/, quickstart.md

**Tests**: Included. Constitution mandates test-first development and coverage gates.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently.

## Format: `[ID] [P?] [Story] Description`

- `[P]`: Can run in parallel (different files, no unmet dependencies)
- `[Story]`: User story label (`[US1]` ... `[US7]`)
- Every task includes an exact file path

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Initialize the monorepo, project scaffolding, shared build settings, and baseline deployment templates aligned to the six-component architecture.

- [ ] T001 Create monorepo folder structure in src/CloudImaging.Contracts/, src/CloudImaging.DeviceGatewayApi/, src/CloudImaging.OperatorApi/, src/CloudImaging.ImagingCoreApi/, src/CloudImaging.Client/, src/CloudImaging.MediaBuilder/, src/cloud-imaging-portal/client/, src/cloud-imaging-portal/server/, src/deploy/bicep/, src/deploy/parameters/, src/deploy/scripts/, tests/, docs/
- [ ] T002 Create .NET solution and add projects in CloudImaging.sln for CloudImaging.Contracts, CloudImaging.DeviceGatewayApi, CloudImaging.OperatorApi, CloudImaging.ImagingCoreApi, CloudImaging.Client, and CloudImaging.MediaBuilder
- [ ] T003 Configure solution-wide diagnostics, nullable settings, and warning-as-error policy in Directory.Build.props
- [ ] T004 [P] Create package manifests and scripts for the portal workspace in src/cloud-imaging-portal/client/package.json and src/cloud-imaging-portal/server/package.json
- [ ] T005 [P] Configure TypeScript strict mode and path aliases in src/cloud-imaging-portal/client/tsconfig.json and src/cloud-imaging-portal/server/tsconfig.json
- [ ] T006 [P] Configure lint and format rules for portal code in src/cloud-imaging-portal/client/eslint.config.js and src/cloud-imaging-portal/server/eslint.config.js
- [ ] T007 [P] Configure frontend build baseline with Vite and Tailwind in src/cloud-imaging-portal/client/vite.config.ts and src/cloud-imaging-portal/client/tailwind.config.ts
- [ ] T008 [P] Add environment templates for self-hosting in src/cloud-imaging-portal/client/.env.example and src/cloud-imaging-portal/server/.env.example
- [ ] T009 Create self-hosting deployment parameter templates in src/deploy/parameters/dev.parameters.json, src/deploy/parameters/test.parameters.json, and src/deploy/parameters/prod.parameters.json
- [ ] T010 [P] Create initial CI workflow scaffold (triggers, base jobs, artifact layout) in .github/workflows/ci.yml; also create stub files for the core dev team's Azure deployment workflows (.github/workflows/deploy-dev.yml with workflow_dispatch trigger and placeholder IaC and component deploy jobs, .github/workflows/deploy-components.yml with workflow_dispatch trigger and component name input and placeholder per-component deploy job); full implementation of both deployment workflows follows in T158

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core contracts, security model, API foundations, and deployment baseline.

**CRITICAL**: No user story work starts until this phase is complete.

- [ ] T011 Implement shared contracts and enums aligned to updated spec in src/CloudImaging.Contracts/Models/DeviceSession.cs (include deviceSerialNumber, deviceManufacturer, deviceModel, preFlightAuthorizationResult fields), src/CloudImaging.Contracts/Models/DeviceRegistrationPayload.cs (include serialNumber, manufacturer, model fields), src/CloudImaging.Contracts/Models/ImagingStep.cs, src/CloudImaging.Contracts/Models/OSImage.cs, src/CloudImaging.Contracts/Models/BootImage.cs, src/CloudImaging.Contracts/Models/BrandingConfiguration.cs, src/CloudImaging.Contracts/Models/PortalConfiguration.cs
- [ ] T012 [P] Implement DeviceSession passcode hashing and consume metadata in src/CloudImaging.ImagingCoreApi/Domain/PasscodeSecurityPolicy.cs and src/CloudImaging.ImagingCoreApi/Domain/DeviceSessionFactory.cs
- [ ] T013 [P] Implement Table Storage repositories for core entities in src/CloudImaging.ImagingCoreApi/Repositories/DeviceSessionRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/ImagingStepRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/OSImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BootImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BrandingRepository.cs
- [ ] T014 [P] Implement Device Gateway API session token issuance/validation middleware in src/CloudImaging.DeviceGatewayApi/Security/DeviceSessionTokenService.cs and src/CloudImaging.DeviceGatewayApi/Middleware/DeviceSessionTokenValidationMiddleware.cs
- [ ] T014a [P] Implement Device Gateway API per-session rate limiting middleware: sliding-window counter keyed by device-session token, 10 calls per 30 seconds, returns HTTP 429 with Retry-After header on excess; public session bootstrap endpoint is exempt from rate limiting (FR-018) in src/CloudImaging.DeviceGatewayApi/Middleware/RateLimitingMiddleware.cs
- [ ] T015 [P] Implement Operator API Entra ID auth and app-role authorization middleware in src/CloudImaging.OperatorApi/Middleware/EntraAuthMiddleware.cs and src/CloudImaging.OperatorApi/Middleware/AppRoleAuthorizationMiddleware.cs
- [ ] T016 [P] Implement service-to-service client stack (gateway/operator to core) in src/CloudImaging.DeviceGatewayApi/Services/ImagingCoreClient.cs and src/CloudImaging.OperatorApi/Services/ImagingCoreClient.cs
- [ ] T017 [P] Implement ProblemDetails middleware across APIs in src/CloudImaging.DeviceGatewayApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.OperatorApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.ImagingCoreApi/Middleware/ProblemDetailsMiddleware.cs
- [ ] T018 [P] Implement portal backend Entra token validation middleware in src/cloud-imaging-portal/server/src/middleware/auth.ts
- [ ] T018a [P] Add portal frontend auth guard contract test: unauthenticated navigation redirects to sign-in, authenticated navigation renders protected content, and MsalProvider is present in the component tree (FR-030) in tests/cloud-imaging-portal/client/auth-guard.test.tsx
- [ ] T018b [P] Implement portal frontend Entra ID auth setup: configure @azure/msal-react MsalProvider with deployment-injected client ID and tenant ID, implement auth context with useAuth hook, and wrap all portal routes in a protected-route guard that redirects unauthenticated users to sign-in (FR-030) in src/cloud-imaging-portal/client/src/main.tsx, src/cloud-imaging-portal/client/src/context/authContext.tsx, and src/cloud-imaging-portal/client/src/components/ProtectedRoute.tsx
- [ ] T018c [P] Add portal backend role enforcement integration tests: CloudImagingTechnician role is denied administrator-only operations (OS image upload, boot image lifecycle management, branding configuration, deployment configuration changes including pre-flight auth toggle), CloudImagingAdministrator role is permitted all operations, and requests with a missing or invalid roles claim are rejected with HTTP 403 (FR-040, FR-040a) in tests/cloud-imaging-portal/server/role-enforcement.test.ts
- [ ] T018d [P] Implement portal backend user-level role authorization middleware: extract CloudImagingAdministrator/CloudImagingTechnician roles from the validated JWT roles claim and enforce per-route access restrictions per FR-040a (Technician permitted: view sessions, couple by passcode, assign OS images from catalog, initiate bulk assignment, monitor progress, read OS image catalog; Administrator required for: OS image upload/edit/delete, boot image lifecycle management, branding configuration, deployment configuration including pre-flight auth toggle), return HTTP 403 on role violation; apply after Entra token validation middleware (T018) on all authenticated portal backend routes (FR-040, FR-040a) in src/cloud-imaging-portal/server/src/middleware/roleGuard.ts
- [ ] T018e Implement portal app shell and sidebar navigation: fixed left sidebar with five labeled navigation items in order -- Sessions (default landing route), OS Images, Boot Images, Branding, and Configuration; react-router-dom v7 route definitions for all five sections; navigation replaces main content area without a full page reload; sidebar present on all authenticated routes; depends on T018b ProtectedRoute (FR-040) in src/cloud-imaging-portal/client/src/components/AppShell.tsx, src/cloud-imaging-portal/client/src/components/Sidebar.tsx, and src/cloud-imaging-portal/client/src/App.tsx
- [ ] T019 [P] Configure OpenAPI export for Device Gateway API and Operator API in src/CloudImaging.DeviceGatewayApi/OpenApi/OpenApiConfig.cs and src/CloudImaging.OperatorApi/OpenApi/OpenApiConfig.cs
- [ ] T020 [P] Implement OpenAPI type generation scripts for portal in src/deploy/scripts/generate-types.ps1 and src/cloud-imaging-portal/server/package.json
- [ ] T021 [P] Add baseline Bicep modules for networking, identities, and compute in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/networking.bicep
- [ ] T022 [P] Add Bicep module for private Imaging Core API and Private Link in src/deploy/bicep/modules/imaging-core-api.bicep
- [ ] T023 [P] Add Bicep modules for Device Gateway API and Operator API in src/deploy/bicep/modules/device-gateway-api.bicep and src/deploy/bicep/modules/operator-api.bicep
- [ ] T024 [P] Add Bicep modules for portal client/server resources in src/deploy/bicep/modules/cloud-imaging-portal.bicep
- [ ] T025 [P] Expand CI scaffold to full matrix execution and required quality gates in .github/workflows/ci.yml
- [ ] T025a [P] Implement Application Insights SDK instrumentation and structured telemetry in DeviceGatewayApi, OperatorApi, ImagingCoreApi, and portal backend: requests, dependencies, exceptions, and custom trace events; connection string injected via app settings with no hardcoded instrumentation keys (FR-065, FR-067) in src/CloudImaging.DeviceGatewayApi/Program.cs, src/CloudImaging.OperatorApi/Program.cs, src/CloudImaging.ImagingCoreApi/Program.cs, and src/cloud-imaging-portal/server/src/app.ts
- [ ] T025b [P] Implement rolling log file output in Cloud Imaging Client and Cloud Imaging Media Builder WPF apps: maximum 10 MB per file, 5 files retained, structured format, no network dependency (FR-066) in src/CloudImaging.Client/Logging/LoggingConfiguration.cs and src/CloudImaging.MediaBuilder/Logging/LoggingConfiguration.cs
- [x] T026 Align canonical contract documents to current architecture names and endpoint scope in specs/001-cloud-windows-imaging/contracts/device-gateway-api.md, specs/001-cloud-windows-imaging/contracts/imaging-core-api.md, specs/001-cloud-windows-imaging/contracts/operator-api.md, and specs/001-cloud-windows-imaging/contracts/cloud-imaging-portal-api.md -- COMPLETED June 24, 2026
- [ ] T130 [P] Implement PortalConfiguration Table Storage repository in src/CloudImaging.ImagingCoreApi/Repositories/PortalConfigurationRepository.cs (entity model PortalConfiguration.cs is defined in T011 via src/CloudImaging.Contracts/Models/PortalConfiguration.cs)
- [ ] T131 [P] Implement Imaging Core API PortalConfiguration get/put endpoints and Operator API proxy endpoints in src/CloudImaging.ImagingCoreApi/Functions/PortalConfigurationFunctions.cs and src/CloudImaging.OperatorApi/Functions/PortalConfigurationFunctions.cs
- [ ] T026a Validate SC-017 walking skeleton milestone: with all six components deployed to the shared dev Azure subscription, smoke-test the minimum end-to-end path -- Cloud Imaging Client registers a session through Device Gateway API and Imaging Core API, an operator couples and assigns via Cloud Imaging Portal through Operator API and Imaging Core API, and the Cloud Imaging Client receives the assignment and displays a terminal ResultsView; document results in docs/validation-walking-skeleton.md; this milestone gates the start of full-feature iteration on all components (SC-017)

**Checkpoint**: Foundation complete; user story phases may begin.

---

## Phase 3: User Story 1 - Device Boot and Session Initiation (Priority: P1) 🎯 MVP

**Goal**: Cloud Imaging Client starts in WinPE, creates session through Device Gateway API, receives one-time pairing passcode and device-session token.

**Independent Test**: Boot client in WinPE, confirm Imaging operation in OperationSelectionView, register a new session, display passcode within 10 seconds, and verify session record exists with the SessionAllowed path and no portal interaction; also verify that a device not found in Autopilot or Corporate Identifiers transitions immediately to SessionNotAuthorized and the client displays ResultsView Not Authorized without any polling delay.

### Tests for User Story 1

- [ ] T027 [P] [US1] Add Device Gateway API contract test for POST /api/v1/sessions in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CreateSessionContractTests.cs
- [ ] T027a [P] [US1] Add Device Gateway API contract test for per-session rate limiting: verify HTTP 429 with Retry-After header returned when 10-call-per-30-second window is exceeded on authenticated endpoints, and verify the public session bootstrap endpoint is exempt (FR-018) in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/RateLimitingContractTests.cs
- [ ] T028 [P] [US1] Add Imaging Core API integration test for server-generated passcode hash-at-rest behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/CreateSessionSecurityIntegrationTests.cs
- [ ] T029 [P] [US1] Add Cloud Imaging Client startup and session registration view model tests, including verification that the registration payload contains device identity fields and hardware metadata (motherboard, BIOS, NIC identifiers, storage layout) collected silently before registration in tests/CloudImaging.Client.Tests/SessionRegistrationViewModelTests.cs
- [ ] T029a [P] [US1] Add Cloud Imaging Client unit tests for BrandingLogoService: logo-found path (reads logo asset from executable directory and returns it for display), fallback-to-default path (no logo asset found in executable directory), and executable-relative path resolution (FR-002a) in tests/CloudImaging.Client.Tests/BrandingLogoServiceTests.cs
- [ ] T132 [P] [US1] Add Imaging Core API integration tests for device pre-flight authorization: Autopilot V1 Graph match advances to SessionAllowed, Corporate Identifiers Graph match advances to SessionAllowed, no-match transitions immediately to SessionNotAuthorized (terminal state), and disabled-mode direct SessionAllowed transition in tests/CloudImaging.ImagingCoreApi.Tests/Integration/DevicePreFlightAuthorizationIntegrationTests.cs
- [ ] T133 [P] [US1] Add Cloud Imaging Client unit tests for ResultsView all three terminal outcomes: Success (SessionCompleted shows confirmation), Failure (SessionFailed shows support reference code and remediation), Not Authorized (SessionNotAuthorized shows device serial number and enrollment guidance with no further polling) in tests/CloudImaging.Client.Tests/ResultsViewTests.cs

### Implementation for User Story 1

- [ ] T030 [US1] Implement Imaging Core API create-session endpoint with one-time passcode generation and hash persistence in src/CloudImaging.ImagingCoreApi/Functions/CreateSessionFunction.cs
- [ ] T031 [US1] Implement Device Gateway API POST /api/v1/sessions endpoint: accept device registration payload including serialNumber, manufacturer, and model fields, forward device identity fields to Imaging Core API as part of session creation, and return device-session token and one-time passcode in src/CloudImaging.DeviceGatewayApi/Functions/CreateSessionFunction.cs
- [ ] T032 [US1] Implement Cloud Imaging Client OperationSelectionView (Imaging/Decommissioning cards, Continue action triggers session registration with silent hardware metadata collection: motherboard, BIOS, NICs, storage layout) in src/CloudImaging.Client/Views/OperationSelectionView.xaml, src/CloudImaging.Client/ViewModels/OperationSelectionViewModel.cs, and src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs
- [ ] T032a [P] [US1] Implement Cloud Imaging Client branding logo loader service: reads logo asset from executable directory (embedded in boot image WIM by the Generate Boot Image workflow) at startup and provides it to the application header; falls back to MSEndpointMgr default logo if no asset found in executable directory (FR-002a) in src/CloudImaging.Client/Services/BrandingLogoService.cs
- [ ] T032b [P] [US1] Implement Cloud Imaging Client WPF window fixed-layout baseline: 1024x768 minimum window size, centered both axes on display at startup, no-scroll constraint enforced across all four views in src/CloudImaging.Client/MainWindow.xaml
- [ ] T033 [US1] Implement Cloud Imaging Client SessionInitView: passcode and session ID display while awaiting operator coupling; transitions immediately to ResultsView on SessionNotAuthorized response in src/CloudImaging.Client/Views/SessionInitView.xaml and src/CloudImaging.Client/ViewModels/SessionInitViewModel.cs
- [ ] T034 [US1] Implement startup error handling and retry UX in src/CloudImaging.Client/Services/SessionStartupCoordinator.cs
- [ ] T134 [US1] Implement Imaging Core API device pre-flight authorization service with parallel Microsoft Graph queries against Autopilot V1 (windowsAutopilotDeviceIdentities filtered by serialNumber) and Intune Corporate Identifiers (importedDeviceIdentities filtered by manufacturer,model,serialNumber) in src/CloudImaging.ImagingCoreApi/Services/DevicePreFlightAuthorizationService.cs
- [ ] T135 [US1] Implement Cloud Imaging Client ResultsView displaying all three terminal outcomes: Success (SessionCompleted), Failure (SessionFailed with support reference code, error detail, and remediation with retry option), and Not Authorized (SessionNotAuthorized with device serial number and enrollment guidance; immediate transition from SessionInitView, no further polling) in src/CloudImaging.Client/Views/ResultsView.xaml and src/CloudImaging.Client/ViewModels/ResultsViewModel.cs

**Checkpoint**: US1 independently functional.

---

## Phase 4: User Story 2 - Technician Couples Device and Assigns Image (Priority: P2)

**Goal**: Authenticated Cloud Imaging Portal couples a waiting device by passcode and assigns OS image through Operator API.

**Independent Test**: With a waiting session created from US1, couple by passcode in portal and verify client transitions to `SessionAssigned` and retrieves assignment details.

### Tests for User Story 2

- [ ] T035 [P] [US2] Add Operator API contract test for couple operation in tests/CloudImaging.OperatorApi.Tests/Contracts/CoupleSessionContractTests.cs
- [ ] T035a [P] [US2] Add Operator API contract test for single-session image assign endpoint (POST /api/sessions/{sessionId}/assign): verify CloudImagingPortal role enforcement, session-not-found (404), image-not-found (400), session-not-in-assignable-state (409), and successful assignment response in tests/CloudImaging.OperatorApi.Tests/Contracts/AssignSessionContractTests.cs
- [ ] T036 [P] [US2] Add Imaging Core API integration test for passcode consume-on-success and conflict handling in tests/CloudImaging.ImagingCoreApi.Tests/Integration/PasscodeConsumeConflictIntegrationTests.cs
- [ ] T036a [P] [US2] Add Imaging Core API integration test for single-session image assign endpoint: SessionAssigned-state session accepts assignment, unknown image ID returns 400, non-Assigned-state session returns 409, and response includes initial SAS token URL with expiry from PortalConfiguration.sasTokenUrlExpiryMinutes in tests/CloudImaging.ImagingCoreApi.Tests/Integration/AssignSessionIntegrationTests.cs
- [ ] T037 [P] [US2] Add portal backend integration test for couple endpoint in tests/cloud-imaging-portal/server/session-couple.test.ts
- [ ] T037a [P] [US2] Add portal backend integration test for single-session assign route (POST /sessions/:id/assign) in tests/cloud-imaging-portal/server/session-assign.test.ts
- [ ] T038 [P] [US2] Add portal frontend integration test for passcode coupling flow in tests/cloud-imaging-portal/client/session-couple-flow.test.tsx
- [ ] T038a [P] [US2] Add portal frontend integration test for single-session image assign flow: Assigned-state row shows [Assign Image] button, clicking opens AssignImageDialog, confirming image selection triggers POST /sessions/:id/assign and transitions row to started state in tests/cloud-imaging-portal/client/session-assign-flow.test.tsx

### Implementation for User Story 2

- [ ] T039a [US2] Implement Imaging Core API session couple endpoint (POST /api/internal/sessions/couple): verify passcode hash against stored hash, confirm passcode is not expired and not already consumed, transition session from SessionAllowed to SessionAssigned, and invalidate passcode on success; return 409 on already-coupled passcode and 404 on unknown or expired passcode in src/CloudImaging.ImagingCoreApi/Functions/CoupleSessionFunction.cs
- [ ] T039b [US2] Implement Imaging Core API single-session image assign endpoint (POST /api/internal/sessions/{sessionId}/assign): validate session is in SessionAssigned state with no prior image assignment, validate the referenced OSImage is active in catalog, persist the image assignment, and issue initial SAS token URL using PortalConfiguration.sasTokenUrlExpiryMinutes (default 240 min) in src/CloudImaging.ImagingCoreApi/Functions/AssignSessionFunction.cs
- [ ] T040 [US2] Implement Operator API couple endpoint and role checks in src/CloudImaging.OperatorApi/Functions/CoupleSessionFunction.cs
- [ ] T040a [US2] Implement Operator API single-session image assign endpoint (POST /api/sessions/{sessionId}/assign) with CloudImagingPortal role enforcement and proxy call to Imaging Core API assign endpoint over Private Link in src/CloudImaging.OperatorApi/Functions/AssignSessionFunction.cs
- [ ] T041 [US2] Implement portal backend couple route proxy to Operator API in src/cloud-imaging-portal/server/src/routes/sessions.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [ ] T041a [US2] Implement portal backend single-session assign route (POST /sessions/:id/assign) proxying to Operator API POST /api/sessions/{sessionId}/assign in src/cloud-imaging-portal/server/src/routes/sessions.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [ ] T042 [US2] Implement Cloud Imaging Portal Sessions section UI: (1) session filter tabs (Active default, Completed, Failed, All) with real-time count badges sourced from GET /api/sessions?filter=... (FR-031) in src/cloud-imaging-portal/client/src/components/SessionFilterTabs.tsx; (2) per-row checkboxes, Select All and Deselect All controls, and eligible-count selection footer displaying the count of checked Assigned-state rows regardless of other-state checked rows (FR-035) in src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx; (3) persistent [Couple Device] toolbar button with passcode coupling modal -- inline error on invalid/expired/consumed passcode, modal stays open on failure, closes on success (FR-032) in src/cloud-imaging-portal/client/src/components/CoupleSessionDialog.tsx; (4) [Assign Image] row-level action button visible on Assigned-state rows and reusable searchable OS image selection modal displaying name/version/file size, shared between single-session and bulk assignment paths (FR-033) in src/cloud-imaging-portal/client/src/components/AssignImageDialog.tsx
- [ ] T043 [US2] Implement Device Gateway API status polling response mapping for assigned sessions; establish response contract to include both currentStep (active ImagingStep name, null until imaging begins) and overallProgressPercent per plan.md constraint in src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs
- [ ] T044 [US2] Implement Cloud Imaging Client assignment poller transition logic in src/CloudImaging.Client/Services/SessionStatusPoller.cs

**Checkpoint**: US2 independently functional.

---

## Phase 5: User Story 3 - Cloud Imaging Client Downloads and Applies Windows Image (Priority: P3)

**Goal**: Client downloads and applies image using SAS token URLs, reports step-level progress, refreshes SAS token URLs when needed, and keeps UI responsive.

**Independent Test**: Simulate assigned session and validate complete step pipeline (download/apply/report/complete) with refresh and failure handling.

### Tests for User Story 3

- [ ] T045 [P] [US3] Add Device Gateway API contract test for progress relay endpoint: verify status responses include both currentStep (active ImagingStep name) and overallProgressPercent fields per plan.md constraint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/ReportProgressContractTests.cs
- [ ] T046 [P] [US3] Add Imaging Core API integration test for lifecycle transitions and heartbeat timeout policy in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs
- [ ] T047 [P] [US3] Add Cloud Imaging Client workflow tests for background download/apply and no UI blocking in tests/CloudImaging.Client.Tests/ImagingWorkflowViewModelTests.cs
- [ ] T047a [P] [US3] Add WinPE UI thread blocking detection test using performance counters and async verification; also assert that the MainWindow meets the 1024x768 minimum dimensions and centered-window layout constraint (FR-002b) at startup in tests/CloudImaging.Client.Tests/UiThreadResponsivenessTests.cs
- [ ] T047b [P] [US3] Add Device Gateway API contract test for cache validation endpoint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CacheValidationContractTests.cs
- [ ] T047c [P] [US3] Add Cloud Imaging Client cache hit/miss workflow tests with hash validation and cache-skip behavior when insufficient space remains on cache partition after 30-day purge (FR-009d) in tests/CloudImaging.Client.Tests/ImageCacheValidationTests.cs
- [ ] T047d [P] [US3] Add Imaging Core API integration test for overall imaging completion percentage persistence and retrieval in tests/CloudImaging.ImagingCoreApi.Tests/Integration/OverallProgressIntegrationTests.cs

### Implementation for User Story 3

- [ ] T048 [US3] Implement Imaging Core API progress endpoint, state transitions, overall imaging completion percentage calculation, and ensure all session status responses include both currentStep (active ImagingStep name) and overallProgressPercent per plan.md constraint in src/CloudImaging.ImagingCoreApi/Functions/ReportProgressFunction.cs and src/CloudImaging.ImagingCoreApi/Services/OverallProgressCalculator.cs
- [ ] T049 [US3] Implement Imaging Core API SAS refresh endpoint with 15-minute threshold support in src/CloudImaging.ImagingCoreApi/Functions/RefreshSasTokenFunction.cs
- [ ] T050 [US3] Implement Device Gateway API progress relay endpoint; ensure all session status responses include both currentStep (active ImagingStep name) and overallProgressPercent per plan.md constraint in src/CloudImaging.DeviceGatewayApi/Functions/ReportProgressFunction.cs and src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs (see also T043 for the GetSessionStatus response contract baseline established in US2 Phase 4)
- [ ] T051 [US3] Implement Device Gateway API SAS refresh endpoint in src/CloudImaging.DeviceGatewayApi/Functions/RefreshSasTokenFunction.cs
- [ ] T052 [US3] Implement Cloud Imaging Client image download service (SAS for blob download only) in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [ ] T053 [US3] Implement Cloud Imaging Client image apply orchestration in src/CloudImaging.Client/Services/ImageApplyService.cs
- [ ] T054 [US3] Implement Cloud Imaging Client per-step progress reporter and overall imaging completion percentage publisher in src/CloudImaging.Client/Services/ImagingProgressReporter.cs
- [ ] T055 [US3] Implement Cloud Imaging Client SAS refresh coordinator in src/CloudImaging.Client/Services/SasRefreshCoordinator.cs
- [ ] T056 [US3] Implement Cloud Imaging Client ProgressView (3-node horizontal step indicator: Format, Download, Apply; visual distinction for completed/active/pending; transitions to ResultsView on Apply complete) and failure/retry UX with support codes in src/CloudImaging.Client/Views/ProgressView.xaml, src/CloudImaging.Client/ViewModels/ProgressViewModel.cs, and src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs
- [ ] T056a [US3] Implement Cloud Imaging Client OS image cache storage service with SHA256 hash computation and cache metadata persistence in src/CloudImaging.Client/Services/ImageCacheService.cs
- [ ] T056b [US3] Implement Device Gateway API cache validation endpoint (POST /api/v1/sessions/{sessionId}/cache/validate) in src/CloudImaging.DeviceGatewayApi/Functions/CacheValidationFunction.cs
- [ ] T056c [US3] Implement Cloud Imaging Client cache-hit pre-download logic: hash validation, skip-download on match, and cache invalidation on mismatch in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [ ] T056d [US3] Implement Cloud Imaging Client cache cleanup: 30-day expiry auto-purge on next boot and orphaned entry removal; skip cache write and proceed with direct download when insufficient space remains after purge; no LRU eviction of valid cache entries (FR-009d) in src/CloudImaging.Client/Services/ImageCacheMaintenanceService.cs

**Checkpoint**: US3 independently functional.

---

## Phase 6: User Story 7 - Cloud Imaging Media Builder for WinPE Boot (Priority: P3)

**Goal**: Provide Media Builder app workflows for boot image generation and Entra-authenticated USB preparation/download/deploy.

**Independent Test**: Generate boot image locally with signed manifest, then use Entra-signed-in USB workflow to retrieve boot image metadata/SAS through Operator API, prepare USB, and validate auto-start boot behavior.

### Tests for User Story 7

- [ ] T057 [P] [US7] Add Media Builder boot image generation tests in tests/CloudImaging.MediaBuilder.Tests/BootImageGenerationTests.cs
- [ ] T152 [P] [US7] Add Media Builder GenerateBootImageView source selection tests: GitHub releases API resolves latest tag and downloads Client binaries with real-time progress, custom local path validation accepts pre-downloaded binaries, and output folder path confirmed before generation starts (FR-051a) in tests/CloudImaging.MediaBuilder.Tests/GenerateBootImageSourceSelectionTests.cs
- [ ] T154 [P] [US7] Add Media Builder GenerateBootImageView completion notification tests: output folder path displayed on success, next-step portal upload instructions shown, no portal upload initiated by the app (FR-051b) in tests/CloudImaging.MediaBuilder.Tests/GenerateBootImageCompletionTests.cs
- [ ] T058 [P] [US7] Add Media Builder signing/verification tests in tests/CloudImaging.MediaBuilder.Tests/BootImageSigningTests.cs
- [ ] T059 [P] [US7] Add Media Builder Entra sign-in tests in tests/CloudImaging.MediaBuilder.Tests/EntraSignInTests.cs
- [ ] T150 [P] [US7] Add Media Builder ADK prerequisite detection tests: ADK and WinPE add-on installed path enables both workflow cards, not-installed path disables both cards with installation guidance message (FR-050a) in tests/CloudImaging.MediaBuilder.Tests/AdkPrerequisiteDetectionTests.cs
- [ ] T060 [P] [US7] Add Operator API contract tests for boot image list and SAS token URL issuance in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageApiContractTests.cs
- [ ] T113 [P] [US7] Add Operator API contract tests for portal-role boot image lifecycle CRUD endpoints in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageLifecycleContractTests.cs
- [ ] T114 [P] [US7] Add Imaging Core API integration tests for boot image lifecycle CRUD and role-bound behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootImageLifecycleIntegrationTests.cs
- [ ] T061 [P] [US7] Add Media Builder USB device qualification tests: bus type = USB and removable flag = true qualifies, non-USB bus type excluded, non-removable device excluded, host OS/system disk always blocked regardless of criteria (FR-054) in tests/CloudImaging.MediaBuilder.Tests/UsbDiskValidationTests.cs
- [ ] T062 [P] [US7] Add Media Builder USB partition layout and deployment tests; branding logo is embedded in the boot image WIM during Generate Boot Image and requires no separate write step during USB preparation (FR-053) in tests/CloudImaging.MediaBuilder.Tests/UsbProvisioningWorkflowTests.cs
- [ ] T160 [P] [US7] Add Media Builder support reference code format compliance tests: assert that all six CMB failure stages emit support reference codes conforming to {CMB}-{SessionRef}-{StageCode}-{EpochSeconds} with component prefix CMB and valid stage codes (DVI=disk-validation, PRT=partitioning, BID=boot-image-download, BCF=boot-config for the USB workflow; equivalent stage codes for boot image generation workflow); assert SessionRef uses a short operation-stage identifier when no session context is available; assert EpochSeconds is a non-zero Unix epoch value at time of error (FR-058) in tests/CloudImaging.MediaBuilder.Tests/SupportReferenceCodeTests.cs

### Implementation for User Story 7

- [ ] T063 [US7] Implement Cloud Imaging Media Builder SignInView (welcome screen with branding logo, app title, and short capability description; Entra ID sign-in as first required action; MUST NOT navigate to OperationSelectionView until sign-in completes; FR-050, FR-052) and Entra auth orchestration service in src/CloudImaging.MediaBuilder/Views/SignInView.xaml, src/CloudImaging.MediaBuilder/ViewModels/SignInViewModel.cs, and src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs
- [ ] T151 [US7] Implement Cloud Imaging Media Builder OperationSelectionView (Generate Boot Image card and Prepare USB Storage Device card) and Windows ADK prerequisite detection service; verify copype.cmd and makewinpemedia present at startup; visually disable both workflow cards with installation guidance when Windows ADK or WinPE add-on not detected (FR-050, FR-050a) in src/CloudImaging.MediaBuilder/Views/OperationSelectionView.xaml, src/CloudImaging.MediaBuilder/ViewModels/OperationSelectionViewModel.cs, and src/CloudImaging.MediaBuilder/Services/AdkPrerequisiteDetectionService.cs
- [ ] T153 [US7] Implement Cloud Imaging Media Builder GenerateBootImageView source selection UI: GitHub auto-download option (calls MSEndpointMgr GitHub public releases API to resolve latest tag and download Client binaries with real-time progress display) and custom local path option (supports offline/airgap deployments); output folder picker; generation must not start until source and output folder confirmed (FR-051a) in src/CloudImaging.MediaBuilder/Views/GenerateBootImageView.xaml, src/CloudImaging.MediaBuilder/ViewModels/GenerateBootImageViewModel.cs, and src/CloudImaging.MediaBuilder/Services/GitHubReleasesClient.cs
- [ ] T064 [US7] Implement Media Builder boot image generation workflow: assemble WinPE environment, Client binaries from source selected in T153, and manifest; retrieve current branding logo from Operator API and embed alongside Client executable in boot image WIM via BrandingLogoEmbedService (fall back to MSEndpointMgr default if no branding configured; FR-051); display completion notification with output folder path and next-step portal upload instructions on success (FR-051b) in src/CloudImaging.MediaBuilder/Services/BootImageGenerationService.cs and src/CloudImaging.MediaBuilder/Services/BrandingLogoEmbedService.cs
- [ ] T065 [US7] Implement Media Builder boot image signing and verification services in src/CloudImaging.MediaBuilder/Services/BootImageSigningService.cs
- [ ] T066 [US7] Implement Imaging Core API boot image query/SAS endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageFunctions.cs
- [ ] T067 [US7] Implement Operator API boot image query/SAS proxy endpoints in src/CloudImaging.OperatorApi/Functions/BootImageFunctions.cs
- [ ] T115 [US7] Implement Imaging Core API boot image lifecycle CRUD endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageLifecycleFunctions.cs
- [ ] T116 [US7] Implement Operator API portal-role boot image lifecycle proxy endpoints in src/CloudImaging.OperatorApi/Functions/BootImageLifecycleFunctions.cs
- [ ] T117 [US7] Implement portal backend boot image lifecycle routes and service wiring in src/cloud-imaging-portal/server/src/routes/boot-images.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [ ] T121 [P] [US7] Add portal backend contract tests for staged boot image upload session and finalize publish in tests/cloud-imaging-portal/server/boot-image-upload.test.ts
- [ ] T122 [P] [US7] Add Imaging Core API integration tests for staged upload visibility, checksum validation, and publish commit in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootImageUploadLifecycleIntegrationTests.cs
- [ ] T126 [P] [US7] Add portal frontend contract tests for staged boot image upload progress, retry, and finalize publish in tests/cloud-imaging-portal/client/boot-image-upload-flow.test.tsx
- [ ] T068 [US7] Implement Media Builder operator API client for boot image retrieval in src/CloudImaging.MediaBuilder/Services/OperatorApiClient.cs
- [ ] T069 [US7] Implement Media Builder boot image download service in src/CloudImaging.MediaBuilder/Services/BootImageDownloadService.cs
- [ ] T070 [US7] Implement Media Builder USB device qualification (bus type = USB and removable flag = true required; host OS/system disk always blocked regardless of bus type or removable flag; FR-054) and two-partition provisioning in src/CloudImaging.MediaBuilder/Services/UsbSafetyValidationService.cs and src/CloudImaging.MediaBuilder/Services/UsbPartitionProvisioningService.cs
- [ ] T071 [US7] Implement Media Builder boot image deployment to bootable partition, WinPE auto-start configuration, and preparation manifest; branding logo is embedded in the boot image WIM during the Generate Boot Image workflow (T064) and requires no separate retrieval or write step during USB preparation (FR-053); PrepareUsbStorageViewModel MUST subscribe to Windows DeviceWatcher (or equivalent WMI DeviceChanged notification) to auto-refresh the device list on USB plug/unplug events -- newly connected qualifying removable USB devices appear automatically and disconnected devices are removed; expose a manual Refresh command as a fallback (FR-054); implement PrepareStorageDeviceView.xaml with the device list, partition progress, and Refresh button in src/CloudImaging.MediaBuilder/Services/BootImageDeploymentService.cs, src/CloudImaging.MediaBuilder/ViewModels/PrepareUsbStorageViewModel.cs, and src/CloudImaging.MediaBuilder/Views/PrepareStorageDeviceView.xaml
- [ ] T123 [US7] Implement portal backend staged boot image upload session and finalize publish routes in src/cloud-imaging-portal/server/src/routes/boot-images.ts and src/cloud-imaging-portal/server/src/services/blobUploadService.ts
- [ ] T124 [US7] Implement Operator API upload-session and finalize-publish proxy endpoints for boot images in src/CloudImaging.OperatorApi/Functions/BootImageUploadFunctions.cs
- [ ] T125 [US7] Implement Imaging Core API staged boot image upload session and publish-commit endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageUploadFunctions.cs
- [ ] T125a [US7] Implement Imaging Core API boot image checksum validation, atomic publish-commit operation, and corruption detection in src/CloudImaging.ImagingCoreApi/Services/BootImageValidationService.cs and src/CloudImaging.ImagingCoreApi/Functions/BootImagePublishFunction.cs
- [ ] T127 [US7] Implement portal frontend staged WIM upload flow with chunked progress, retry, and finalize publish UI in src/cloud-imaging-portal/client/src/pages/BootImagesPage.tsx and src/cloud-imaging-portal/client/src/components/BootImageUploadDialog.tsx
- [ ] T128 [US7] Implement portal frontend upload session state handling and progress indicators in src/cloud-imaging-portal/client/src/services/bootImageUploadService.ts and src/cloud-imaging-portal/client/src/components/UploadProgressBar.tsx

**Checkpoint**: US7 independently functional.

---

## Phase 7: User Story 4 - Cloud Imaging Portal Bulk Imaging Operations (Priority: P4)

**Goal**: Enable multi-device coupling/assignment and dashboard monitoring for concurrent imaging.

**Independent Test**: Simulate multiple waiting sessions, perform bulk assignment, verify per-device status updates and partial-failure isolation.

### Tests for User Story 4

- [ ] T072 [P] [US4] Add Operator API bulk assignment contract tests in tests/CloudImaging.OperatorApi.Tests/Contracts/BulkAssignContractTests.cs
- [ ] T073 [P] [US4] Add portal backend bulk assign route tests in tests/cloud-imaging-portal/server/session-bulk-assign.test.ts
- [ ] T074 [P] [US4] Add portal frontend bulk assignment UI tests in tests/cloud-imaging-portal/client/bulk-assignment-flow.test.tsx

### Implementation for User Story 4

- [ ] T075 [US4] Implement Imaging Core API bulk assignment service with conflict-safe processing in src/CloudImaging.ImagingCoreApi/Services/BulkAssignmentService.cs
- [ ] T076 [US4] Implement Operator API bulk assignment endpoint in src/CloudImaging.OperatorApi/Functions/BulkAssignFunction.cs
- [ ] T077 [US4] Implement portal backend bulk assignment route in src/cloud-imaging-portal/server/src/routes/sessions.ts
- [ ] T078 [US4] Implement portal bulk assignment toolbar: context-sensitive [Assign Image to N selected sessions] button appearing when at least one Assigned-state row is checked (N counts only checked Assigned-state rows, regardless of other-state checked rows; FR-035) that opens AssignImageDialog (T042) with an N-count subtitle and N-reflecting confirm label; wires bulk confirmation to Operator API POST /api/sessions/bulk-assign in src/cloud-imaging-portal/client/src/components/BulkAssignPanel.tsx
- [ ] T079 [US4] Implement portal session progress table and inline expandable detail panel: SessionProgressTable showing per-session current step, per-step status, and overall imaging completion percentage; each row includes a [View Details] toggle that expands an inline SessionRowDetailPanel directly below the row displaying a horizontal 3-node step indicator (Format / Download / Apply) with visual distinction for completed, active, and pending nodes, per-step status and timestamps, optional sub-progress, and -- for Failed sessions -- the support reference code and error detail inline (FR-034); multiple rows may be expanded simultaneously in src/cloud-imaging-portal/client/src/components/SessionProgressTable.tsx and src/cloud-imaging-portal/client/src/components/SessionRowDetailPanel.tsx

**Checkpoint**: US4 independently functional.

---

## Phase 8: User Story 5 - Cloud Imaging Portal OS Image Management (Priority: P5)

**Goal**: Support OS image upload/list/edit/delete with in-use guards.

**Independent Test**: Perform full image CRUD, verify catalog consistency, and verify deletion block when image is actively assigned.

### Tests for User Story 5

- [ ] T080 [P] [US5] Add Operator API image CRUD contract tests in tests/CloudImaging.OperatorApi.Tests/Contracts/ImageCatalogContractTests.cs
- [ ] T081 [P] [US5] Add portal backend image management route tests in tests/cloud-imaging-portal/server/image-management.test.ts
- [ ] T082 [P] [US5] Add portal frontend image management UI tests in tests/cloud-imaging-portal/client/image-management-flow.test.tsx
- [ ] T082a [P] [US5] Add portal backend contract tests for chunked OS image upload session, progress tracking, and resumable transfer in tests/cloud-imaging-portal/server/os-image-chunked-upload.test.ts

### Implementation for User Story 5

- [ ] T083 [US5] Implement Imaging Core API image catalog endpoints in src/CloudImaging.ImagingCoreApi/Functions/ImageCatalogFunctions.cs
- [ ] T084 [US5] Implement Imaging Core API active-session delete guard in src/CloudImaging.ImagingCoreApi/Services/ImageDeletionGuardService.cs
- [ ] T085 [US5] Implement Operator API image catalog proxy endpoints in src/CloudImaging.OperatorApi/Functions/ImageCatalogFunctions.cs
- [ ] T086 [US5] Implement portal backend image upload + metadata registration in src/cloud-imaging-portal/server/src/routes/images.ts and src/cloud-imaging-portal/server/src/services/blobUploadService.ts
- [ ] T086a [US5] Implement portal backend chunked upload session service for large OS images (5-10GB) with resumable transfer, progress tracking, and partial-upload cleanup in src/cloud-imaging-portal/server/src/services/chunkedUploadService.ts and src/cloud-imaging-portal/server/src/routes/chunked-upload.ts
- [ ] T087 [US5] Implement portal frontend image management page in src/cloud-imaging-portal/client/src/pages/ImagesPage.tsx
- [ ] T087a [US5] Implement portal frontend chunked upload dialog with progress bar, pause/resume, retry logic, and error recovery for large OS images in src/cloud-imaging-portal/client/src/components/ChunkedUploadDialog.tsx and src/cloud-imaging-portal/client/src/services/chunkedUploadService.ts
- [ ] T088 [US5] Implement portal frontend image editor dialog in src/cloud-imaging-portal/client/src/components/ImageEditorDialog.tsx

**Checkpoint**: US5 independently functional.

---

## Phase 9: User Story 6 - Cloud Imaging Portal Branding Configuration (Priority: P6)

**Goal**: Enable runtime branding updates (logo/colors/app name) without redeploy.

**Independent Test**: Update branding in portal and verify fresh page load reflects all changes.

### Tests for User Story 6

- [ ] T089 [P] [US6] Add Operator API branding endpoint contract tests including GET /api/branding (get/put branding configuration) and GET /api/branding/logo/sas (time-limited SAS token URL for current branding logo asset, consumed by Media Builder boot image generation workflow; FR-062) in tests/CloudImaging.OperatorApi.Tests/Contracts/BrandingContractTests.cs
- [ ] T090 [P] [US6] Add portal backend branding route tests in tests/cloud-imaging-portal/server/branding.test.ts
- [ ] T091 [P] [US6] Add portal frontend branding flow tests in tests/cloud-imaging-portal/client/branding-settings-flow.test.tsx

### Implementation for User Story 6

- [ ] T092 [US6] Implement Imaging Core API branding get/put endpoints in src/CloudImaging.ImagingCoreApi/Functions/BrandingFunctions.cs
- [ ] T093 [US6] Implement Operator API branding proxy endpoints including GET /api/branding and PUT /api/branding for branding configuration get/put, and GET /api/branding/logo/sas to issue a time-limited SAS token URL for the current branding logo asset in the Storage Account for the Media Builder Generate Boot Image workflow (FR-062) in src/CloudImaging.OperatorApi/Functions/BrandingFunctions.cs
- [ ] T094 [US6] Implement portal backend branding routes in src/cloud-imaging-portal/server/src/routes/branding.ts
- [ ] T095 [US6] Implement portal branding settings page and logo upload flow in src/cloud-imaging-portal/client/src/pages/BrandingSettingsPage.tsx
- [ ] T096 [US6] Implement dynamic CSS variable application for runtime branding in src/cloud-imaging-portal/client/src/context/brandingContext.tsx and src/cloud-imaging-portal/client/src/index.css
- [ ] T143 [P] [US6] Add Operator API contract tests for portal configuration get/put endpoints in tests/CloudImaging.OperatorApi.Tests/Contracts/PortalConfigurationContractTests.cs
- [ ] T144 [P] [US6] Add portal backend integration tests for portal configuration route in tests/cloud-imaging-portal/server/portal-configuration.test.ts
- [ ] T145 [P] [US6] Add portal frontend integration tests for deployment configuration page and pre-flight authorization toggle in tests/cloud-imaging-portal/client/portal-configuration-flow.test.tsx
- [ ] T146 [US6] Implement portal backend portal configuration route proxying PortalConfiguration get/put to Operator API in src/cloud-imaging-portal/server/src/routes/portal-config.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [ ] T147 [US6] Implement portal frontend deployment configuration page with pre-flight authorization enable/disable toggle in src/cloud-imaging-portal/client/src/pages/DeploymentConfigPage.tsx and src/cloud-imaging-portal/client/src/components/PreFlightAuthorizationToggle.tsx

**Checkpoint**: US6 independently functional.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Hardening, performance, accessibility, deployment packaging, and final validation.

- [ ] T097 [P] Finalize Bicep deployment graph and module wiring in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/*.bicep
- [ ] T098 [P] Implement deployment and upgrade scripts for self-hosting: deploy.ps1 for fresh deployment into a clean Azure subscription, update.ps1 for in-place upgrade of an existing deployment to a new release version (idempotent; re-applies IaC and redeploys all application components without requiring manual resource deletion or re-entry of unchanged parameters; FR-041, FR-044a), and validate.ps1 for post-deployment validation in src/deploy/scripts/deploy.ps1, src/deploy/scripts/update.ps1, and src/deploy/scripts/validate.ps1
- [ ] T099 [P] Implement CI quality gates (build/test/lint/perf/accessibility) in .github/workflows/ci.yml
- [ ] T100 [P] Add portal WCAG 2.1 AA automated accessibility tests in tests/cloud-imaging-portal/client/accessibility/accessibility-a11y.test.tsx
- [ ] T101 [P] Add performance test for 50 concurrent imaging sessions (SC-003) in tests/performance/session-concurrency.k6.js
- [ ] T102 [P] Add performance benchmark for OS image CRUD with 500-item catalog (SC-008) in tests/performance/image-crud-latency.k6.js
- [ ] T103 [P] Add performance test for Operator API boot image query/SAS generation (SC-015) in tests/performance/operator-api-boot-image-perf.k6.js
- [ ] T129 [P] Add performance test for bulk assignment initiation latency for 20+ devices (SC-005) in tests/performance/bulk-assign-latency.k6.js
- [ ] T104 [P] Add benchmark for boot image generation duration (SC-012) in tests/performance/media-builder-boot-generation-perf.ps1
- [ ] T105 [P] Add benchmark for USB preparation duration (SC-013) in tests/performance/media-builder-usb-prep-perf.ps1
- [ ] T106 [P] Add validation runbook for 20-device auto-launch matrix (SC-014) in docs/validation-usb-autostart-matrix.md
- [ ] T107 [P] Create deployment package specification document in specs/001-cloud-windows-imaging/deployment-package.md; the document MUST include a complete step-by-step Entra ID enterprise app registration setup guide as a dedicated prerequisite section covering all eight steps from the Session June 24, 2026 clarification: (1) sign in to entra.microsoft.com as Cloud Application Administrator; (2) create app registration with Accounts in this organizational directory only; (3) define all five app roles -- CloudImagingAdministrator and CloudImagingTechnician with Allowed member types Users/Groups, and CloudImagingPortal, CloudImagingMediaBuilder with Allowed member types Applications; (4) configure Portal SPA and backend redirect URIs; (5) expose Application ID URI and required delegated scopes; (6) assign CloudImagingPortal and CloudImagingMediaBuilder roles to service principals with admin consent; (7) assign users or security groups to CloudImagingAdministrator and CloudImagingTechnician roles; (8) record clientId and tenantId for IaC deployment parameters; no steps may be omitted; the document MUST also include a dedicated Upgrade section describing how self-hosters apply a new release to an existing deployment using update.ps1, including what the script does, what parameters it requires, and expected duration (FR-044a, FR-045)
- [ ] T108 [P] Update self-hosting guide with environment prerequisites, initial deployment walkthrough, auth setup, and upgrade procedures for applying a new release version to an existing deployment using update.ps1 in docs/self-hosting-guide.md
- [ ] T109 [P] Update operations runbook with troubleshooting for session/token/SAS flows in docs/operations-runbook.md
- [ ] T110 Execute quickstart validation scenarios and capture results in specs/001-cloud-windows-imaging/quickstart.md
- [ ] T111 [P] Add Media Builder resumable download and interrupted-transfer recovery coverage in tests/CloudImaging.MediaBuilder.Tests/BootImageDeploymentTests.cs and src/CloudImaging.MediaBuilder/Services/BootImageDownloadService.cs
- [ ] T112 [P] Add session inactivity expiry, heartbeat failure, and terminal purge coverage in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs and src/CloudImaging.ImagingCoreApi/Services/DeviceSessionLifecycleService.cs
- [ ] T118 [P] Add timed clean-tenant deployment rehearsal validation for <= 120 minute target (SC-009) in tests/validation/deployment-timing-validation.ps1 and docs/validation-deployment-timing.md
- [ ] T119 [P] Add config-only environment promotion validation with artifact and code-diff checks (SC-010) ensuring environment promotion requires only parameter/environment changes in src/deploy/parameters/*.json, endpoint URLs, and secrets -- no source code differences; applies to self-hoster deployments that maintain their own dev/test/prod pipeline; core dev team uses a single shared subscription per FR-043 in tests/validation/promotion-config-only-validation.ps1 and docs/validation-promotion-config-only.md
- [ ] T120 [P] Add documentation-only WinPE package reproducibility validation (SC-011) in tests/validation/winpe-package-repro-validation.ps1 and docs/validation-winpe-package-repro.md
- [ ] T157 [P] Add in-place upgrade validation: starting from a successfully deployed prior release version, run update.ps1 with the new release archive and verify all six components reach the expected new version, all Azure resources reflect the updated IaC state, and the procedure completes without manual Azure portal intervention; document results in docs/validation-upgrade-path.md (FR-041, FR-044a)
- [ ] T159 Configure Azure Workload Identity Federation for GitHub Actions OIDC authentication against the shared dev Azure subscription: create a federated credential on the shared Azure app registration that trusts the GitHub repository with the `environment:dev` subject claim (no service principal client secret required); create the GitHub Actions `dev` environment in the repository and populate all required environment secrets (AZURE_SUBSCRIPTION_ID, AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_RESOURCE_GROUP, FUNCTION_APP_DEVICE_GATEWAY, FUNCTION_APP_OPERATOR, FUNCTION_APP_IMAGING_CORE, APP_SERVICE_PORTAL_BACKEND, STATIC_WEB_APP_PORTAL); document the complete federated credential creation procedure (az ad app federated-credential create command and portal equivalent) as part of the developer environment onboarding guide; this is a one-time prerequisite for T158 and enables zero-credential OIDC authentication for both deploy-dev.yml and deploy-components.yml (FR-047) in docs/dev-environment-setup.md
- [ ] T158 Implement GitHub Actions deployment workflows for the core development team's shared Azure dev environment: (1) deploy-dev.yml -- full-stack workflow triggered via workflow_dispatch that runs Bicep IaC deployment followed by sequential deployment of all five Azure-hosted components (Device Gateway API Function App, Operator API Function App, Imaging Core API Function App, Portal backend App Service, Portal frontend Static Web Apps); Cloud Imaging Client and Media Builder are build-only components and are not Azure-deployed; (2) deploy-components.yml -- per-component workflow triggered via workflow_dispatch with a required component name input (DeviceGatewayApi, OperatorApi, ImagingCoreApi, PortalBackend, PortalFrontend), deploying only the selected component to the shared dev environment; both workflows MUST use OIDC-based federated identity authentication via Azure Workload Identity Federation (no long-lived credential secrets) and MUST read all environment-specific values (subscription ID, resource group name, resource names) from GitHub Actions `dev` environment secrets; neither workflow is included in community release bundles (FR-047) in .github/workflows/deploy-dev.yml and .github/workflows/deploy-components.yml
- [ ] T148 [P] Add Bicep resource and post-deploy script to grant DeviceManagementServiceConfig.Read.All application permission to Imaging Core API managed identity in customer tenant in src/deploy/bicep/modules/imaging-core-api.bicep and src/deploy/scripts/grant-graph-permissions.ps1
- [ ] T149 [P] Add Playwright E2E tests for Cloud Imaging Portal critical paths: passcode entry and device coupling flow, OS image assignment and session state progression, bulk assignment of multiple coupled sessions, and portal branding update with page-reload verification; configure Playwright runner and portal auth setup in tests/cloud-imaging-portal/e2e/portal-critical-paths.spec.ts and tests/cloud-imaging-portal/e2e/portal.setup.ts
- [ ] T155 [P] Add VS Code workspace deployment tasks configuration with one named task per deployable Azure component (DeviceGatewayApi Function App, OperatorApi Function App, ImagingCoreApi Function App, Portal backend App Service, Portal frontend Static Web Apps); each task MUST invoke deploy-components.yml via `gh workflow run --field component=<name>`, providing a quick in-editor deployment trigger to the shared Azure dev environment; an equivalent direct `az`/`azd` CLI fallback MUST be documented as a comment in each task for offline development scenarios; VS Code tasks are for internal core developer use only and MUST NOT be included in community release bundles (FR-046, FR-047) in .vscode/tasks.json
- [ ] T156 [P] Add GitHub Release packaging CI workflow: on version tag push, assemble all seven component release artifacts (DeviceGatewayApi zip, OperatorApi zip, ImagingCoreApi zip, Portal frontend static asset bundle, Portal backend zip, Cloud Imaging Client WinPE binary, Media Builder Windows installer; Portal is one architecture component contributing two separate release artifacts), the complete IaC template bundle, all parameter templates, documentation, deploy.ps1, update.ps1, and validate.ps1 into a single versioned release archive and publish to GitHub Releases; all seven component release artifacts MUST be present in every release archive regardless of whether they changed since the prior tag; the packaging workflow MUST explicitly exclude .github/workflows/deploy-dev.yml, .github/workflows/deploy-components.yml, and .vscode/tasks.json from the release archive as these are for internal developer use only (FR-044a, FR-047) in .github/workflows/release.yml

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup completion; blocks all stories.
- **User Stories (Phase 3-9)**: Depend on Foundational completion.
- **Polish (Phase 10)**: Depends on desired user stories being complete; T159 (Workload Identity Federation) MUST complete before T158 (GitHub Actions deployment workflows implementation).

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2. No dependency on other stories.
- **US2 (P2)**: Starts after Phase 2. Functionally depends on US1 session creation.
- **US3 (P3)**: Starts after Phase 2. Functionally depends on US2 assignment.
- **US7 (P3)**: Starts after Phase 2. Generate Boot Image workflow is independent; Prepare USB Storage Device workflow depends on Operator API and boot image catalog endpoints.
- **US4 (P4)**: Starts after Phase 2. Depends on US2 baseline coupling/assignment endpoints.
- **US5 (P5)**: Starts after Phase 2. Independent of US4 and US6.
- **US6 (P6)**: Starts after Phase 2. Independent of US4 and US5.

### Within Each User Story

- Write tests first and verify they fail.
- Implement domain/service layer before endpoint orchestration.
- Implement UI/client integration after service endpoints exist.
- Validate story independent test criteria before moving on.

### Parallel Opportunities

- Setup tasks marked `[P]` can run in parallel.
- Foundational tasks marked `[P]` can run in parallel after T011.
- Story test tasks marked `[P]` can run in parallel within each story.
- After Phase 2, US4/US5/US6 can be developed in parallel by separate contributors.

---

## Parallel Example: User Story 1

```bash
# Parallel tests
T027  T027a  T028  T029  T029a  T132  T133

# Implementation sequence (T030 is a partial stub; FR-021 pre-flight compliance requires T134)
T030 -> T031 -> T032 -> T033 -> T034 -> T134 -> T135
# T032a and T032b are [P] and can run alongside T033
```

## Parallel Example: User Story 2

```bash
# Parallel tests (all can run concurrently after T011)
T035  T035a  T036  T036a  T037  T037a  T038  T038a

# Implementation sequence (couple before assign; portal backend routes update same files sequentially)
T039a -> T039b -> T040 -> T040a -> T041 -> T041a -> T042 -> T043 -> T044
```

## Parallel Example: User Story 7

```bash
# Parallel tests
T057  T058  T059  T060  T061  T062  T150  T152  T154

# Generation pipeline
T063 -> T151 -> T153 -> T064 -> T065

# Prepare USB pipeline
T066 -> T067 -> T068 -> T069 -> T070 -> T071

# Boot image lifecycle pipeline (FR-063)
T115 -> T116 -> T117

# Staged upload pipeline (portal WIM upload; T121/T122/T126 are parallel tests)
T125 -> T125a -> T124 -> T123 -> T127 -> T128
```

## Parallel Example: Phase 10 (Polish)

```bash
# Independent Polish tasks (all [P] -- can run in parallel across both contributors)
T097  T098  T099  T100  T101  T102  T103  T104  T105  T106
T107  T108  T109  T111  T112  T118  T119  T120  T129  T148
T149  T155  T156  T157  T160

# Sequential chain within Phase 10 (federation setup before workflow implementation)
T159 -> T158

# T110 (quickstart scenario validation) runs after all other Phase 10 tasks complete
T110
```

---

## Implementation Strategy

### MVP First (US1)

1. Complete Phase 1 (Setup).
2. Complete Phase 2 (Foundational).
3. Complete Phase 3 (US1).
4. Validate US1 independently before expanding scope.

### Incremental Delivery

1. Add US2 for coupling/assignment.
2. Add US3 for end-to-end imaging execution.
3. Add US7 for media generation/prep workflows.
4. Add US4, US5, and US6 as operator efficiency/management increments.
5. Complete Phase 10 hardening and release validation.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. Once Foundational is complete:
   - Developer A: US2 + US3
   - Developer B: US7
   - Developer C: US4 + US5 + US6
3. Integrate through CI gates and quickstart scenario validations.

---

## Notes

- `[P]` tasks target separate files and no unmet dependencies.
- `[USx]` labels ensure traceability from tasks to user stories.
- Each story phase is designed to be independently testable.
- Tasks intentionally align to updated architecture: Device Gateway API, Operator API, Imaging Core API, Cloud Imaging Client, Cloud Imaging Portal, Cloud Imaging Media Builder.

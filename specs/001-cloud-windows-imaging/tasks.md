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
- [ ] T010 [P] Create initial CI workflow scaffold (triggers, base jobs, artifact layout) in .github/workflows/ci.yml

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core contracts, security model, API foundations, and deployment baseline.

**CRITICAL**: No user story work starts until this phase is complete.

- [ ] T011 Implement shared contracts and enums aligned to updated spec in src/CloudImaging.Contracts/Models/DeviceSession.cs, src/CloudImaging.Contracts/Models/ImagingStep.cs, src/CloudImaging.Contracts/Models/OSImage.cs, src/CloudImaging.Contracts/Models/BootImage.cs, src/CloudImaging.Contracts/Models/BrandingConfiguration.cs
- [ ] T012 [P] Implement DeviceSession passcode hashing and consume metadata in src/CloudImaging.ImagingCoreApi/Domain/PasscodeSecurityPolicy.cs and src/CloudImaging.ImagingCoreApi/Domain/DeviceSessionFactory.cs
- [ ] T013 [P] Implement Table Storage repositories for core entities in src/CloudImaging.ImagingCoreApi/Repositories/DeviceSessionRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/ImagingStepRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/OSImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BootImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BrandingRepository.cs
- [ ] T014 [P] Implement Device Gateway API session token issuance/validation middleware in src/CloudImaging.DeviceGatewayApi/Security/DeviceSessionTokenService.cs and src/CloudImaging.DeviceGatewayApi/Middleware/DeviceSessionTokenValidationMiddleware.cs
- [ ] T015 [P] Implement Operator API Entra ID auth and app-role authorization middleware in src/CloudImaging.OperatorApi/Middleware/EntraAuthMiddleware.cs and src/CloudImaging.OperatorApi/Middleware/AppRoleAuthorizationMiddleware.cs
- [ ] T016 [P] Implement service-to-service client stack (gateway/operator to core) in src/CloudImaging.DeviceGatewayApi/Services/ImagingCoreClient.cs and src/CloudImaging.OperatorApi/Services/ImagingCoreClient.cs
- [ ] T017 [P] Implement ProblemDetails middleware across APIs in src/CloudImaging.DeviceGatewayApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.OperatorApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.ImagingCoreApi/Middleware/ProblemDetailsMiddleware.cs
- [ ] T018 [P] Implement portal backend Entra token validation middleware in src/cloud-imaging-portal/server/src/middleware/auth.ts
- [ ] T019 [P] Configure OpenAPI export for Device Gateway API and Operator API in src/CloudImaging.DeviceGatewayApi/OpenApi/OpenApiConfig.cs and src/CloudImaging.OperatorApi/OpenApi/OpenApiConfig.cs
- [ ] T020 [P] Implement OpenAPI type generation scripts for portal in src/deploy/scripts/generate-types.ps1 and src/cloud-imaging-portal/server/package.json
- [ ] T021 [P] Add baseline Bicep modules for networking, identities, and compute in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/networking.bicep
- [ ] T022 [P] Add Bicep module for private Imaging Core API and Private Link in src/deploy/bicep/modules/imaging-core-api.bicep
- [ ] T023 [P] Add Bicep modules for Device Gateway API and Operator API in src/deploy/bicep/modules/device-gateway-api.bicep and src/deploy/bicep/modules/operator-api.bicep
- [ ] T024 [P] Add Bicep modules for portal client/server resources in src/deploy/bicep/modules/cloud-imaging-portal.bicep
- [ ] T025 [P] Expand CI scaffold to full matrix execution and required quality gates in .github/workflows/ci.yml
- [ ] T026 Align canonical contract documents to current architecture names and endpoint scope in specs/001-cloud-windows-imaging/contracts/device-gateway-api.md, specs/001-cloud-windows-imaging/contracts/imaging-core-api.md, specs/001-cloud-windows-imaging/contracts/operator-api.md, and specs/001-cloud-windows-imaging/contracts/cloud-imaging-portal-api.md

**Checkpoint**: Foundation complete; user story phases may begin.

---

## Phase 3: User Story 1 - Device Boot and Session Initiation (Priority: P1) 🎯 MVP

**Goal**: Cloud Imaging Client starts in WinPE, creates session through Device Gateway API, receives one-time pairing passcode and device-session token.

**Independent Test**: Boot client in WinPE, register a new session, display passcode within 10 seconds, and verify session record exists with `SessionInit`/`SessionAllowed` path and no portal interaction.

### Tests for User Story 1

- [ ] T027 [P] [US1] Add Device Gateway API contract test for POST /sessions in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CreateSessionContractTests.cs
- [ ] T028 [P] [US1] Add Imaging Core API integration test for server-generated passcode hash-at-rest behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/CreateSessionSecurityIntegrationTests.cs
- [ ] T029 [P] [US1] Add Cloud Imaging Client startup/session registration tests in tests/CloudImaging.Client.Tests/SessionRegistrationViewModelTests.cs

### Implementation for User Story 1

- [ ] T030 [US1] Implement Imaging Core API create-session endpoint with one-time passcode generation and hash persistence in src/CloudImaging.ImagingCoreApi/Functions/CreateSessionFunction.cs
- [ ] T031 [US1] Implement Device Gateway API POST /sessions endpoint with device-session token issuance in src/CloudImaging.DeviceGatewayApi/Functions/CreateSessionFunction.cs
- [ ] T032 [US1] Implement Cloud Imaging Client startup registration flow in src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs and src/CloudImaging.Client/ViewModels/StartupViewModel.cs
- [ ] T033 [US1] Implement Cloud Imaging Client waiting screen with passcode display in src/CloudImaging.Client/Views/SessionWaitingView.xaml and src/CloudImaging.Client/ViewModels/SessionWaitingViewModel.cs
- [ ] T034 [US1] Implement startup error handling and retry UX in src/CloudImaging.Client/Services/SessionStartupCoordinator.cs

**Checkpoint**: US1 independently functional.

---

## Phase 4: User Story 2 - Technician Couples Device and Assigns Image (Priority: P2)

**Goal**: Authenticated Cloud Imaging Portal couples a waiting device by passcode and assigns OS image through Operator API.

**Independent Test**: With a waiting session created from US1, couple by passcode in portal and verify client transitions to `SessionAssigned` and retrieves assignment details.

### Tests for User Story 2

- [ ] T035 [P] [US2] Add Operator API contract test for couple operation in tests/CloudImaging.OperatorApi.Tests/Contracts/CoupleSessionContractTests.cs
- [ ] T036 [P] [US2] Add Imaging Core API integration test for passcode consume-on-success and conflict handling in tests/CloudImaging.ImagingCoreApi.Tests/Integration/PasscodeConsumeConflictIntegrationTests.cs
- [ ] T037 [P] [US2] Add portal backend integration test for couple endpoint in tests/cloud-imaging-portal/server/session-couple.test.ts
- [ ] T038 [P] [US2] Add portal frontend integration test for passcode coupling flow in tests/cloud-imaging-portal/client/session-couple-flow.test.tsx

### Implementation for User Story 2

- [ ] T039 [US2] Implement Imaging Core API couple-and-assign endpoint in src/CloudImaging.ImagingCoreApi/Functions/CoupleSessionFunction.cs
- [ ] T040 [US2] Implement Operator API couple endpoint and role checks in src/CloudImaging.OperatorApi/Functions/CoupleSessionFunction.cs
- [ ] T041 [US2] Implement portal backend couple route proxy to Operator API in src/cloud-imaging-portal/server/src/routes/sessions.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [ ] T042 [US2] Implement portal passcode + image assignment UI in src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx and src/cloud-imaging-portal/client/src/components/CoupleSessionDialog.tsx
- [ ] T043 [US2] Implement Device Gateway API status polling response mapping for assigned sessions in src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs
- [ ] T044 [US2] Implement Cloud Imaging Client assignment poller transition logic in src/CloudImaging.Client/Services/SessionStatusPoller.cs

**Checkpoint**: US2 independently functional.

---

## Phase 5: User Story 3 - Cloud Imaging Client Downloads and Applies Windows Image (Priority: P3)

**Goal**: Client downloads and applies image using SAS tokens, reports step-level progress, refreshes SAS when needed, and keeps UI responsive.

**Independent Test**: Simulate assigned session and validate complete step pipeline (download/apply/report/complete) with refresh and failure handling.

### Tests for User Story 3

- [ ] T045 [P] [US3] Add Device Gateway API contract test for progress relay endpoint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/ReportProgressContractTests.cs
- [ ] T046 [P] [US3] Add Imaging Core API integration test for lifecycle transitions and heartbeat timeout policy in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs
- [ ] T047 [P] [US3] Add Cloud Imaging Client workflow tests for background download/apply and no UI blocking in tests/CloudImaging.Client.Tests/ImagingWorkflowViewModelTests.cs
- [ ] T047a [P] [US3] Add WinPE UI thread blocking detection test using performance counters and async verification in tests/CloudImaging.Client.Tests/UiThreadResponsivenessTests.cs
- [ ] T047b [P] [US3] Add Device Gateway API contract test for cache validation endpoint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CacheValidationContractTests.cs
- [ ] T047c [P] [US3] Add Cloud Imaging Client cache hit/miss workflow tests with hash validation in tests/CloudImaging.Client.Tests/ImageCacheValidationTests.cs
- [ ] T047d [P] [US3] Add Imaging Core API integration test for overall imaging completion percentage persistence and retrieval in tests/CloudImaging.ImagingCoreApi.Tests/Integration/OverallProgressIntegrationTests.cs

### Implementation for User Story 3

- [ ] T048 [US3] Implement Imaging Core API progress endpoint, state transitions, and overall imaging completion percentage calculation in src/CloudImaging.ImagingCoreApi/Functions/ReportProgressFunction.cs and src/CloudImaging.ImagingCoreApi/Services/OverallProgressCalculator.cs
- [ ] T049 [US3] Implement Imaging Core API SAS refresh endpoint with 15-minute threshold support in src/CloudImaging.ImagingCoreApi/Functions/RefreshSasTokenFunction.cs
- [ ] T050 [US3] Implement Device Gateway API progress relay endpoint and include overall imaging completion percentage in status responses in src/CloudImaging.DeviceGatewayApi/Functions/ReportProgressFunction.cs and src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs
- [ ] T051 [US3] Implement Device Gateway API SAS refresh endpoint in src/CloudImaging.DeviceGatewayApi/Functions/RefreshSasTokenFunction.cs
- [ ] T052 [US3] Implement Cloud Imaging Client image download service (SAS for blob download only) in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [ ] T053 [US3] Implement Cloud Imaging Client image apply orchestration in src/CloudImaging.Client/Services/ImageApplyService.cs
- [ ] T054 [US3] Implement Cloud Imaging Client per-step progress reporter and overall imaging completion percentage publisher in src/CloudImaging.Client/Services/ImagingProgressReporter.cs
- [ ] T055 [US3] Implement Cloud Imaging Client SAS refresh coordinator in src/CloudImaging.Client/Services/SasRefreshCoordinator.cs
- [ ] T056 [US3] Implement Cloud Imaging Client failure/retry UX with support codes in src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs
- [ ] T056a [US3] Implement Cloud Imaging Client OS image cache storage service with SHA256 hash computation and cache metadata persistence in src/CloudImaging.Client/Services/ImageCacheService.cs
- [ ] T056b [US3] Implement Device Gateway API cache validation endpoint (POST /api/sessions/{sessionId}/cache/validate) in src/CloudImaging.DeviceGatewayApi/Functions/CacheValidationFunction.cs
- [ ] T056c [US3] Implement Cloud Imaging Client cache-hit pre-download logic: hash validation, skip-download on match, and cache invalidation on mismatch in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [ ] T056d [US3] Implement Cloud Imaging Client cache eviction and cleanup: LRU eviction on space pressure, 30-day expiry auto-purge, and orphaned entry removal in src/CloudImaging.Client/Services/ImageCacheMaintenanceService.cs

**Checkpoint**: US3 independently functional.

---

## Phase 6: User Story 7 - Cloud Imaging Media Builder for WinPE Boot (Priority: P3)

**Goal**: Provide Media Builder app workflows for boot image generation and Entra-authenticated USB preparation/download/deploy.

**Independent Test**: Generate boot image locally with signed manifest, then use Entra-signed-in USB workflow to retrieve boot image metadata/SAS through Operator API, prepare USB, and validate auto-start boot behavior.

### Tests for User Story 7

- [ ] T057 [P] [US7] Add Media Builder boot image generation tests in tests/CloudImaging.MediaBuilder.Tests/BootImageGenerationTests.cs
- [ ] T058 [P] [US7] Add Media Builder signing/verification tests in tests/CloudImaging.MediaBuilder.Tests/BootImageSigningTests.cs
- [ ] T059 [P] [US7] Add Media Builder Entra sign-in tests in tests/CloudImaging.MediaBuilder.Tests/EntraSignInTests.cs
- [ ] T060 [P] [US7] Add Operator API contract tests for boot image list and SAS issuance in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageApiContractTests.cs
- [ ] T113 [P] [US7] Add Operator API contract tests for portal-role boot image lifecycle CRUD endpoints in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageLifecycleContractTests.cs
- [ ] T114 [P] [US7] Add Imaging Core API integration tests for boot image lifecycle CRUD and role-bound behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootImageLifecycleIntegrationTests.cs
- [ ] T061 [P] [US7] Add Media Builder USB disk safety validation tests in tests/CloudImaging.MediaBuilder.Tests/UsbDiskValidationTests.cs
- [ ] T062 [P] [US7] Add Media Builder partition layout and deployment tests in tests/CloudImaging.MediaBuilder.Tests/UsbProvisioningWorkflowTests.cs

### Implementation for User Story 7

- [ ] T063 [US7] Implement Media Builder Entra auth orchestration in src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs
- [ ] T064 [US7] Implement Media Builder boot image generation workflow and manifest creation in src/CloudImaging.MediaBuilder/Services/BootImageGenerationService.cs and src/CloudImaging.MediaBuilder/ViewModels/GenerateBootImageViewModel.cs
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
- [ ] T070 [US7] Implement Media Builder removable disk safety and two-partition provisioning in src/CloudImaging.MediaBuilder/Services/UsbSafetyValidationService.cs and src/CloudImaging.MediaBuilder/Services/UsbPartitionProvisioningService.cs
- [ ] T071 [US7] Implement Media Builder deployment, auto-start configuration, and preparation manifest in src/CloudImaging.MediaBuilder/Services/BootImageDeploymentService.cs and src/CloudImaging.MediaBuilder/ViewModels/PrepareUsbStorageViewModel.cs
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
- [ ] T078 [US4] Implement portal bulk assign UI controls in src/cloud-imaging-portal/client/src/components/BulkAssignPanel.tsx
- [ ] T079 [US4] Implement portal multi-session progress table with current step, per-step status, and overall imaging completion percentage display in src/cloud-imaging-portal/client/src/components/SessionProgressTable.tsx

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

- [ ] T089 [P] [US6] Add Operator API branding endpoint contract tests in tests/CloudImaging.OperatorApi.Tests/Contracts/BrandingContractTests.cs
- [ ] T090 [P] [US6] Add portal backend branding route tests in tests/cloud-imaging-portal/server/branding.test.ts
- [ ] T091 [P] [US6] Add portal frontend branding flow tests in tests/cloud-imaging-portal/client/branding-settings-flow.test.tsx

### Implementation for User Story 6

- [ ] T092 [US6] Implement Imaging Core API branding get/put endpoints in src/CloudImaging.ImagingCoreApi/Functions/BrandingFunctions.cs
- [ ] T093 [US6] Implement Operator API branding proxy endpoints in src/CloudImaging.OperatorApi/Functions/BrandingFunctions.cs
- [ ] T094 [US6] Implement portal backend branding routes in src/cloud-imaging-portal/server/src/routes/branding.ts
- [ ] T095 [US6] Implement portal branding settings page and logo upload flow in src/cloud-imaging-portal/client/src/pages/BrandingSettingsPage.tsx
- [ ] T096 [US6] Implement dynamic CSS variable application for runtime branding in src/cloud-imaging-portal/client/src/context/brandingContext.tsx and src/cloud-imaging-portal/client/src/index.css

**Checkpoint**: US6 independently functional.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Hardening, performance, accessibility, deployment packaging, and final validation.

- [ ] T097 [P] Finalize Bicep deployment graph and module wiring in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/*.bicep
- [ ] T098 [P] Implement deployment/validation scripts for self-hosting in src/deploy/scripts/deploy.ps1 and src/deploy/scripts/validate.ps1
- [ ] T099 [P] Implement CI quality gates (build/test/lint/perf/accessibility) in .github/workflows/ci.yml
- [ ] T100 [P] Add portal WCAG 2.1 AA automated accessibility tests in tests/cloud-imaging-portal/client/accessibility/accessibility-a11y.test.tsx
- [ ] T101 [P] Add performance test for 50 concurrent imaging sessions (SC-003) in tests/performance/session-concurrency.k6.js
- [ ] T102 [P] Add performance benchmark for OS image CRUD with 500-item catalog (SC-008) in tests/performance/image-crud-latency.k6.js
- [ ] T103 [P] Add performance test for Operator API boot image query/SAS generation (SC-015) in tests/performance/operator-api-boot-image-perf.k6.js
- [ ] T129 [P] Add performance test for bulk assignment initiation latency for 20+ devices (SC-005) in tests/performance/bulk-assign-latency.k6.js
- [ ] T104 [P] Add benchmark for boot image generation duration (SC-012) in tests/performance/media-builder-boot-generation-perf.ps1
- [ ] T105 [P] Add benchmark for USB preparation duration (SC-013) in tests/performance/media-builder-usb-prep-perf.ps1
- [ ] T106 [P] Add validation runbook for 20-device auto-launch matrix (SC-014) in docs/validation-usb-autostart-matrix.md
- [ ] T107 [P] Create deployment package specification document (FR-045) in specs/001-cloud-windows-imaging/deployment-package.md
- [ ] T108 [P] Update self-hosting guide with environment prerequisites and auth setup in docs/self-hosting-guide.md
- [ ] T109 [P] Update operations runbook with troubleshooting for session/token/SAS flows in docs/operations-runbook.md
- [ ] T110 Execute quickstart validation scenarios and capture results in specs/001-cloud-windows-imaging/quickstart.md
- [ ] T111 [P] Add Media Builder resumable download and interrupted-transfer recovery coverage in tests/CloudImaging.MediaBuilder.Tests/BootImageDeploymentTests.cs and src/CloudImaging.MediaBuilder/Services/BootImageDownloadService.cs
- [ ] T112 [P] Add session inactivity expiry, heartbeat failure, and terminal purge coverage in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs and src/CloudImaging.ImagingCoreApi/Services/DeviceSessionLifecycleService.cs
- [ ] T118 [P] Add timed clean-tenant deployment rehearsal validation for <= 120 minute target (SC-009) in tests/validation/deployment-timing-validation.ps1 and docs/validation-deployment-timing.md
- [ ] T119 [P] Add config-only environment promotion validation with artifact and code-diff checks (SC-010) ensuring dev->test->prod transition requires only parameter/environment changes in src/deploy/parameters/*.json, endpoint URLs, and secrets—no source code differences in tests/validation/promotion-config-only-validation.ps1 and docs/validation-promotion-config-only.md
- [ ] T120 [P] Add documentation-only WinPE package reproducibility validation (SC-011) in tests/validation/winpe-package-repro-validation.ps1 and docs/validation-winpe-package-repro.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup completion; blocks all stories.
- **User Stories (Phase 3-9)**: Depend on Foundational completion.
- **Polish (Phase 10)**: Depends on desired user stories being complete.

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
T027  T028  T029

# Implementation sequence
T030 -> T031 -> T032 -> T033 -> T034
```

## Parallel Example: User Story 7

```bash
# Parallel tests
T057  T058  T059  T060  T061  T062

# Generation pipeline
T063 -> T064 -> T065

# Prepare USB pipeline
T066 -> T067 -> T068 -> T069 -> T070 -> T071

# Boot image lifecycle pipeline (FR-063)
T115 -> T116 -> T117
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

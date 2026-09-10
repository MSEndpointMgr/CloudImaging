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

- [x] T001 Create monorepo folder structure in src/CloudImaging.Contracts/, src/CloudImaging.DeviceGatewayApi/, src/CloudImaging.OperatorApi/, src/CloudImaging.ImagingCoreApi/, src/CloudImaging.Client/, src/CloudImaging.MediaBuilder/, src/cloud-imaging-portal/client/, src/cloud-imaging-portal/server/, src/deploy/bicep/, src/deploy/parameters/, src/deploy/scripts/, tests/, docs/
- [x] T002 Create .NET solution and add projects in CloudImaging.sln for CloudImaging.Contracts, CloudImaging.DeviceGatewayApi, CloudImaging.OperatorApi, CloudImaging.ImagingCoreApi, CloudImaging.Client, and CloudImaging.MediaBuilder
- [x] T003 Configure solution-wide diagnostics, nullable settings, and warning-as-error policy in Directory.Build.props
- [x] T004 [P] Create package manifests and scripts for the portal workspace in src/cloud-imaging-portal/client/package.json and src/cloud-imaging-portal/server/package.json
- [x] T005 [P] Configure TypeScript strict mode and path aliases in src/cloud-imaging-portal/client/tsconfig.json and src/cloud-imaging-portal/server/tsconfig.json
- [x] T006 [P] Configure lint and format rules for portal code in src/cloud-imaging-portal/client/eslint.config.js and src/cloud-imaging-portal/server/eslint.config.js
- [x] T007 [P] Configure frontend build baseline with Vite and Tailwind in src/cloud-imaging-portal/client/vite.config.ts and src/cloud-imaging-portal/client/tailwind.config.ts
- [x] T008 [P] Add environment templates for self-hosting in src/cloud-imaging-portal/client/.env.example and src/cloud-imaging-portal/server/.env.example
- [x] T009 Create self-hosting deployment parameter templates in src/deploy/parameters/dev.parameters.json, src/deploy/parameters/test.parameters.json, and src/deploy/parameters/prod.parameters.json
- [x] T010 [P] Create initial CI workflow scaffold (triggers, base jobs, artifact layout) in .github/workflows/ci.yml; also create stub files for the core dev team's Azure deployment workflows (.github/workflows/deploy-dev.yml with workflow_dispatch trigger and placeholder IaC and component deploy jobs, .github/workflows/deploy-components.yml with workflow_dispatch trigger and component name input and placeholder per-component deploy job); full implementation of both deployment workflows follows in T158

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core contracts, security model, API foundations, and deployment baseline.

**CRITICAL**: No user story work starts until this phase is complete.

- [x] T011 Implement shared contracts and enums aligned to updated spec in src/CloudImaging.Contracts/Models/DeviceSession.cs (include deviceSerialNumber, deviceManufacturer, deviceModel, preFlightAuthorizationResult fields), src/CloudImaging.Contracts/Models/DeviceRegistrationPayload.cs (include serialNumber, manufacturer, model fields), src/CloudImaging.Contracts/Models/ImagingStep.cs, src/CloudImaging.Contracts/Models/OSImage.cs, src/CloudImaging.Contracts/Models/BootImage.cs, src/CloudImaging.Contracts/Models/BrandingConfiguration.cs, src/CloudImaging.Contracts/Models/PortalConfiguration.cs, src/CloudImaging.Contracts/Models/SupportReferenceCode.cs (value object with ComponentCode string, SessionRef string, StageCode string, EpochSeconds long, and a ToString() method rendering the {ComponentCode}-{SessionRef}-{StageCode}-{EpochSeconds} format)
- [x] T012 [P] Implement DeviceSession passcode hashing and consume metadata in src/CloudImaging.ImagingCoreApi/Domain/PasscodeSecurityPolicy.cs and src/CloudImaging.ImagingCoreApi/Domain/DeviceSessionFactory.cs
- [x] T013 [P] Implement Table Storage repositories for core entities in src/CloudImaging.ImagingCoreApi/Repositories/DeviceSessionRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/ImagingStepRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/OSImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BootImageRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/BrandingRepository.cs
- [x] T014 [P] Implement Device Gateway API session token issuance/validation middleware in src/CloudImaging.DeviceGatewayApi/Security/DeviceSessionTokenService.cs and src/CloudImaging.DeviceGatewayApi/Middleware/DeviceSessionTokenValidationMiddleware.cs
- [x] T014a [P] Implement Device Gateway API per-session rate limiting middleware: sliding-window counter keyed by device-session token, 10 calls per 30 seconds, returns HTTP 429 with Retry-After header on excess; public session bootstrap endpoint is exempt from rate limiting (FR-018) in src/CloudImaging.DeviceGatewayApi/Middleware/RateLimitingMiddleware.cs
- [x] T015 [P] Implement Operator API Entra ID auth and app-role authorization middleware in src/CloudImaging.OperatorApi/Middleware/EntraAuthMiddleware.cs and src/CloudImaging.OperatorApi/Middleware/AppRoleAuthorizationMiddleware.cs
- [x] T016 [P] Implement service-to-service client stack (gateway/operator to core) in src/CloudImaging.DeviceGatewayApi/Services/ImagingCoreClient.cs and src/CloudImaging.OperatorApi/Services/ImagingCoreClient.cs
- [x] T017 [P] Implement ProblemDetails middleware across APIs in src/CloudImaging.DeviceGatewayApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.OperatorApi/Middleware/ProblemDetailsMiddleware.cs, src/CloudImaging.ImagingCoreApi/Middleware/ProblemDetailsMiddleware.cs
- [x] T018 [P] Implement portal backend Entra token validation middleware in src/cloud-imaging-portal/server/src/middleware/auth.ts
- [x] T018a [P] Add portal frontend auth guard contract test: unauthenticated navigation redirects to sign-in, authenticated navigation renders protected content, and MsalProvider is present in the component tree (FR-030) in tests/cloud-imaging-portal/client/auth-guard.test.tsx
- [x] T018b [P] Implement portal frontend Entra ID auth setup: configure @azure/msal-react MsalProvider with deployment-injected client ID and tenant ID, implement auth context with useAuth hook, and wrap all portal routes in a protected-route guard that redirects unauthenticated users to sign-in (FR-030) in src/cloud-imaging-portal/client/src/main.tsx, src/cloud-imaging-portal/client/src/context/authContext.tsx, and src/cloud-imaging-portal/client/src/components/ProtectedRoute.tsx
- [x] T018c [P] Add portal backend role enforcement integration tests: CloudImaging.Technician role is denied administrator-only operations (OS image upload, boot image lifecycle management, branding configuration, deployment configuration changes including pre-flight auth toggle), CloudImaging.Administrator role is permitted all operations, and requests with a missing or invalid roles claim are rejected with HTTP 403 (FR-040, FR-040a) in tests/cloud-imaging-portal/server/role-enforcement.test.ts
- [x] T018d [P] Implement portal backend user-level role authorization middleware: extract CloudImaging.Administrator/CloudImaging.Technician roles from the validated JWT roles claim and enforce per-route access restrictions per FR-040a (Technician permitted: view sessions, couple by passcode, assign OS images from catalog, initiate bulk assignment, monitor progress, read OS image catalog; Administrator required for: OS image upload/edit/delete, boot image lifecycle management, branding configuration, deployment configuration including pre-flight auth toggle), return HTTP 403 on role violation; apply after Entra token validation middleware (T018) on all authenticated portal backend routes (FR-040, FR-040a) in src/cloud-imaging-portal/server/src/middleware/roleGuard.ts
- [x] T018e Implement portal app shell and sidebar navigation: fixed left sidebar with five labeled navigation items in order -- Sessions (default landing route), OS Images, Boot Images, Branding, and Configuration; react-router-dom v7 route definitions for all five sections; navigation replaces main content area without a full page reload; sidebar present on all authenticated routes; depends on T018b ProtectedRoute (FR-040) in src/cloud-imaging-portal/client/src/components/AppShell.tsx, src/cloud-imaging-portal/client/src/components/Sidebar.tsx, and src/cloud-imaging-portal/client/src/App.tsx
- [x] T019 [P] Configure OpenAPI export for Device Gateway API and Operator API in src/CloudImaging.DeviceGatewayApi/OpenApi/OpenApiConfig.cs and src/CloudImaging.OperatorApi/OpenApi/OpenApiConfig.cs
- [x] T020 [P] Implement OpenAPI type generation scripts for portal in src/deploy/scripts/generate-types.ps1 and src/cloud-imaging-portal/server/package.json
- [x] T021 [P] Add baseline Bicep modules for networking, identities, and compute in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/networking.bicep
- [x] T022 [P] Add Bicep module for private Imaging Core API and Private Link in src/deploy/bicep/modules/imaging-core-api.bicep
- [x] T023 [P] Add Bicep modules for Device Gateway API and Operator API in src/deploy/bicep/modules/device-gateway-api.bicep and src/deploy/bicep/modules/operator-api.bicep
- [x] T024 [P] Add Bicep modules for portal client/server resources in src/deploy/bicep/modules/cloud-imaging-portal.bicep
- [x] T148 [P] Add Bicep resource and post-deploy script to grant DeviceManagementServiceConfig.Read.All application permission to Imaging Core API managed identity in customer tenant in src/deploy/bicep/modules/imaging-core-api.bicep and src/deploy/scripts/grant-graph-permissions.ps1
- [x] T025 [P] Expand CI scaffold to full matrix execution and required quality gates in .github/workflows/ci.yml
- [x] T025a [P] Implement Application Insights SDK instrumentation and structured telemetry in DeviceGatewayApi, OperatorApi, ImagingCoreApi, and portal backend: requests, dependencies, exceptions, and custom trace events; connection string injected via app settings with no hardcoded instrumentation keys (FR-065, FR-067) in src/CloudImaging.DeviceGatewayApi/Program.cs, src/CloudImaging.OperatorApi/Program.cs, src/CloudImaging.ImagingCoreApi/Program.cs, and src/cloud-imaging-portal/server/src/app.ts
- [x] T025b [P] Implement rolling log file output in Cloud Imaging Client and Cloud Imaging Media Builder WPF apps: maximum 10 MB per file, 5 files retained, structured format, no network dependency (FR-066) in src/CloudImaging.Client/Logging/LoggingConfiguration.cs and src/CloudImaging.MediaBuilder/Logging/LoggingConfiguration.cs
- [x] T026 Align canonical contract documents to current architecture names and endpoint scope in specs/001-cloud-windows-imaging/contracts/device-gateway-api.md, specs/001-cloud-windows-imaging/contracts/imaging-core-api.md, specs/001-cloud-windows-imaging/contracts/operator-api.md, and specs/001-cloud-windows-imaging/contracts/cloud-imaging-portal-api.md -- COMPLETED June 24, 2026
- [x] T180 Revise frozen contract documents to add endpoints introduced by FR-068 through FR-071 (added June 26, post-freeze): add GET /api/bootmedia/certificate/pfx (CloudImaging.MediaBuilderAccess role, returns active boot media certificate PFX bytes) to operator-api.md; add POST /api/cert/generate, POST /api/cert/rotate, GET /api/cert/active/metadata (all CloudImaging.Administrator user role, admin-only) to cloud-imaging-portal-api.md; mark the revision with version note per the contract freeze policy ("breaking changes require an explicit versioned revision") in specs/001-cloud-windows-imaging/contracts/operator-api.md and specs/001-cloud-windows-imaging/contracts/cloud-imaging-portal-api.md; complete before T171 and T178 implementation begins (FR-062, FR-068) -- COMPLETED 2026-07-15
- [x] T130 [P] Implement PortalConfiguration Table Storage repository in src/CloudImaging.ImagingCoreApi/Repositories/PortalConfigurationRepository.cs (entity model PortalConfiguration.cs is defined in T011 via src/CloudImaging.Contracts/Models/PortalConfiguration.cs)
- [x] T131 [P] Implement Imaging Core API PortalConfiguration get/put endpoints and Operator API proxy endpoints in src/CloudImaging.ImagingCoreApi/Functions/PortalConfigurationFunctions.cs and src/CloudImaging.OperatorApi/Functions/PortalConfigurationFunctions.cs
- [x] T161a [P] Add unit tests for BootMediaCertificate repository atomic-swap semantics and Key Vault PFX service: generate activates new entity and deactivates all prior active entries in a single operation (no window where zero certs are active), querying active cert after swap returns the new thumbprint, KV get-PFX returns bytes matching stored PFX, KV write failure does not leave a partially-activated certificate in Table Storage; follow Red-Green-Refactor -- these tests MUST fail before T161 implementation begins (FR-068) in tests/CloudImaging.ImagingCoreApi.Tests/Unit/BootMediaCertificateRepositoryTests.cs and tests/CloudImaging.ImagingCoreApi.Tests/Unit/KeyVaultCertificateServiceTests.cs
- [x] T161 [P] Implement BootMediaCertificate Table Storage repository and Azure Key Vault PFX storage service: BootMediaCertificateRepository.cs persists certificate ID, thumbprint (SHA-256), subject, validity period, issued-at, expires-at, issued-by, and status (active/inactive) for the BootMediaCertificate entity; enforces exactly one active certificate at any time via atomic swap on activation; KeyVaultCertificateService.cs stores and retrieves certificate PFX blobs from Key Vault keyed by certificate ID (FR-068) in src/CloudImaging.ImagingCoreApi/Repositories/BootMediaCertificateRepository.cs and src/CloudImaging.ImagingCoreApi/Services/KeyVaultCertificateService.cs
- [x] T162 [P] Extend Device Gateway API and Key Vault Bicep modules: configure clientCertificateMode=require on the Device Gateway API Premium EP1 Function App resource; add Key Vault access policy granting the Imaging Core API managed identity secrets get/set/delete permissions for certificate PFX storage; add optional Application Gateway WAF_v2 resource block with Device Gateway API subnet restriction conditional on the deployApplicationGateway parameter (FR-069, FR-042) in src/deploy/bicep/modules/device-gateway-api.bicep and src/deploy/bicep/modules/key-vault.bicep
- [x] T163a [P] Add unit tests for Device Gateway API BootMediaCertificateThumbprintCache: first call with empty cache reads from Table Storage and stores result, second call within 60 seconds returns cached value without a Table Storage read, call after 60 seconds triggers a refresh read from Table Storage, cache returns the updated thumbprint after a certificate rotation is committed to Table Storage; follow Red-Green-Refactor -- these tests MUST fail before T163 implementation begins (FR-069) in tests/CloudImaging.DeviceGatewayApi.Tests/Unit/BootMediaCertificateThumbprintCacheTests.cs
- [x] T163 [P] Implement Device Gateway API active boot media certificate thumbprint cache: singleton BootMediaCertificateThumbprintCache service that retrieves the active certificate thumbprint from Table Storage and caches it in Function memory; cache MUST be refreshed at an interval of no more than 60 seconds to ensure certificate revocation takes effect promptly without a per-request Table Storage lookup on every inbound request (FR-069) in src/CloudImaging.DeviceGatewayApi/Security/BootMediaCertificateThumbprintCache.cs
- [x] T164 [P] Implement Device Gateway API mTLS certificate validation middleware: extract forwarded client certificate from the X-ARR-ClientCert request header, parse its SHA-256 thumbprint, validate against the active thumbprint from BootMediaCertificateThumbprintCache (T163); reject requests where the thumbprint does not match with HTTP 401; this is a defence-in-depth layer -- the primary enforcement (TLS handshake drop) is handled by the clientCertificateMode=require platform setting configured in T162; middleware is registered on all Function endpoints (FR-069) in src/CloudImaging.DeviceGatewayApi/Middleware/MtlsCertificateValidationMiddleware.cs
- [x] T165 [P] Author uiFormDefinition.json Form View UI definition (schema 2021-09-09/uiFormDefinition.schema.json) for the Azure Template Spec portal wizard: define four tabs -- Basics (subscription and target resource group selectors), Configuration (resourcePrefix text input max 4 alphanumeric characters, environment dropdown dev/prod, Application client ID text input, Directory (tenant) ID auto-populated from the current portal session, Azure region input, deployment tier dropdown Standard/Enterprise mapping to deployApplicationGateway boolean, certValidityPeriodDays integer defaulting to 365, passcode TTL, SAS expiry window, network CIDR inputs), Preview (live resource name output panel that applies the {prefix}-{env}-{type}[-{name}] naming convention for all resources and the {prefix}{env}st{purpose} convention for Storage Accounts using the entered prefix and environment values), and Review + Create; reference the Bicep parameter schema from main.bicep (FR-041, FR-042) in src/deploy/uiFormDefinition.json
- [x] T166 [P] Author publish-template-spec.ps1 PowerShell script: accept -ResourceGroupName, -Location, and -Version parameters (all required); validate all three are provided before executing; call New-AzTemplateSpec -Name "CloudImaging" -ResourceGroupName <rg> -Location <location> -Version <version> -TemplateFile ".\main.bicep" -UIFormDefinitionFile ".\uiFormDefinition.json"; output the portal deployment URL (https://portal.azure.com/#create/Microsoft.TemplateSpec/resourceId/<encoded-spec-version-id>) on successful publish; suitable for inclusion in the community release bundle (FR-041, FR-044a) in src/deploy/scripts/publish-template-spec.ps1
- [x] T167 [P] Add Device Gateway API mTLS validation middleware contract tests: valid active cert thumbprint passes and returns expected response body, mismatched thumbprint returns HTTP 401, request with missing X-ARR-ClientCert header returns HTTP 401, thumbprint cache respects the 60-second maximum refresh interval and reads updated thumbprint after rotation (FR-069) in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/MtlsValidationContractTests.cs

**Checkpoint**: Foundation complete; user story phases may begin.

---

## Phase 3: User Story 1 - Device Boot and Session Initiation (Priority: P1) 🎯 MVP

**Goal**: Cloud Imaging Client starts in WinPE, creates session through Device Gateway API, receives one-time pairing passcode and device-session token.

**Independent Test**: Boot client in WinPE, confirm Imaging operation in OperationSelectionView, register a new session, display passcode within 10 seconds, and verify session record exists with the SessionAllowed path and no portal interaction; also verify that a device not found in Autopilot or Corporate Identifiers transitions immediately to SessionNotAuthorized and the client displays ResultsView Not Authorized without any polling delay.

### Tests for User Story 1

- [x] T027 [P] [US1] Add Device Gateway API contract test for POST /api/v1/sessions in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CreateSessionContractTests.cs
- [x] T027a [P] [US1] Add Device Gateway API contract test for per-session rate limiting: verify HTTP 429 with Retry-After header returned when 10-call-per-30-second window is exceeded on authenticated endpoints, and verify the public session bootstrap endpoint is exempt (FR-018) in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/RateLimitingContractTests.cs
- [x] T028 [P] [US1] Add Imaging Core API integration test for server-generated passcode hash-at-rest behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/CreateSessionSecurityIntegrationTests.cs
- [x] T029 [P] [US1] Add Cloud Imaging Client startup and session registration view model tests, including verification that the registration payload contains device identity fields and hardware metadata (motherboard, BIOS, NIC identifiers, storage layout) collected silently before registration in tests/CloudImaging.Client.Tests/SessionRegistrationViewModelTests.cs
- [x] T029a [P] [US1] Add Cloud Imaging Client unit tests for BrandingLogoService: logo-found path (reads logo asset from executable directory and returns it for display), fallback-to-default path (no logo asset found in executable directory), and executable-relative path resolution (FR-002a) in tests/CloudImaging.Client.Tests/BrandingLogoServiceTests.cs
- [x] T132 [P] [US1] Add Imaging Core API integration tests for device pre-flight authorization: Autopilot V1 Graph match advances to SessionAllowed, Corporate Identifiers Graph match advances to SessionAllowed, no-match transitions immediately to SessionNotAuthorized (terminal state), and disabled-mode direct SessionAllowed transition in tests/CloudImaging.ImagingCoreApi.Tests/Integration/DevicePreFlightAuthorizationIntegrationTests.cs
- [x] T133 [P] [US1] Add Cloud Imaging Client unit tests for ResultsView all three terminal outcomes: Success (SessionCompleted shows confirmation), Failure (SessionFailed shows support reference code and remediation), Not Authorized (SessionNotAuthorized shows device serial number and enrollment guidance with no further polling) in tests/CloudImaging.Client.Tests/ResultsViewTests.cs

### Implementation for User Story 1

- [x] T030 [US1] Implement Imaging Core API create-session endpoint with one-time passcode generation and hash persistence in src/CloudImaging.ImagingCoreApi/Functions/CreateSessionFunction.cs
- [x] T031 [US1] Implement Device Gateway API POST /api/v1/sessions endpoint: accept device registration payload including serialNumber, manufacturer, and model fields, forward device identity fields to Imaging Core API as part of session creation, and return device-session token and one-time passcode in src/CloudImaging.DeviceGatewayApi/Functions/CreateSessionFunction.cs
- [x] T032 [US1] Implement Cloud Imaging Client OperationSelectionView (Imaging/Decommissioning cards, Continue action triggers session registration with silent hardware metadata collection: motherboard, BIOS, NICs, storage layout) in src/CloudImaging.Client/Views/OperationSelectionView.xaml, src/CloudImaging.Client/ViewModels/OperationSelectionViewModel.cs, and src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs
- [x] T032a [P] [US1] Implement Cloud Imaging Client branding logo loader service: reads logo asset from executable directory (embedded in boot image WIM by the Generate Boot Image workflow) at startup and provides it to the application header; falls back to MSEndpointMgr default logo if no asset found in executable directory (FR-002a) in src/CloudImaging.Client/Services/BrandingLogoService.cs
- [x] T032b [P] [US1] Implement Cloud Imaging Client WPF window fixed-layout baseline: 1024x768 minimum window size, centered both axes on display at startup, no-scroll constraint enforced across all four views in src/CloudImaging.Client/MainWindow.xaml
- [x] T033 [US1] Implement Cloud Imaging Client SessionInitView: passcode and session ID display while awaiting operator coupling; transitions immediately to ResultsView on SessionNotAuthorized response in src/CloudImaging.Client/Views/SessionInitView.xaml and src/CloudImaging.Client/ViewModels/SessionInitViewModel.cs
- [x] T034 [US1] Implement startup error handling and retry UX in src/CloudImaging.Client/Services/SessionStartupCoordinator.cs
- [x] T134 [US1] Implement Imaging Core API device pre-flight authorization service with parallel Microsoft Graph queries against Autopilot V1 (windowsAutopilotDeviceIdentities filtered by serialNumber) and Intune Corporate Identifiers (importedDeviceIdentities filtered by manufacturer,model,serialNumber) in src/CloudImaging.ImagingCoreApi/Services/DevicePreFlightAuthorizationService.cs
- [x] T135 [US1] Implement Cloud Imaging Client ResultsView displaying all three terminal outcomes: Success (SessionCompleted), Failure (SessionFailed with support reference code, error detail, and remediation with retry option), and Not Authorized (SessionNotAuthorized with device serial number and enrollment guidance; immediate transition from SessionInitView, no further polling) in src/CloudImaging.Client/Views/ResultsView.xaml and src/CloudImaging.Client/ViewModels/ResultsViewModel.cs
- [x] T168 [P] [US1] Add Cloud Imaging Client unit tests for boot media certificate PFX loading: PFX found in executable directory successfully configures HttpClientHandler.ClientCertificates, PFX not found at expected path transitions to cert-missing error state on SessionInitView displaying regenerate-boot-image instruction, TLS handshake failure (SSL/TLS exception on connect) transitions to cert-error state on SessionInitView, HTTP 401 cert-mismatch response from Device Gateway API also transitions to cert-error state, and no automatic retry is offered for any cert rejection scenario (FR-071) in tests/CloudImaging.Client.Tests/BootMediaCertificateLoaderTests.cs
- [x] T169 [US1] Implement Cloud Imaging Client boot media certificate loader: load PFX from the same directory as the Cloud Imaging Client executable (embedded in the boot image WIM during the Generate Boot Image workflow) at application startup; configure the loaded certificate on all HTTPS connections to the Device Gateway API via HttpClientHandler.ClientCertificates; on TLS handshake failure due to certificate rejection (SSL/TLS exception) or on receipt of an HTTP 401 cert-mismatch response from the Device Gateway API, transition SessionInitViewModel to a dedicated cert-error state displaying a message that the boot media certificate is no longer valid with clear instruction to regenerate the boot image using the Cloud Imaging Media Builder and re-prepare USB media with the new image; no automatic retry is offered for a certificate rejection error (FR-071) in src/CloudImaging.Client/Services/BootMediaCertificateLoader.cs and src/CloudImaging.Client/ViewModels/SessionInitViewModel.cs

**Checkpoint**: US1 independently functional.

---

## Phase 4: User Story 2 - Technician Couples Device and Assigns Image (Priority: P2)

**Goal**: Authenticated Cloud Imaging Portal couples a waiting device by passcode and assigns OS image through Operator API.

**Independent Test**: With a waiting session created from US1, couple by passcode in portal and verify client transitions to `SessionAssigned` and retrieves assignment details.

### Tests for User Story 2

- [x] T035 [P] [US2] Add Operator API contract test for couple operation in tests/CloudImaging.OperatorApi.Tests/Contracts/CoupleSessionContractTests.cs
- [x] T035a [P] [US2] Add Operator API contract test for single-session image assign endpoint (POST /api/sessions/{sessionId}/assign): verify CloudImaging.PortalAccess role enforcement, session-not-found (404), image-not-found (400), session-not-in-assignable-state (409), and successful assignment response in tests/CloudImaging.OperatorApi.Tests/Contracts/AssignSessionContractTests.cs
- [x] T036 [P] [US2] Add Imaging Core API integration test for passcode consume-on-success and conflict handling in tests/CloudImaging.ImagingCoreApi.Tests/Integration/PasscodeConsumeConflictIntegrationTests.cs
- [x] T036a [P] [US2] Add Imaging Core API integration test for single-session image assign endpoint: SessionAssigned-state session accepts assignment, unknown image ID returns 400, non-Assigned-state session returns 409, and response includes initial SAS token URL with expiry from PortalConfiguration.sasTokenUrlExpiryMinutes in tests/CloudImaging.ImagingCoreApi.Tests/Integration/AssignSessionIntegrationTests.cs
- [x] T037 [P] [US2] Add portal backend integration test for couple endpoint in tests/cloud-imaging-portal/server/session-couple.test.ts
- [x] T037a [P] [US2] Add portal backend integration test for single-session assign route (POST /sessions/:id/assign) in tests/cloud-imaging-portal/server/session-assign.test.ts
- [x] T038 [P] [US2] Add portal frontend integration test for passcode coupling flow in tests/cloud-imaging-portal/client/session-couple-flow.test.tsx
- [x] T038a [P] [US2] Add portal frontend integration test for single-session image assign flow: Assigned-state row shows [Assign Image] button, clicking opens AssignImageDialog, confirming image selection triggers POST /sessions/:id/assign and transitions row to started state in tests/cloud-imaging-portal/client/session-assign-flow.test.tsx

### Implementation for User Story 2

- [x] T039a [US2] Implement Imaging Core API session couple endpoint (POST /api/internal/sessions/couple): verify passcode hash against stored hash, confirm passcode is not expired and not already consumed, transition session from SessionAllowed to SessionAssigned, and invalidate passcode on success; return 409 on already-coupled passcode and 404 on unknown or expired passcode in src/CloudImaging.ImagingCoreApi/Functions/CoupleSessionFunction.cs
- [x] T039b [US2] Implement Imaging Core API single-session image assign endpoint (POST /api/internal/sessions/{sessionId}/assign): validate session is in SessionAssigned state with no prior image assignment, validate the referenced OSImage is active in catalog, persist the image assignment, and issue initial SAS token URL using PortalConfiguration.sasTokenUrlExpiryMinutes (default 240 min) in src/CloudImaging.ImagingCoreApi/Functions/AssignSessionFunction.cs
- [x] T040 [US2] Implement Operator API couple endpoint and role checks in src/CloudImaging.OperatorApi/Functions/CoupleSessionFunction.cs
- [x] T040a [US2] Implement Operator API single-session image assign endpoint (POST /api/sessions/{sessionId}/assign) with CloudImaging.PortalAccess role enforcement and proxy call to Imaging Core API assign endpoint over Private Link in src/CloudImaging.OperatorApi/Functions/AssignSessionFunction.cs
- [x] T041 [US2] Implement portal backend couple route proxy to Operator API in src/cloud-imaging-portal/server/src/routes/sessions.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [x] T041a [US2] Implement portal backend single-session assign route (POST /sessions/:id/assign) proxying to Operator API POST /api/sessions/{sessionId}/assign in src/cloud-imaging-portal/server/src/routes/sessions.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [x] T042 [US2] Implement Cloud Imaging Portal Sessions section UI: (1) session filter tabs (Active default, Completed, Failed, All) with real-time count badges sourced from GET /api/sessions?filter=... (FR-031) in src/cloud-imaging-portal/client/src/components/SessionFilterTabs.tsx; (2) per-row checkboxes, Select All and Deselect All controls, and eligible-count selection footer displaying the count of checked Assigned-state rows regardless of other-state checked rows (FR-035) in src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx; (3) persistent [Couple Device] toolbar button with passcode coupling modal -- inline error on invalid/expired/consumed passcode, modal stays open on failure, closes on success (FR-032) in src/cloud-imaging-portal/client/src/components/CoupleSessionDialog.tsx; (4) [Assign Image] row-level action button visible on Assigned-state rows and reusable searchable OS image selection modal displaying name/version/file size, shared between single-session and bulk assignment paths (FR-033) in src/cloud-imaging-portal/client/src/components/AssignImageDialog.tsx; (5) implement the FR-031 session table polling model: auto-poll every 5 seconds when any session is in SessionStarted or SessionInProgress state, every 30 seconds otherwise; add a manual Refresh button always visible in the toolbar that triggers an immediate GET /api/sessions call; do NOT use WebSocket or SSE (FR-031) in src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx
- [x] T043 [US2] Implement Device Gateway API status polling response mapping for assigned sessions; establish response contract to include both currentStep (active ImagingStep name, null until imaging begins) and overallProgressPercent per plan.md constraint in src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs
- [x] T044 [US2] Implement Cloud Imaging Client assignment poller transition logic in src/CloudImaging.Client/Services/SessionStatusPoller.cs
- [x] T026a Validate SC-017 walking skeleton milestone: with all six components deployed to the shared dev Azure subscription, smoke-test the minimum end-to-end path -- Cloud Imaging Client registers a session through Device Gateway API and Imaging Core API, an operator couples and assigns via Cloud Imaging Portal through Operator API and Imaging Core API, and the Cloud Imaging Client receives the assignment and displays a terminal ResultsView; document results in specs/001-cloud-windows-imaging/validation-walking-skeleton.md; this milestone gates the start of full-feature iteration on all components (SC-017)

**Checkpoint**: US2 independently functional.

---

## Phase 5: User Story 3 - Cloud Imaging Client Downloads and Applies Windows Image (Priority: P3)

**Goal**: Client downloads and applies image using SAS token URLs, reports step-level progress, refreshes SAS token URLs when needed, and keeps UI responsive.

**Independent Test**: Simulate assigned session and validate complete step pipeline (download/apply/report/complete) with refresh and failure handling.

### Tests for User Story 3

- [x] T045 [P] [US3] Add Device Gateway API contract test for progress relay endpoint: verify status responses include both currentStep (active ImagingStep name) and overallProgressPercent fields per plan.md constraint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/ReportProgressContractTests.cs
- [x] T046 [P] [US3] Add Imaging Core API integration test for lifecycle transitions and heartbeat timeout policy in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs
- [x] T047 [P] [US3] Add Cloud Imaging Client workflow tests for background download/apply and no UI blocking in tests/CloudImaging.Client.Tests/ImagingWorkflowViewModelTests.cs
- [x] T047a [P] [US3] Add WinPE UI thread blocking detection test using performance counters and async verification; also assert that the MainWindow meets the 1024x768 minimum dimensions and centered-window layout constraint (FR-002b) at startup in tests/CloudImaging.Client.Tests/UiThreadResponsivenessTests.cs
- [x] T047b [P] [US3] Add Device Gateway API contract test for cache validation endpoint in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/CacheValidationContractTests.cs
- [x] T047c [P] [US3] Add Cloud Imaging Client cache hit/miss workflow tests with hash validation and cache-skip behavior when insufficient space remains on cache partition after 30-day purge (FR-009d) in tests/CloudImaging.Client.Tests/ImageCacheValidationTests.cs
- [x] T047d [P] [US3] Add Imaging Core API integration test for overall imaging completion percentage persistence and retrieval in tests/CloudImaging.ImagingCoreApi.Tests/Integration/OverallProgressIntegrationTests.cs

### Implementation for User Story 3

- [x] T048 [US3] Implement Imaging Core API progress endpoint, state transitions, overall imaging completion percentage calculation, and ensure all session status responses include both currentStep (active ImagingStep name) and overallProgressPercent per plan.md constraint in src/CloudImaging.ImagingCoreApi/Functions/ReportProgressFunction.cs and src/CloudImaging.ImagingCoreApi/Services/OverallProgressCalculator.cs
- [x] T049 [US3] Implement Imaging Core API SAS refresh endpoint with 15-minute threshold support in src/CloudImaging.ImagingCoreApi/Functions/RefreshSasTokenFunction.cs
- [x] T050 [US3] Implement Device Gateway API progress relay endpoint; ensure all session status responses include both currentStep (active ImagingStep name) and overallProgressPercent per plan.md constraint in src/CloudImaging.DeviceGatewayApi/Functions/ReportProgressFunction.cs and src/CloudImaging.DeviceGatewayApi/Functions/GetSessionStatusFunction.cs (see also T043 for the GetSessionStatus response contract baseline established in US2 Phase 4)
- [x] T051 [US3] Implement Device Gateway API SAS refresh endpoint in src/CloudImaging.DeviceGatewayApi/Functions/RefreshSasTokenFunction.cs
- [x] T052 [US3] Implement Cloud Imaging Client image download service (SAS for blob download only) in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [x] T053 [US3] Implement Cloud Imaging Client image apply orchestration: before invoking DISM, compute the SHA256 hash of the locally stored OS image (whether freshly downloaded or from USB cache) and verify it against `assignedImage.sha256Hash` from the Device Gateway API polling response; if hash does not match, transition session to SessionFailed with APL stage code support reference and display tamper/corruption error to technician -- DISM MUST NOT be invoked on hash mismatch; on successful hash verification proceed with DISM apply (FR-006) in src/CloudImaging.Client/Services/ImageApplyService.cs
- [x] T054 [US3] Implement Cloud Imaging Client per-step progress reporter and overall imaging completion percentage publisher in src/CloudImaging.Client/Services/ImagingProgressReporter.cs
- [x] T055 [US3] Implement Cloud Imaging Client SAS refresh coordinator in src/CloudImaging.Client/Services/SasRefreshCoordinator.cs
- [x] T056 [US3] Implement Cloud Imaging Client ProgressView (3-node horizontal step indicator: Format, Download, Apply; visual distinction for completed/active/pending; transitions to ResultsView on Apply complete) and failure/retry UX with support codes in src/CloudImaging.Client/Views/ProgressView.xaml, src/CloudImaging.Client/ViewModels/ProgressViewModel.cs, and src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs
- [x] T056a [US3] Implement Cloud Imaging Client OS image cache storage service with SHA256 hash computation and cache metadata persistence in src/CloudImaging.Client/Services/ImageCacheService.cs
- [x] T056b [US3] Implement Device Gateway API cache validation endpoint (POST /api/v1/sessions/{sessionId}/cache/validate) in src/CloudImaging.DeviceGatewayApi/Functions/CacheValidationFunction.cs
- [x] T056c [US3] Implement Cloud Imaging Client cache-hit pre-download logic: hash validation, skip-download on match, and cache invalidation on mismatch in src/CloudImaging.Client/Services/ImageDownloadService.cs
- [x] T056d [US3] Implement Cloud Imaging Client cache cleanup: 30-day expiry auto-purge on next boot and orphaned entry removal; skip cache write and proceed with direct download when insufficient space remains after purge; no LRU eviction of valid cache entries (FR-009d) in src/CloudImaging.Client/Services/ImageCacheMaintenanceService.cs

**Checkpoint**: US3 independently functional.

---

## Phase 6: User Story 7 - Cloud Imaging Media Builder for WinPE Boot (Priority: P3)

**Goal**: Provide Media Builder app workflows for boot image generation and Entra-authenticated USB preparation/download/deploy.

**Independent Test**: Generate boot image locally with an embedded integrity/provenance manifest, then use Entra-signed-in USB workflow to retrieve boot image metadata/SAS through Operator API, prepare USB, and validate auto-start boot behavior.

### Tests for User Story 7

- [x] T057 [P] [US7] Add Media Builder boot image generation tests in tests/CloudImaging.MediaBuilder.Tests/BootImageGenerationTests.cs
- [x] T152 [P] [US7] Add Media Builder GenerateBootImageView source selection tests: GitHub releases API resolves latest tag and downloads Client binaries with real-time progress, custom local path validation accepts pre-downloaded binaries, and output folder path confirmed before generation starts (FR-051a) in tests/CloudImaging.MediaBuilder.Tests/GenerateBootImageSourceSelectionTests.cs
- [x] T154 [P] [US7] Add Media Builder GenerateBootImageView completion notification tests: output folder path displayed on success, next-step portal upload instructions shown, no portal upload initiated by the app (FR-051b) in tests/CloudImaging.MediaBuilder.Tests/GenerateBootImageCompletionTests.cs
- [x] T058 [P] [US7] Add Media Builder boot image manifest tests: manifest is built with image version/timestamp/component checksums (Cloud Imaging Client, branding logo, boot media certificate when present)/deployment metadata and embedded at `ci-manifest.json` in the mounted WIM root; no cryptographic signing occurs (FR-051 Option B: descoped, no genuine threat model beyond the independent SHA256 check already required at download time, FR-056) in tests/CloudImaging.MediaBuilder.Tests/BootImageManifestTests.cs
- [x] T059 [P] [US7] Add Media Builder Entra sign-in tests in tests/CloudImaging.MediaBuilder.Tests/EntraSignInTests.cs
- [x] T150 [P] [US7] Add Media Builder ADK prerequisite detection and cert-existence check tests: (1) ADK and WinPE add-on installed → both workflow cards enabled; (2) ADK absent → both cards disabled with installation guidance; (3) ADK present and `GET /api/bootmedia/certificate/metadata` returns 200 → Generate Boot Image card enabled; (4) ADK present and cert endpoint returns HTTP 404 → Generate Boot Image card disabled with cert-required banner; (5) ADK absent and cert absent → both cards disabled for their respective reasons simultaneously (FR-050a) in tests/CloudImaging.MediaBuilder.Tests/AdkPrerequisiteDetectionTests.cs
- [x] T060 [P] [US7] Add Operator API contract tests for boot image list and SAS token URL issuance: verify `GET /api/boot-images` response includes `sha256Hash` and `isLatestPublished` fields for each entry; verify exactly one entry has `isLatestPublished=true`; verify `POST /api/boot-images/{bootImageId}/sas` response includes `downloadUrl`, `expiresAt`, and `sha256Hash`; verify `CloudImaging.MediaBuilderAccess` role can call both endpoints and `CloudImaging.PortalAccess` role can also call both (FR-053, FR-056, FR-063, contracts/operator-api.md) in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageApiContractTests.cs
- [x] T113 [P] [US7] Add Operator API contract tests for portal-role boot image lifecycle CRUD endpoints in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageLifecycleContractTests.cs
- [x] T114 [P] [US7] Add Imaging Core API integration tests for boot image lifecycle CRUD and role-bound behavior in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootImageLifecycleIntegrationTests.cs
- [x] T061 [P] [US7] Add Media Builder USB device qualification tests: bus type = USB and removable flag = true qualifies, non-USB bus type excluded, non-removable device excluded, host OS/system disk always blocked regardless of criteria (FR-054) in tests/CloudImaging.MediaBuilder.Tests/UsbDiskValidationTests.cs
- [x] T062 [P] [US7] Add Media Builder USB partition layout and deployment tests: (1) boot image list is presented as a selectable list with `isLatestPublished=true` entry pre-selected; (2) after download, SHA256 hash of downloaded WIM is computed and verified against `sha256Hash` from the SAS response; (3) hash mismatch triggers retry up to 3 times then aborts with BID stage code support reference; (4) hash verification passes → deploy to bootable partition proceeds; (5) branding logo is embedded in the WIM during Generate Boot Image and requires no separate write step during USB preparation (FR-053, FR-056) in tests/CloudImaging.MediaBuilder.Tests/UsbProvisioningWorkflowTests.cs
- [x] T160 [P] [US7] Add Media Builder support reference code format compliance tests: assert that all six CMB failure stages emit support reference codes conforming to {CMB}-{SessionRef}-{StageCode}-{EpochSeconds} with component prefix CMB and valid stage codes (DVI=disk-validation, PRT=partitioning, BID=boot-image-download, BCF=boot-config for the USB workflow; equivalent stage codes for boot image generation workflow); assert SessionRef uses a short operation-stage identifier when no session context is available; assert EpochSeconds is a non-zero Unix epoch value at time of error (FR-058) in tests/CloudImaging.MediaBuilder.Tests/SupportReferenceCodeTests.cs

### Implementation for User Story 7

- [x] T063 [US7] Implement Cloud Imaging Media Builder SignInView (welcome screen with branding logo, app title, and short capability description; Entra ID sign-in as first required action; MUST NOT navigate to OperationSelectionView until sign-in completes; FR-050, FR-052) and Entra auth orchestration service in src/CloudImaging.MediaBuilder/Views/SignInView.xaml, src/CloudImaging.MediaBuilder/ViewModels/SignInViewModel.cs, and src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs
- [x] T151 [US7] Implement Cloud Imaging Media Builder OperationSelectionView (Generate Boot Image card and Prepare USB Storage Device card), Windows ADK prerequisite detection service, and boot media certificate existence check: (1) verify copype.cmd and makewinpemedia present at startup; visually disable both workflow cards with installation guidance when ADK/WinPE add-on not detected; (2) for `CloudImaging.Administrator` role users, call `GET /api/bootmedia/certificate/metadata` on the Operator API after sign-in; if HTTP 404, disable the Generate Boot Image card with a prominent banner message instructing the administrator to generate a certificate in Portal Configuration before generating boot images; (3) a successful cert-metadata response enables the Generate Boot Image card (subject to ADK check); depend on T172 (OperatorApi cert PFX endpoint) -- T183 (cert metadata endpoint) MUST be implemented first (FR-050, FR-050a, FR-062) in src/CloudImaging.MediaBuilder/Views/OperationSelectionView.xaml, src/CloudImaging.MediaBuilder/ViewModels/OperationSelectionViewModel.cs, src/CloudImaging.MediaBuilder/Services/AdkPrerequisiteDetectionService.cs, and src/CloudImaging.MediaBuilder/Services/BootMediaCertificateCheckService.cs
- [x] T153 [US7] Implement Cloud Imaging Media Builder GenerateBootImageView source selection UI: GitHub auto-download option (calls MSEndpointMgr GitHub public releases API to resolve latest tag and download Client binaries with real-time progress display) and custom local path option (supports offline/airgap deployments); output folder picker; generation must not start until source and output folder confirmed (FR-051a) in src/CloudImaging.MediaBuilder/Views/GenerateBootImageView.xaml, src/CloudImaging.MediaBuilder/ViewModels/GenerateBootImageViewModel.cs, and src/CloudImaging.MediaBuilder/Services/GitHubReleasesClient.cs
- [x] T064 [US7] Implement Media Builder boot image generation workflow: assemble WinPE environment, Client binaries from source selected in T153, and manifest; retrieve current branding logo from Operator API and embed alongside Client executable in boot image WIM via BrandingLogoEmbedService (fall back to MSEndpointMgr default if no branding configured; FR-051); display completion notification with output folder path and next-step portal upload instructions on success (FR-051b) in src/CloudImaging.MediaBuilder/Services/BootImageGenerationService.cs and src/CloudImaging.MediaBuilder/Services/BrandingLogoEmbedService.cs
- [x] T065 [US7] Implement Media Builder boot image manifest embedding service: build a `BootImageManifest` (image version, timestamp, component checksums for the Cloud Imaging Client executable/branding logo/boot media certificate when present, deployment metadata) and embed it as `ci-manifest.json` at the root of the mounted WIM during generation; plain integrity/provenance record only, no cryptographic signing (FR-051 Option B) in src/CloudImaging.MediaBuilder/Services/BootImageManifestService.cs
- [x] T066 [US7] Implement Imaging Core API boot image query/SAS endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageFunctions.cs
- [x] T067 [US7] Implement Operator API boot image query/SAS proxy endpoints: (1) `GET /api/boot-images` response MUST include `sha256Hash` (of the WIM blob) and `isLatestPublished` (boolean; true for the most recently published active entry only) fields alongside existing metadata; (2) `POST /api/boot-images/{bootImageId}/sas` response MUST include `sha256Hash` so the Media Builder can verify the downloaded WIM before USB deployment (FR-053, FR-056, contracts/operator-api.md) in src/CloudImaging.OperatorApi/Functions/BootImageFunctions.cs
- [x] T115 [US7] Implement Imaging Core API boot image lifecycle CRUD endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageLifecycleFunctions.cs
- [x] T116 [US7] Implement Operator API portal-role boot image lifecycle proxy endpoints in src/CloudImaging.OperatorApi/Functions/BootImageLifecycleFunctions.cs
- [x] T117 [US7] Implement portal backend boot image lifecycle routes and service wiring in src/cloud-imaging-portal/server/src/routes/boot-images.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [x] T121 [P] [US7] Add portal backend contract tests for staged boot image upload session and finalize publish in tests/cloud-imaging-portal/server/boot-image-upload.test.ts
- [x] T122 [P] [US7] Add Imaging Core API integration tests for staged upload visibility, checksum validation, and publish commit in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootImageUploadLifecycleIntegrationTests.cs
- [x] T126 [P] [US7] Add portal frontend contract tests for staged boot image upload progress, retry, and finalize publish in tests/cloud-imaging-portal/client/boot-image-upload-flow.test.tsx
- [x] T068 [US7] Implement Media Builder operator API client for boot image retrieval in src/CloudImaging.MediaBuilder/Services/OperatorApiClient.cs
- [x] T069 [US7] Implement Media Builder boot image download service: download the selected boot image WIM via SAS URL entirely off the UI thread with real-time progress; after download completes, compute the SHA256 hash of the downloaded file and verify it against the `sha256Hash` value returned by the Operator API SAS endpoint; on hash mismatch, delete the corrupted download, retry up to 3 times with exponential backoff, and on exhaustion abort with a support reference code (BID stage code) and clear error message; deployment to the USB partition MUST NOT proceed until hash verification succeeds (FR-056) in src/CloudImaging.MediaBuilder/Services/BootImageDownloadService.cs
- [x] T070 [US7] Implement Media Builder USB device qualification (bus type = USB and removable flag = true required; host OS/system disk always blocked regardless of bus type or removable flag; FR-054) and two-partition provisioning in src/CloudImaging.MediaBuilder/Services/UsbSafetyValidationService.cs and src/CloudImaging.MediaBuilder/Services/UsbPartitionProvisioningService.cs
- [x] T071 [US7] Implement Media Builder boot image deployment to bootable partition, WinPE auto-start configuration, and preparation manifest; branding logo is embedded in the boot image WIM during the Generate Boot Image workflow (T064) and requires no separate retrieval or write step during USB preparation (FR-053); PrepareUsbStorageViewModel MUST subscribe to Windows DeviceWatcher (or equivalent WMI DeviceChanged notification) to auto-refresh the device list on USB plug/unplug events -- newly connected qualifying removable USB devices appear automatically and disconnected devices are removed; expose a manual Refresh command as a fallback (FR-054); implement PrepareStorageDeviceView.xaml with the device list, partition progress, and Refresh button in src/CloudImaging.MediaBuilder/Services/BootImageDeploymentService.cs, src/CloudImaging.MediaBuilder/ViewModels/PrepareUsbStorageViewModel.cs, and src/CloudImaging.MediaBuilder/Views/PrepareStorageDeviceView.xaml
- [x] T071a [US7] Implement `UsbPreparationManifest` persistence: after successful deployment, write `cloudimaging-manifest.json` (preparation timestamp, tool version, deployed boot image version, partition schema recording both BOOT and CACHE drive letters, disk validation results, auto-start-configured flag) to the root of the BOOT partition via `BootImageDeploymentService.WriteUsbPreparationManifestAsync`, called from `PrepareStorageDeviceViewModel` immediately after `DeployAsync` succeeds; this is the version record the Cloud Imaging Client reads at every boot to detect newer published boot images (FR-059) in src/CloudImaging.MediaBuilder/Services/BootImageDeploymentService.cs and src/CloudImaging.MediaBuilder/ViewModels/PrepareStorageDeviceViewModel.cs
- [x] T071b [US7] Implement Cloud Imaging Client boot image self-update: on every boot, locate the BOOT-labelled volume via WMI, read the local `cloudimaging-manifest.json`, call a new mTLS-authenticated (no session token required) Device Gateway API endpoint `GET /api/v1/boot-image/latest` (proxying ImagingCoreApi's existing boot image catalog/SAS endpoints, T066/T067) for the latest published version, and, if different, download it, verify its SHA256 hash, overwrite `\sources\boot.wim` on that same BOOT partition in place (safe because WinPE's RAMDISK boot has already loaded the current boot.wim into RAM), and update the local manifest's recorded version; runs as a fire-and-forget background task at startup, never blocks Client startup or the current session, and swallows/logs-as-warning every failure (FR-059a) in src/CloudImaging.Client/Services/BootImageSelfUpdateService.cs, src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs, src/CloudImaging.Client/App.xaml.cs, src/CloudImaging.DeviceGatewayApi/Functions/GetLatestBootImageFunction.cs, src/CloudImaging.DeviceGatewayApi/Services/ImagingCoreClient.cs, and src/CloudImaging.DeviceGatewayApi/Middleware/DeviceSessionTokenValidationMiddleware.cs
- [x] T123 [US7] Implement portal backend staged boot image upload session and finalize publish routes in src/cloud-imaging-portal/server/src/routes/boot-images.ts and src/cloud-imaging-portal/server/src/services/blobUploadService.ts
- [x] T124 [US7] Implement Operator API upload-session and finalize-publish proxy endpoints for boot images in src/CloudImaging.OperatorApi/Functions/BootImageUploadFunctions.cs
- [x] T125 [US7] Implement Imaging Core API staged boot image upload session and publish-commit endpoints in src/CloudImaging.ImagingCoreApi/Functions/BootImageUploadFunctions.cs
- [x] T125a [US7] Implement Imaging Core API boot image checksum validation, atomic publish-commit operation, and corruption detection in src/CloudImaging.ImagingCoreApi/Services/BootImageValidationService.cs and src/CloudImaging.ImagingCoreApi/Functions/BootImagePublishFunction.cs
- [x] T127 [US7] Implement portal frontend staged WIM upload flow with chunked progress, retry, and finalize publish UI in src/cloud-imaging-portal/client/src/pages/BootImagesPage.tsx and src/cloud-imaging-portal/client/src/components/BootImageUploadDialog.tsx
- [x] T128 [US7] Implement portal frontend upload session state handling and progress indicators in src/cloud-imaging-portal/client/src/services/bootImageUploadService.ts and src/cloud-imaging-portal/client/src/components/UploadProgressBar.tsx
- [x] T183 [P] [US7] Add Operator API contract tests for the boot media certificate metadata endpoint: `GET /api/bootmedia/certificate/metadata` returns 200 with thumbprintDisplay, subject, issuedAt, and expiresAt when an active cert exists; returns HTTP 404 when no active cert is configured; endpoint is accessible by `CloudImaging.MediaBuilderAccess` service role and rejected for `CloudImaging.PortalAccess` service role; response MUST NOT contain PFX bytes or private key material (FR-062) in tests/CloudImaging.OperatorApi.Tests/Contracts/BootMediaCertificateMetadataContractTests.cs
- [x] T184 [P] [US7] Implement Operator API GET /api/bootmedia/certificate/metadata endpoint: proxy to Imaging Core API (GET /api/internal/cert/active, which already returns non-sensitive metadata); enforce `CloudImaging.MediaBuilderAccess` service-level role only; return 200 with thumbprintDisplay (last 8 chars of SHA-256 thumbprint), subject, issuedAt, expiresAt; return 404 with descriptive message when no active cert exists; never return PFX bytes (FR-062) in src/CloudImaging.OperatorApi/Functions/BootMediaCertificateFunctions.cs
- [x] T170 [P] [US7] Add Media Builder tests for active boot media certificate PFX retrieval and boot image cert embedding: Operator API returns active cert PFX and it is successfully embedded in the boot image WIM alongside the Cloud Imaging Client executable; Operator API returns no active certificate and the Generate Boot Image workflow aborts with a clear error instructing the technician to generate a boot media certificate in the Portal Configuration section before generating a boot image; cert PFX endpoint is verified to require CloudImaging.MediaBuilderAccess service role (FR-070) in tests/CloudImaging.MediaBuilder.Tests/BootMediaCertificateEmbedTests.cs
- [x] T171 [US7] Implement Imaging Core API active boot media certificate PFX retrieval endpoint: retrieve PFX bytes from Azure Key Vault using the active certificate ID (via T161 KeyVaultCertificateService); return PFX bytes to the calling Operator API proxy; this endpoint is accessible only via Private Link (FR-062, FR-070) in src/CloudImaging.ImagingCoreApi/Functions/BootMediaCertificateFunctions.cs
- [x] T172 [US7] Implement Operator API active boot media certificate PFX endpoint (GET /api/bootmedia/certificate/pfx): restricted to the CloudImaging.MediaBuilderAccess service-level app role; proxy to Imaging Core API T171 endpoint over Private Link; returns the active certificate PFX bytes to the Media Builder for embedding in the boot image WIM; consumed exclusively by the Media Builder Generate Boot Image workflow (FR-062, FR-070) in src/CloudImaging.OperatorApi/Functions/BootMediaCertificateFunctions.cs
- [x] T173 [US7] Extend Media Builder boot image generation workflow to retrieve and embed active cert PFXieve the current active boot media certificate PFX from the Operator API (T172) alongside the branding logo before WIM assembly; embed the PFX in the boot image WIM in a location accessible to the Cloud Imaging Client executable at startup; if the Operator API returns no active certificate, abort generation immediately with a clear error message instructing the technician to generate a boot media certificate in the Portal Configuration section first; generation MUST NOT proceed without a valid active certificate (FR-051, FR-070) in src/CloudImaging.MediaBuilder/Services/BootImageGenerationService.cs

**Checkpoint**: US7 independently functional.

---

## Phase 7: User Story 4 - Cloud Imaging Portal Bulk Imaging Operations (Priority: P4)

**Goal**: Enable multi-device coupling/assignment and dashboard monitoring for concurrent imaging.

**Independent Test**: Simulate multiple waiting sessions, perform bulk assignment, verify per-device status updates and partial-failure isolation.

### Tests for User Story 4

- [x] T072 [P] [US4] Add Operator API bulk assignment contract tests in tests/CloudImaging.OperatorApi.Tests/Contracts/BulkAssignContractTests.cs
- [x] T073 [P] [US4] Add portal backend bulk assign route tests in tests/cloud-imaging-portal/server/session-bulk-assign.test.ts
- [x] T074 [P] [US4] Add portal frontend bulk assignment UI tests in tests/cloud-imaging-portal/client/bulk-assignment-flow.test.tsx

### Implementation for User Story 4

- [x] T075 [US4] Implement Imaging Core API bulk assignment service with conflict-safe processing in src/CloudImaging.ImagingCoreApi/Services/BulkAssignmentService.cs
- [x] T076 [US4] Implement Operator API bulk assignment endpoint in src/CloudImaging.OperatorApi/Functions/BulkAssignFunction.cs
- [x] T077 [US4] Implement portal backend bulk assignment route in src/cloud-imaging-portal/server/src/routes/sessions.ts
- [x] T078 [US4] Implement portal bulk assignment toolbar: context-sensitive [Assign Image to N selected sessions] button appearing when at least one Assigned-state row is checked (N counts only checked Assigned-state rows, regardless of other-state checked rows; FR-035) that opens AssignImageDialog (T042) with an N-count subtitle and N-reflecting confirm label; wires bulk confirmation to Operator API POST /api/sessions/bulk-assign in src/cloud-imaging-portal/client/src/components/BulkAssignPanel.tsx
- [x] T079 [US4] Implement portal session progress table and inline expandable detail panel: SessionProgressTable showing per-session current step, per-step status, and overall imaging completion percentage; each row includes a [View Details] toggle that expands an inline SessionRowDetailPanel directly below the row displaying a horizontal 3-node step indicator (Format / Download / Apply) with visual distinction for completed, active, and pending nodes, per-step status and timestamps, optional sub-progress, and -- for Failed sessions -- the support reference code and error detail inline (FR-034); multiple rows may be expanded simultaneously in src/cloud-imaging-portal/client/src/components/SessionProgressTable.tsx and src/cloud-imaging-portal/client/src/components/SessionRowDetailPanel.tsx

**Checkpoint**: US4 independently functional.

---

## Phase 8: User Story 5 - Cloud Imaging Portal OS Image Management (Priority: P5)

**Goal**: Support OS image upload/list/edit/delete with in-use guards.

**Independent Test**: Perform full image CRUD, verify catalog consistency, and verify deletion block when image is actively assigned.

### Tests for User Story 5

- [x] T080 [P] [US5] Add Operator API image CRUD contract tests in tests/CloudImaging.OperatorApi.Tests/Contracts/ImageCatalogContractTests.cs
- [x] T081 [P] [US5] Add portal backend image management route tests in tests/cloud-imaging-portal/server/image-management.test.ts
- [x] T082 [P] [US5] Add portal frontend image management UI tests in tests/cloud-imaging-portal/client/image-management-flow.test.tsx
- [x] T082a [P] [US5] Add portal backend contract tests for chunked OS image upload session, progress tracking, and resumable transfer in tests/cloud-imaging-portal/server/os-image-chunked-upload.test.ts

### Implementation for User Story 5

- [x] T083 [US5] Implement Imaging Core API image catalog endpoints in src/CloudImaging.ImagingCoreApi/Functions/ImageCatalogFunctions.cs
- [x] T084 [US5] Implement Imaging Core API active-session delete guard in src/CloudImaging.ImagingCoreApi/Services/ImageDeletionGuardService.cs
- [x] T085 [US5] Implement Operator API image catalog proxy endpoints in src/CloudImaging.OperatorApi/Functions/ImageCatalogFunctions.cs
- [x] T086 [US5] Implement portal backend image upload + metadata registration in src/cloud-imaging-portal/server/src/routes/images.ts and src/cloud-imaging-portal/server/src/services/blobUploadService.ts
- [x] T086a [US5] Implement portal backend chunked upload session service for large OS images (5-10GB) with resumable transfer, progress tracking, and partial-upload cleanup in src/cloud-imaging-portal/server/src/services/chunkedUploadService.ts and src/cloud-imaging-portal/server/src/routes/chunked-upload.ts
- [x] T087 [US5] Implement portal frontend image management page in src/cloud-imaging-portal/client/src/pages/ImagesPage.tsx
- [x] T087a [US5] Implement portal frontend chunked upload dialog with progress bar, pause/resume, retry logic, and error recovery for large OS images in src/cloud-imaging-portal/client/src/components/ChunkedUploadDialog.tsx and src/cloud-imaging-portal/client/src/services/chunkedUploadService.ts
- [x] T088 [US5] Implement portal frontend image editor dialog in src/cloud-imaging-portal/client/src/components/ImageEditorDialog.tsx

**Checkpoint**: US5 independently functional.

---

## Phase 9: User Story 6 - Cloud Imaging Portal Branding Configuration (Priority: P6)

**Goal**: Enable runtime branding updates (logo/colors/app name) without redeploy.

**Independent Test**: Update branding in portal and verify fresh page load reflects all changes.

### Tests for User Story 6

- [x] T089 [P] [US6] Add Operator API branding endpoint contract tests including GET /api/branding (get/put branding configuration) and GET /api/branding/logo/sas (time-limited SAS token URL for current branding logo asset, consumed by Media Builder boot image generation workflow; FR-062) in tests/CloudImaging.OperatorApi.Tests/Contracts/BrandingContractTests.cs
- [x] T090 [P] [US6] Add portal backend branding route tests in tests/cloud-imaging-portal/server/branding.test.ts
- [x] T091 [P] [US6] Add portal frontend branding flow tests in tests/cloud-imaging-portal/client/branding-settings-flow.test.tsx

### Implementation for User Story 6

- [x] T092 [US6] Implement Imaging Core API branding get/put endpoints in src/CloudImaging.ImagingCoreApi/Functions/BrandingFunctions.cs
- [x] T093 [US6] Implement Operator API branding proxy endpoints including GET /api/branding and PUT /api/branding for branding configuration get/put, and GET /api/branding/logo/sas to issue a time-limited SAS token URL for the current branding logo asset in the Storage Account for the Media Builder Generate Boot Image workflow (FR-062) in src/CloudImaging.OperatorApi/Functions/BrandingFunctions.cs
- [x] T094 [US6] Implement portal backend branding routes in src/cloud-imaging-portal/server/src/routes/branding.ts
- [x] T095 [US6] Implement portal branding settings page and logo upload flow in src/cloud-imaging-portal/client/src/pages/BrandingSettingsPage.tsx
- [x] T096 [US6] Implement dynamic CSS variable application for runtime branding in src/cloud-imaging-portal/client/src/context/brandingContext.tsx and src/cloud-imaging-portal/client/src/index.css
- [x] T143 [P] [US6] Add Operator API contract tests for portal configuration get/put endpoints in tests/CloudImaging.OperatorApi.Tests/Contracts/PortalConfigurationContractTests.cs
- [x] T144 [P] [US6] Add portal backend integration tests for portal configuration route in tests/cloud-imaging-portal/server/portal-configuration.test.ts
- [x] T145 [P] [US6] Add portal frontend integration tests for deployment configuration page and pre-flight authorization toggle in tests/cloud-imaging-portal/client/portal-configuration-flow.test.tsx
- [x] T146 [US6] Implement portal backend portal configuration route proxying PortalConfiguration get/put to Operator API in src/cloud-imaging-portal/server/src/routes/portal-config.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [x] T147 [US6] Implement portal frontend deployment configuration page with pre-flight authorization enable/disable toggle in src/cloud-imaging-portal/client/src/pages/DeploymentConfigPage.tsx and src/cloud-imaging-portal/client/src/components/PreFlightAuthorizationToggle.tsx
- [x] T174 [P] [US6] Add contract and integration tests for boot media certificate management: Imaging Core API generate endpoint creates a self-signed cert, stores PFX in Key Vault, persists BootMediaCertificate metadata in Table Storage, and atomically activates it; rotation endpoint deactivates prior cert and activates new cert in a single atomic operation with no overlap period; Operator API proxy enforces CloudImaging.PortalAccess service role for all cert management operations; active-metadata endpoint returns thumbprint, subject, validity, issued-at, and expires-at without exposing PFX bytes (FR-068) in tests/CloudImaging.ImagingCoreApi.Tests/Integration/BootMediaCertificateManagementIntegrationTests.cs and tests/CloudImaging.OperatorApi.Tests/Contracts/BootMediaCertificateManagementContractTests.cs
- [x] T175 [P] [US6] Add portal frontend tests for boot media certificate management UI: Configuration section displays BootMediaCertificatePanel with thumbprint, subject, expiry, and issued-by when a cert is active and a no-certificate-configured warning when none exists; Generate Certificate button visible to CloudImaging.Administrator role only; clicking it calls POST /api/cert/generate and refreshes the panel; Rotate Certificate button triggers a confirmation modal displaying the exact warning that all boot media using the current certificate will stop working immediately, with Cancel and Confirm Rotation actions (FR-068) in tests/cloud-imaging-portal/client/boot-media-cert-management.test.tsx
- [x] T176 [US6] Implement Imaging Core API boot media certificate management endpoints: POST /api/internal/cert/generate creates a new self-signed client certificate with the validity period from the PortalConfiguration (default 1 year), stores PFX in Azure Key Vault via T161 KeyVaultCertificateService, persists BootMediaCertificate metadata in Table Storage via T161 repository, and atomically sets the new cert as the sole active certificate (deactivating any prior active cert); POST /api/internal/cert/rotate performs the same generation and atomic swap in one operation with no overlap period; GET /api/internal/cert/active returns active certificate metadata only (no PFX); enforces exactly one active certificate at all times (FR-068) in src/CloudImaging.ImagingCoreApi/Functions/BootMediaCertificateFunctions.cs
- [x] T177 [US6] Implement Operator API boot media certificate management proxy endpoints: POST /api/cert/generate, POST /api/cert/rotate, and GET /api/cert/active/metadata; all three restricted to the CloudImaging.PortalAccess service-level app role; proxy to Imaging Core API cert management endpoints (T176) over Private Link; consumed by portal backend cert management routes (FR-068) in src/CloudImaging.OperatorApi/Functions/BootMediaCertificateFunctions.cs
- [x] T178 [US6] Implement portal backend boot media certificate management routes: GET /api/cert/active (returns metadata), POST /api/cert/generate (CloudImaging.Administrator user role required), POST /api/cert/rotate (CloudImaging.Administrator user role required; confirmation flag validated in request body before forwarding); proxy each route to the Operator API cert management endpoints (T177); surfaced under the Configuration section of the portal (FR-068) in src/cloud-imaging-portal/server/src/routes/boot-media-cert.ts and src/cloud-imaging-portal/server/src/services/operatorApiClient.ts
- [x] T179 [US6] Implement portal frontend boot media certificate management panel in DeploymentConfigPage: BootMediaCertificatePanel displays active certificate status showing last 8 characters of thumbprint, subject, expires-at, and issued-by, or a no-certificate-configured warning with a prompt to generate one; Generate Certificate button (CloudImaging.Administrator role only) calls POST /api/cert/generate and refreshes the panel on success; Rotate Certificate button opens a confirmation modal displaying the warning "All boot media using the current certificate will stop working immediately" with Cancel and Confirm Rotation actions; confirmed rotation calls POST /api/cert/rotate with confirmation flag and refreshes panel (FR-068) in src/cloud-imaging-portal/client/src/pages/DeploymentConfigPage.tsx and src/cloud-imaging-portal/client/src/components/BootMediaCertificatePanel.tsx

**Checkpoint**: US6 independently functional.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Hardening, performance, accessibility, deployment packaging, and final validation.

- [x] T097 [P] Finalize Bicep deployment graph and module wiring in src/deploy/bicep/main.bicep and src/deploy/bicep/modules/*.bicep
- [x] T098 [P] Implement update.ps1 community upgrade script: accepts -ResourceGroupName parameter; automatically discovers all target resource names in the resource group using the {prefix}-{env}-{type}[-{name}] naming convention; performs zip deploy in sequence to Device Gateway API Function App (az functionapp deployment source config-zip), Operator API Function App, Imaging Core API Function App, Portal backend App Service (az webapp deploy), and Portal frontend Static Web App (SWA deployment token retrieved from the SWA resource); infrastructure is not re-provisioned; idempotent and safe to re-run; requires both Connect-AzAccount (Az PowerShell) and az login (Az CLI) to be authenticated before invocation; initial fresh deployment is performed via the Azure Portal Template Spec wizard using T165 uiFormDefinition.json and T166 publish-template-spec.ps1 (not by this script); post-deployment validation is a manual checklist per FR-045 -- no automated validate.ps1 script (FR-041, FR-044a) in src/deploy/scripts/update.ps1
- [x] T099 [P] Implement CI quality gates (build/test/lint/perf/accessibility) in .github/workflows/ci.yml
- [x] T100 [P] Add portal WCAG 2.1 AA automated accessibility tests in tests/cloud-imaging-portal/client/accessibility/accessibility-a11y.test.tsx
- [x] T101 [P] Add performance test for 50 concurrent imaging sessions (SC-003) in tests/performance/session-concurrency.k6.js
- [x] T102 [P] Add performance benchmark for OS image CRUD with 500-item catalog (SC-008) in tests/performance/image-crud-latency.k6.js
- [x] T103 [P] Add performance test for Operator API boot image query/SAS generation (SC-015) in tests/performance/operator-api-boot-image-perf.k6.js
- [x] T129 [P] Add performance test for bulk assignment initiation latency for 20+ devices (SC-005) in tests/performance/bulk-assign-latency.k6.js
- [x] T104 [P] Add benchmark for boot image generation duration (SC-012) in tests/performance/media-builder-boot-generation-perf.ps1
- [x] T105 [P] Add benchmark for USB preparation duration (SC-013) in tests/performance/media-builder-usb-prep-perf.ps1
- [x] T106 [P] Add validation runbook for 20-device auto-launch matrix (SC-014) in specs/001-cloud-windows-imaging/validation-usb-autostart-matrix.md
- [x] T181 [P] Add end-to-end imaging timing benchmark test for SC-001: simulate full imaging cycle (session registration -> operator couple + assign -> client download + apply -> SessionCompleted) on a reference workload (~10 GB image over ~50 Mbps effective bandwidth) against a deployed dev environment; measure wall-clock time from session-init to SessionCompleted and assert completion within 900 seconds (15 minutes); also validate cached-image path completes within 300 seconds (5 minutes); document setup and results in docs/validation-e2e-timing.md (SC-001) in tests/performance/e2e-imaging-timing.ps1
- [x] T107 [P] Create deployment package document in specs/001-cloud-windows-imaging/deployment-package.md implementing all seven sections specified in FR-045 with every command accurate, complete, and copy-pasteable with no unexplained placeholders: (1) Deployment tier selection guide -- Standard (clientCertificateMode=require on Premium EP1, suitable for most organizations) vs Enterprise (deployApplicationGateway=true, WAF_v2 Application Gateway, required for compliance frameworks mandating WAF-level inspection) with decision criteria; (2) Prerequisites with exact install commands -- PowerShell 7.4+ ($PSVersionTable.PSVersion), Az module (Install-Module -Name Az -Repository PSGallery -Force -Scope CurrentUser), Az CLI (winget install Microsoft.AzureCLI; az --version to verify), Owner at resource group scope with justification (Microsoft.Authorization/roleAssignments provisioning for managed identities); (3) Entra ID app registration setup guide covering the two user-facing app roles for the shared registration (CloudImaging.Administrator/CloudImaging.Technician with Allowed member types Users/Groups), Microsoft Graph User.Read admin consent, user assignment, and a note that the Operator API app registration and its service-level roles (CloudImaging.PortalAccess/CloudImaging.MediaBuilderAccess) are provisioned automatically by Bicep -- no steps omitted; (4) Initial deployment 10-step walkthrough with exact commands: Connect-AzAccount, Set-AzContext -SubscriptionId, Get-AzContext to verify, New-AzResourceGroup -Name "rg-<prefix>-<env>-cloudimaging" -Location, Set-Location to Deploy folder, .\publish-template-spec.ps1 -ResourceGroupName -Location -Version, portal URL format and manual navigation path, Form View wizard tab-by-tab walkthrough (Basics/Configuration/Preview/Review+Create), deployment monitoring via Deployments blade (Succeeded in 8-15 min), recording Portal URL and API URLs from Outputs tab; (5) Post-deployment validation manual checklist -- five items: Portal URL sign-in with CloudImaging.Administrator, empty active session list, Configuration section accessible, Media Builder sign-in and role-appropriate workflow cards, boot image list retrieval without error; (6) Component upgrade walkthrough with exact commands: Connect-AzAccount, Set-AzContext, az login, Set-Location to new Deploy folder, .\update.ps1 -ResourceGroupName, re-run validation checklist, optional .\publish-template-spec.ps1 re-run if Bicep changed between versions; (7) Rollback guidance -- .\update.ps1 from prior bundle for code rollback; Remove-AzResourceGroup -Name -Force for failed initial ARM deployment followed by restart from step 4 (FR-044a, FR-045)
- [x] T108 [P] Create docs/self-hosting-guide.md as a concise community-facing quick-reference that summarises the deployment journey and links to the authoritative step-by-step commands in specs/001-cloud-windows-imaging/deployment-package.md (T107); the guide covers: solution overview, prerequisites at a glance, links to the full Entra ID setup guide and initial deployment walkthrough in deployment-package.md, a brief upgrade summary (download new release bundle, run update.ps1 -ResourceGroupName <rg>), common troubleshooting pointers, and a link to the GitHub Releases page; guide is intentionally shorter than deployment-package.md and does not duplicate its step-level command content in docs/self-hosting-guide.md
- [x] T109 [P] Update operations runbook with troubleshooting for session/token/SAS flows in docs/operations-runbook.md
- [x] T110 Execute quickstart validation scenarios and capture results in specs/001-cloud-windows-imaging/quickstart.md
- [x] T111 [P] Add Media Builder resumable download and interrupted-transfer recovery coverage in tests/CloudImaging.MediaBuilder.Tests/BootImageDeploymentTests.cs and src/CloudImaging.MediaBuilder/Services/BootImageDownloadService.cs
- [x] T112 [P] Add session inactivity expiry, heartbeat failure, and terminal purge coverage in tests/CloudImaging.ImagingCoreApi.Tests/Integration/LifecycleAndHeartbeatIntegrationTests.cs and src/CloudImaging.ImagingCoreApi/Services/DeviceSessionLifecycleService.cs
- [x] T118 [P] Add timed clean-tenant deployment rehearsal validation for <= 120 minute target (SC-009) in tests/validation/deployment-timing-validation.ps1 and docs/validation-deployment-timing.md
- [x] T182 [P] Add dedicated Azure staging environment smoke test validation for pre-merge release gating in tests/validation/staging-smoke-test.ps1 and docs/validation-staging-smoke-test.md; deploy to an isolated staging resource group or subscription distinct from the shared dev environment, then smoke-test the walking skeleton path and validate the portal and operator flows before merge (constitution dedicated staging requirement)
- [x] T119 [P] Add config-only environment promotion validation with artifact and code-diff checks (SC-010) ensuring environment promotion requires only parameter/environment changes in src/deploy/parameters/*.json, endpoint URLs, and secrets -- no source code differences; applies to self-hoster deployments that maintain their own dev/test/prod pipeline; core dev team uses a single shared subscription per FR-043 in tests/validation/promotion-config-only-validation.ps1 and docs/validation-promotion-config-only.md
- [x] T120 [P] Add documentation-only WinPE package reproducibility validation (SC-011) in tests/validation/winpe-package-repro-validation.ps1 and docs/validation-winpe-package-repro.md
- [x] T157 [P] Add in-place upgrade validation: starting from a successfully deployed prior release version, run update.ps1 with the new release archive and verify all six components reach the expected new version, all Azure resources reflect the updated IaC state, and the procedure completes without manual Azure portal intervention; document results in docs/validation-upgrade-path.md (FR-041, FR-044a)
- [x] T159 Configure Azure Workload Identity Federation for GitHub Actions OIDC authentication against the shared dev Azure subscription: create a federated credential on the shared Azure app registration that trusts the GitHub repository with the `environment:dev` subject claim (no service principal client secret required); create the GitHub Actions `dev` environment in the repository and populate all required environment secrets (AZURE_SUBSCRIPTION_ID, AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_RESOURCE_GROUP, FUNCTION_APP_DEVICE_GATEWAY, FUNCTION_APP_OPERATOR, FUNCTION_APP_IMAGING_CORE, APP_SERVICE_PORTAL_BACKEND, STATIC_WEB_APP_PORTAL); document the complete federated credential creation procedure (az ad app federated-credential create command and portal equivalent) as part of the developer environment onboarding guide; this is a one-time prerequisite for T158 and enables zero-credential OIDC authentication for both deploy-dev.yml and deploy-components.yml (FR-047) in docs/dev-environment-setup.md
- [x] T158 Implement GitHub Actions deployment workflows for the core development team's shared Azure dev environment: (1) deploy-dev.yml -- full-stack workflow triggered via workflow_dispatch that runs Bicep IaC deployment followed by sequential deployment of all five Azure-hosted components (Device Gateway API Function App, Operator API Function App, Imaging Core API Function App, Portal backend App Service, Portal frontend Static Web Apps); Cloud Imaging Client and Media Builder are build-only components and are not Azure-deployed; (2) deploy-components.yml -- per-component workflow triggered via workflow_dispatch with a required component name input (DeviceGatewayApi, OperatorApi, ImagingCoreApi, PortalBackend, PortalFrontend), deploying only the selected component to the shared dev environment; both workflows MUST use OIDC-based federated identity authentication via Azure Workload Identity Federation (no long-lived credential secrets) and MUST read all environment-specific values (subscription ID, resource group name, resource names) from GitHub Actions `dev` environment secrets; neither workflow is included in community release bundles (FR-047) in .github/workflows/deploy-dev.yml and .github/workflows/deploy-components.yml
- [x] T149 [P] Add Playwright E2E tests for Cloud Imaging Portal critical paths: passcode entry and device coupling flow, OS image assignment and session state progression, bulk assignment of multiple coupled sessions, and portal branding update with page-reload verification; configure Playwright runner and portal auth setup in tests/cloud-imaging-portal/e2e/portal-critical-paths.spec.ts and tests/cloud-imaging-portal/e2e/portal.setup.ts
- [x] T155 [P] Add VS Code workspace deployment tasks configuration with one named task per deployable Azure component (DeviceGatewayApi Function App, OperatorApi Function App, ImagingCoreApi Function App, Portal backend App Service, Portal frontend Static Web Apps); each task MUST invoke deploy-components.yml via `gh workflow run --field component=<name>`, providing a quick in-editor deployment trigger to the shared Azure dev environment; an equivalent direct `az`/`azd` CLI fallback MUST be documented as a comment in each task for offline development scenarios; VS Code tasks are for internal core developer use only and MUST NOT be included in community release bundles (FR-046, FR-047) in .vscode/tasks.json
- [x] T156 [P] Add GitHub Release packaging CI workflow: on version tag push, assemble all seven component release artifacts (DeviceGatewayApi zip, OperatorApi zip, ImagingCoreApi zip, Portal frontend static asset bundle, Portal backend zip, Cloud Imaging Client WinPE binary, Media Builder Windows installer; Portal is one architecture component contributing two separate release artifacts), the complete Bicep IaC template bundle, uiFormDefinition.json Form View UI definition, publish-template-spec.ps1 Template Spec publish script, update.ps1 upgrade script, all parameter templates, and deployment package document (deployment-package.md) into a single versioned release archive and publish to GitHub Releases; all seven component release artifacts MUST be present in every release archive regardless of whether they changed since the prior tag; the packaging workflow MUST explicitly exclude .github/workflows/deploy-dev.yml, .github/workflows/deploy-components.yml, and .vscode/tasks.json from the release archive as these are for internal developer use only; no deploy.ps1 or validate.ps1 should exist in the release bundle (FR-044a, FR-047) in .github/workflows/release.yml
- [x] T185 [P] Replace shallow/tautological test assertions with real behavioral coverage across the gap-remediation review findings: tests/CloudImaging.MediaBuilder.Tests/BootImageDeploymentTests.cs, tests/CloudImaging.MediaBuilder.Tests/BootImageGenerationTests.cs, tests/CloudImaging.MediaBuilder.Tests/BootMediaCertificateRetrievalTests.cs, tests/CloudImaging.MediaBuilder.Tests/DeviceGatewayEndpointStampingTests.cs, tests/CloudImaging.MediaBuilder.Tests/UsbPartitionDeploymentTests.cs, and tests/CloudImaging.MediaBuilder.Tests/EntraSignInTests.cs; each replaced test MUST assert on actual produced values/state (e.g. file contents, mock call arguments, returned objects) rather than merely asserting a call did not throw or that a trivially-true condition holds; follows the same fake-`HttpMessageHandler`/mock patterns already used in tests/CloudImaging.MediaBuilder.Tests/BootImageDownloadRetryTests.cs

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
- **US6 (P6)**: Starts after Phase 2. Independent of US4 and US5. Boot media certificate management tasks (T174-T179) depend on T161a/T161 (BootMediaCertificate repository and Key Vault service) from Foundational phase. T176 (ImagingCore cert endpoints) extends the file started by T171 (cert PFX retrieval from US7); complete T171 before T176. T177 extends the file started by T172 (Operator API cert proxy from US7); complete T172 before T177.

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
T057  T058  T059  T060  T061  T062  T150  T152  T154  T170  T183  T184

# Generation pipeline (T173 extends T064 with cert embedding; T171/T172 must complete before T173; T064 must complete before T173)
# T183/T184 (cert metadata endpoint test+impl) must complete before T151 (OperationSelectionView with cert check)
T183 -> T184  # cert metadata impl requires T183 test pass first
T184 -> T151  # OperationSelectionView cert check depends on cert metadata endpoint
T063 -> T151 -> T153 -> T064 -> T065
T171 -> T172
T064 -> T173  # T173 depends on BOTH T064 (generation baseline) and T172 (cert PFX endpoint)

# Prepare USB pipeline
T066 -> T067 -> T068 -> T069 -> T070 -> T071 -> T071a -> T071b

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
# Note: T098 (update.ps1) is independent; T165/T166 (uiFormDefinition.json, publish-template-spec.ps1) are Foundational [P] tasks

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
- Boot media mTLS cert chain: T161a -> T161 (repo+KV) -> T162 (Bicep) -> T163a -> T163 (thumbprint cache) -> T164 (mTLS middleware) -> T171 (ImagingCore PFX retrieval) -> T172 (OperatorApi PFX proxy) -> T173 (MediaBuilder embed) | T176 (ImagingCore cert mgmt) -> T177 (OperatorApi proxy) -> T178 (portal backend) -> T179 (portal frontend).
- Template Spec deployment chain: T165 (uiFormDefinition.json) + T166 (publish-template-spec.ps1) -> T098 (update.ps1) -> T107 (deployment-package.md) -> T156 (release packaging).

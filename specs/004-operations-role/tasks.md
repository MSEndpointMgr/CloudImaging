---

description: "Task list for the Operations role feature"
---

# Tasks: Operations Role

**Input**: Design documents from `specs/004-operations-role/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Included. The plan (Constitution II and III, research R9) replaces the string-only role tests with a real route-by-role enforcement test on the Portal server, adds client capability-map unit tests and Media Builder gate tests. Within each story, test tasks come first and must fail against the current code before the implementation tasks start.

**Organization**: Tasks are grouped by user story so each story can be implemented and verified on its own.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: User story the task belongs to (US1 to US5)
- Every task names the exact file(s) it changes

## Path Conventions

- Portal backend: `src/cloud-imaging-portal/server/src/`, tests in `tests/cloud-imaging-portal/server/`
- Portal frontend: `src/cloud-imaging-portal/client/src/`, tests in `tests/cloud-imaging-portal/client/`
- Media Builder: `src/CloudImaging.MediaBuilder/`, tests in `tests/CloudImaging.MediaBuilder.Tests/`
- Operator API: `src/CloudImaging.OperatorApi/Functions/`, tests in `tests/CloudImaging.OperatorApi.Tests/Contracts/`
- Release: `.github/workflows/`, `.github/release-notes/<stream>/<version>.md`
- Docs: `docs/`

## Writing rules for every task

- Files touched by a task contain no em dash, en dash, middle dot, right arrow or long right arrow (U+2014, U+2013, U+00B7, U+2192, U+27F9) in docs, user-facing text, log messages or comments when the task is done (FR-040). Navigation paths use "A > B"; the role hierarchy uses "implies".
- User-facing strings use ASCII punctuation and contain no requirement, task or story identifiers (FR-039).
- Release notes version: confirmed `v1.1.0` for all three streams (latest tags are `v1.0.0`). If a different version ships this feature, rename the files to match the tag minus its stream prefix.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish a green baseline and confirm the cleanup scope before any change.

- [ ] T001 Run the automated checks from quickstart.md section 1 (server lint and `npm run test:run`, client lint, `npx tsc --noEmit` and `npm run test:run`, `dotnet build CloudImaging.sln -c Release`, `dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release`, `dotnet test tests/CloudImaging.OperatorApi.Tests -c Release`) on the current branch and record that all pass, so later failures are attributable to this feature; also run `npm run test:coverage` in src/cloud-imaging-portal/server and src/cloud-imaging-portal/client and record the baseline line coverage of `src/middleware/roleGuard.ts`, `src/routes/session-history.ts` and whether the client's global 80% line threshold (`coverage.thresholds.lines` in src/cloud-imaging-portal/client/vite.config.ts) passes today
- [ ] T002 [P] Run `Select-String -Pattern "[\u2014\u2013\u00B7\u2192\u27F9]"` over the files in the Character cleanup table of specs/004-operations-role/plan.md and confirm the per-file line counts still match the table; note any drift for the cleanup done inside each story
- [ ] T073 [P] Add `coverlet.collector` (current stable version) as a `PackageVersion` in Directory.Packages.props and a `PackageReference` in tests/CloudImaging.MediaBuilder.Tests/CloudImaging.MediaBuilder.Tests.csproj, because `dotnet test --collect "XPlat Code Coverage"` produces no report without it (no test project references a coverage collector today); remove the four U+2014 in Directory.Packages.props comments; confirm `dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release --collect "XPlat Code Coverage" --results-directory TestResults/coverage-mediabuilder` writes a `coverage.cobertura.xml` and the Release build stays at zero warnings

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: One implication table and one capability source per Portal runtime (research R1, R4). Every Portal story builds on these.

**CRITICAL**: No Portal user story work (US1, US2) can begin until this phase is complete. US3 (Media Builder) does not depend on this phase.

### Tests for Foundational (write first, must fail)

- [ ] T003 [P] Extend tests/cloud-imaging-portal/server/role-hierarchy.test.ts: Administrator expands to Operations, Technician and PortalAccess; Operations expands to Technician and PortalAccess; Technician to PortalAccess; Reader and AutopilotApprover expand to themselves only; closure invariant (each implied role's own implications are already listed in the entry); no role implies Administrator; expansion is a superset and idempotent; `requireOperations` 403 detail reads `This operation requires one of the following roles: CloudImaging.Operations, CloudImaging.Administrator.`
- [ ] T004 [P] Create tests/cloud-imaging-portal/client/portal-roles.test.ts: `expandRoles` edges and closure invariant (data-model section 2), and `capabilitiesFromRoles` for each single role (the full table in data-model section 4), for Operations plus Technician, Operations plus Reader, Operations plus AutopilotApprover, an empty role list and unknown role values (ignored, case-sensitive)
- [ ] T005 [P] Replace the tautological string-equality checks in tests/cloud-imaging-portal/client/auth-guard.test.tsx with assertions against `capabilitiesFromRoles` (an Operations-only token has `hasPortalAccess`, `hasDashboardAccess` and `canUseImaging` true, so it never reaches the Access denied screen)

### Implementation for Foundational

- [ ] T006 Update src/cloud-imaging-portal/server/src/middleware/roleGuard.ts: add `CloudImaging.Operations` to the `PortalRole` union, set `ROLE_IMPLICATIONS` to the closure (`Administrator: [Operations, Technician, PortalAccess]`, `Operations: [Technician, PortalAccess]`, `Technician: [PortalAccess]`) keeping the single-pass expansion, export `requireOperations = requireRole('CloudImaging.Operations', 'CloudImaging.Administrator')` next to `requireAdmin`, and remove the U+2014 in its comment (T003 goes green)
- [ ] T007 [P] Create src/cloud-imaging-portal/client/src/lib/portalRoles.ts: role constants, implication table without PortalAccess, `expandRoles(raw: string[])`, `PortalCapabilities` type and `capabilitiesFromRoles(raw)` with exactly the ten flags in data-model section 4 (T004 goes green)
- [ ] T008 Update src/cloud-imaging-portal/client/src/context/authContext.tsx: add `CloudImaging.Operations` to the `PortalRole` union, compute capabilities once per render with `capabilitiesFromRoles(rolesFromAccount(account))` and expose all ten flags on the context; keep the existing `isTechnician` and `isReader` members for now so current consumers compile (removed in T030); remove the three U+2014 in comments (depends on T007; T005 goes green)

**Checkpoint**: Server and client compile and their suites pass. Existing roles behave as before. An Operations-only token already passes PortalAccess routes and the Access denied gate through the implication tables.

---

## Phase 3: User Story 1 - Operations user runs day-to-day administration without security settings (Priority: P1) MVP

**Goal**: An Operations-only Portal user can do every catalog, upload status, Locations, regions, Wi-Fi, Autopilot decision and report operation, sees the matching controls, and is refused (UI and server) everything on the FR-012 Administrator-only list.

**Independent Test**: Assign only Operations on the Portal to a test user. Perform each write in quickstart.md section 4 (Operations steps 1 to 12), then run the section 3 probes with that user's token: catalog, Locations, regions, Wi-Fi, Autopilot decision and history probes pass; `GET /portal-config`, `GET /cert/active`, `GET /update-check`, the pre-flight override and group tag write probes return 403 naming `CloudImaging.Administrator`.

### Tests for User Story 1 (write first, must fail)

- [ ] T009 [US1] Create tests/cloud-imaging-portal/server/role-route-matrix.test.ts: mount all 17 routers from src/cloud-imaging-portal/server/src/routes/ behind the injected-claims auth stand-in used in tests/cloud-imaging-portal/server/autopilot-routes.test.ts, mock every non-local service the routers import so no unit test makes a network call (constitution II): `@/services/operatorApiClient.js` (imported by all 17 routers) with resolved values, and `@/services/updateCheck.js` (`getUpdateStatus`, imported by routes/update-check.ts; the real one calls `fetch` on the GitHub releases URL); the remaining router imports are local and side-effect free (`middleware/roleGuard.js`, `middleware/validateParams.js`, `middleware/auth.js`, `utils/imageFileValidation.js`, `decidingUser` from `routes/autopilot.js`, and `axios`, used only for `axios.isAxiosError` in routes/cert.ts and routes/user-preferences.ts); stub the global `fetch` to throw so any unmocked call fails the test; drive one table from contracts/portal-server-routes.md for the 72 existing routes (session history rows are added in T038) against Administrator, Operations, Technician, Reader, AutopilotApprover, and Operations plus Technician; assert "not 401/403" or 403 per the "Accepted after" column, assert each 403 `detail` lists exactly the guard roles, assert no `operatorApiClient` write mock is called on a 403, and use invalid GUIDs where `requireGuidParams` applies so passing requests stop at 400
- [ ] T010 [P] [US1] Add Operations cases to tests/cloud-imaging-portal/server/autopilot-routes.test.ts: approve, reject and retry succeed and pass the Operations user as `decidingUser`; `GET /api/autopilot/history` and `GET /api/autopilot/group-tags` succeed; group tag `POST`, `PUT` and `DELETE` return 403 naming only `CloudImaging.Administrator`; queue list 403 detail (for a token with no accepted role) names Operations
- [ ] T011 [P] [US1] Add Operations cases to tests/cloud-imaging-portal/server/regions-routes.test.ts: `GET /api/regions` and `PUT /api/regions` succeed for Operations and return 403 for Technician with detail naming `CloudImaging.Operations, CloudImaging.Administrator`
- [ ] T012 [P] [US1] Add Operations cases to tests/cloud-imaging-portal/server/wifi-networks-routes.test.ts: list, create, reorder, update and delete succeed for Operations; create and update pass the Operations user's UPN as `changedBy` to the mocked `operatorApiClient`, as they do for Administrator (FR-017); no response body contains a password field for Operations or Administrator (FR-011b); Technician gets 403
- [ ] T013 [US1] Remove the tautological string-equality assertions in tests/cloud-imaging-portal/server/role-enforcement.test.ts that the route matrix now covers (research R9), keep any assertion that exercises real middleware, and replace the U+2192 in its comment. Do the same in the four other server files whose role checks compare a literal with itself and mount no router: tests/cloud-imaging-portal/server/image-management.test.ts (`GET /api/images` "requires PortalAccess role", `POST /api/images` "requires Administrator role"), os-image-chunked-upload.test.ts (upload start and `GET /api/upload-jobs/:uploadId` "requires Administrator role"), branding.test.ts (`GET /api/branding` PortalAccess, `PUT /api/branding` Administrator) and portal-configuration.test.ts (`GET /api/portal-config` "requires PortalAccess role", which is also wrong because the route is Administrator-only, and `PUT /api/portal-config` Administrator). Decision: remove these role `it` blocks as redundant rather than rewrite them; the matrix asserts the real guards with requests: Operations or Administrator for the image writes and upload status, Administrator only for Branding and portal configuration writes and `GET /api/portal-config`. No remaining test name or comment in the two image files may say an image write requires Administrator only. The non-role object-literal checks in these files are out of scope (depends on T009)
- [ ] T014 [P] [US1] Update the assertion reason text in tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageLifecycleContractTests.cs and tests/CloudImaging.OperatorApi.Tests/Contracts/ImageCatalogContractTests.cs to say the Portal backend accepts Operations or Administrator for these writes

### Implementation for User Story 1: Portal backend

All router tasks depend on T006 and make the matching T009 to T012 rows go green.

- [ ] T015 [P] [US1] In src/cloud-imaging-portal/server/src/routes/images.ts switch the 6 writes (`POST /`, `POST /upload/start`, `POST /upload/:uploadId/publish`, `POST /upload/:uploadId/abandon`, `PATCH /:imageId`, `DELETE /:imageId`) by replacing the inline `requireRole('CloudImaging.Administrator')` with `requireOperations`; leave `GET /` on PortalAccess or Reader; remove the U+2014 in its comment
- [ ] T016 [P] [US1] In src/cloud-imaging-portal/server/src/routes/boot-images.ts replace the inline `requireRole('CloudImaging.Administrator')` on the 5 writes (upload start, publish, promote, demote, delete) with `requireOperations`
- [ ] T017 [P] [US1] In src/cloud-imaging-portal/server/src/routes/recovery-images.ts switch the 3 writes (upload start, publish, delete) to `requireOperations`
- [ ] T018 [P] [US1] In src/cloud-imaging-portal/server/src/routes/upload-jobs.ts switch `GET /:uploadId` to `requireOperations` and remove the U+2014 in its comment
- [ ] T019 [P] [US1] In src/cloud-imaging-portal/server/src/routes/locations.ts switch `POST /`, `PUT /:locationId`, `DELETE /:locationId` and `PUT /:locationId/wifi-networks` to `requireOperations`; leave `GET /` on PortalAccess
- [ ] T020 [P] [US1] In src/cloud-imaging-portal/server/src/routes/regions.ts switch `GET /` and `PUT /` to `requireOperations`
- [ ] T021 [P] [US1] In src/cloud-imaging-portal/server/src/routes/wifi-networks.ts switch all 5 routes to `requireOperations` and confirm no response mapping returns a password
- [ ] T022 [P] [US1] In src/cloud-imaging-portal/server/src/routes/autopilot.ts add `CloudImaging.Operations` to `canView` (403 detail only), `canAudit`, `canDecide` and the `GET /group-tags` guard; keep group tag `POST`, `PUT` and `DELETE` on the inline `requireRole('CloudImaging.Administrator')`; keep `decidingUser` taken from the token

### Implementation for User Story 1: Portal frontend

All client tasks depend on T008.

- [ ] T023 [US1] Update src/cloud-imaging-portal/client/src/App.tsx per the Route guards table in contracts/portal-client-surfaces.md: `/sessions`, `/os-images`, `/boot-images`, `/recovery-images` on `canUseImaging` (rename `RequireOperationsAccess` to `RequireImagingAccess` so it cannot be confused with the role); `/locations` and `/wifi-networks` on `canManageLocations` instead of `RequireAdmin`; `/reports` and the four session report routes on `canViewReports` (replacing `isAdministrator || isReader`); `/reports/autopilot-registrations` on `canViewAutopilotReport`; `/branding` and `/configuration` stay on `RequireAdmin`; refused routes still redirect to `/`
- [ ] T024 [P] [US1] Update src/cloud-imaging-portal/client/src/components/Sidebar.tsx: rename access level `'operations'` to `'imaging'` (gated on `canUseImaging`), add access level `'manage'` (gated on `canManageLocations`) for Locations and Wi-Fi Networks, gate Reports on `canViewReports`, keep `'admin'` for Branding and Configuration, and delete the local `canOperations` and `canReports` computations
- [ ] T025 [P] [US1] Update src/cloud-imaging-portal/client/src/pages/DashboardPage.tsx per the Dashboard table in contracts/portal-client-surfaces.md: stats fetch of sessions, images and boot images on `canUseImaging`; Devices and catalog cards on `canUseImaging`; Reports card on `canViewReports`; Locations card on `canManageLocations`; Branding and Configuration cards on `isAdministrator`; rename the local access level `'operations'` to `'imaging'` (the `access` types, the stat and quick-link card entries, the filter and the switch case) to match Sidebar.tsx; delete the local `canOperations` and `canReports`; remove the three U+2014 in comments (the Completed Sessions fetch changes in T040)
- [ ] T026 [P] [US1] In src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx replace `isAdministrator` with `canManageCatalog` for upload, capacity warning, bulk select and delete, edit, delete and the Actions column; remove the U+2014 in its comment
- [ ] T027 [P] [US1] In src/cloud-imaging-portal/client/src/pages/BootImagesPage.tsx replace `isAdministrator` with `canManageCatalog` for upload, Actions column, promote, demote and delete; change the demote impact text to `Technicians can no longer prepare USB devices with it. Only Operations and Administrator users see it in Media Builder. USB devices are not affected.`; change the selected-file label to `` `${file.name} (${fmtSize(file.size)})` ``; remove the U+2192 and two U+2014 in comments
- [ ] T028 [P] [US1] In src/cloud-imaging-portal/client/src/pages/RecoveryImagesPage.tsx replace `isAdministrator` with `canManageCatalog` for upload, Actions column and delete; change the selected-file label to `` `${file.name} (${fmtSize(file.size)})` ``; remove the U+2192 and U+2014 in comments
- [ ] T029 [P] [US1] Update the doc comments in src/cloud-imaging-portal/client/src/pages/ReportsPage.tsx and src/cloud-imaging-portal/client/src/pages/AutopilotRegistrationsPage.tsx to name Operations among the roles that see Reports, Autopilot decisions and the history link (no logic change; flags come from the context)
- [ ] T030 [US1] In src/cloud-imaging-portal/client/src/context/authContext.tsx remove the raw `isTechnician` and `isReader` members once no consumer reads them, update any test mock under tests/cloud-imaging-portal/client/ (for example protected-route.test.tsx) that sets removed members, and confirm with a search that `isAdministrator` is now read only by `RequireAdmin` (Branding, Configuration), the pre-flight controls in SessionsPage.tsx, UpdateAvailableBanner.tsx and the Branding and Configuration Dashboard and navigation entries (depends on T023 to T029)

### Implementation for User Story 1: Operator API documentation (FR-025, no behaviour change)

- [ ] T031 [P] [US1] Correct the XML doc comments in src/CloudImaging.OperatorApi/Functions/ImageCatalogFunctions.cs to say the Portal backend accepts Operations or Administrator for OS and recovery image writes
- [ ] T032 [P] [US1] Correct the XML doc comments and the inline comment in src/CloudImaging.OperatorApi/Functions/BootImageLifecycleFunctions.cs (remove the claim that the middleware enforces Administrator; the Portal backend accepts Operations or Administrator), replace the three U+2192 and two U+2014, and change the delete log message to `Boot image delete proxied for {BootImageId}. ImagingCore HTTP {StatusCode}.`
- [ ] T033 [P] [US1] Correct the XML doc comments in src/CloudImaging.OperatorApi/Functions/LocationFunctions.cs (Operations or Administrator) and change the create log message to `Location created. HTTP {StatusCode}.`
- [ ] T034 [P] [US1] Correct the XML doc comments in src/CloudImaging.OperatorApi/Functions/WifiNetworkFunctions.cs to say the Portal backend accepts Operations or Administrator
- [ ] T035 [P] [US1] Correct the XML doc comments in src/CloudImaging.OperatorApi/Functions/AutopilotFunctions.cs to say decisions are accepted for Operations, AutopilotApprover or Administrator and settings for Administrator only

### Validation for User Story 1

- [ ] T036 [US1] Run the server suite (route matrix Operations and Operations plus Technician columns green for all 72 existing routes), client `npx tsc --noEmit`, `npm run lint` and `npm run test:run`, server `npm run lint`, and `dotnet build CloudImaging.sln -c Release` plus `dotnet test tests/CloudImaging.OperatorApi.Tests -c Release` with zero warnings; then run `npm run test:coverage` in src/cloud-imaging-portal/server and src/cloud-imaging-portal/client and gate on at least 80% line coverage for `src/middleware/roleGuard.ts` (server) and `src/lib/portalRoles.ts` (client) in the text report, and on the client's global 80% line threshold passing (or not regressing from the T001 baseline)

**Checkpoint**: An Operations-only Portal user has every US1 capability and is refused every FR-012 operation. This is the MVP.

---

## Phase 4: User Story 2 - Administrator and existing roles keep everything they have (Priority: P1)

**Goal**: Administrator, Technician, Reader and AutopilotApprover keep exactly their current access, and Reports-only session data is refused to Technician tokens while the Technician Dashboard's Completed Sessions stat keeps its count (FR-005, FR-015a).

**Independent Test**: Run quickstart.md sections 3 and 4 for `ci-admin`, `ci-tech`, `ci-reader` and `ci-approver` before and after the change. Results are identical except `GET /session-history` for Technician (pass before, 403 after), and the Technician Dashboard shows the same Completed Sessions count loaded from `/api/session-history/completed-timestamps`.

### Tests for User Story 2 (write first, must fail)

- [ ] T037 [P] [US2] Create tests/cloud-imaging-portal/server/session-history-routes.test.ts: Technician gets 403 on `GET /api/session-history` with detail `This operation requires one of the following roles: CloudImaging.Reader, CloudImaging.Operations, CloudImaging.Administrator.`; Reader, Operations and Administrator pass; `GET /api/session-history/completed-timestamps` passes for Administrator, Operations, Technician and Reader and returns 403 for AutopilotApprover; it forwards `from` and `to` to the mocked `operatorApiClient.getSessionHistory`, returns a JSON array of `terminalAt` strings for `SessionCompleted` records only, and returns no serial number, model, location, pre-flight, image or error field; the path is not captured by a parameterised route
- [ ] T038 [US2] Extend tests/cloud-imaging-portal/server/role-route-matrix.test.ts with the Narrow and New rows from contracts/portal-server-routes.md (bringing the table to 73 routes) and add a regression assertion that, for every route other than `GET /api/session-history`, the Administrator, Technician, Reader and AutopilotApprover results equal the "Accepted before" column (SC-003) (depends on T009)

### Implementation for User Story 2

- [ ] T039 [US2] In src/cloud-imaging-portal/server/src/routes/session-history.ts narrow `GET /` to `requireRole('CloudImaging.Reader', 'CloudImaging.Operations', 'CloudImaging.Administrator')`; add `GET /completed-timestamps` guarded by `requireRole('CloudImaging.PortalAccess', 'CloudImaging.Reader')`, registered before any parameterised path, forwarding `from` and `to` as today, calling `operatorApiClient.getSessionHistory(from, to)`, filtering to `SessionCompleted` and returning only the `terminalAt` strings; rewrite the route comment (no U+2014) to name the Dashboard as the reason for the separate read (depends on T006; T037 and T038 go green)
- [ ] T040 [US2] In src/cloud-imaging-portal/client/src/pages/DashboardPage.tsx switch the Completed Sessions stat fetch from `/api/session-history?from=...` to `/api/session-history/completed-timestamps?from=...`, adapt the response handling to an array of ISO 8601 strings, and keep the local-day bucketing and trend logic unchanged; update the doc comment that names `/api/session-history` (depends on T025 and T039)
- [ ] T041 [US2] Confirm with `git diff --stat` that these stay unchanged: src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx, UpdateAvailableBanner.tsx, DeploymentConfigPage.tsx, BrandingPage.tsx, ReportFailureDetailPage.tsx, ReportLocationStatisticsPage.tsx, ReportSessionOutcomesPage.tsx (they keep calling `/api/session-history`), src/CloudImaging.OperatorApi AppRoleAuthorizationMiddleware.cs, src/deploy/scripts/verify-app-registrations.ps1 and every Bicep template under src/deploy/
- [ ] T042 [US2] Run the server suite (all 73 matrix rows green for all six role columns), client `npx tsc --noEmit`, `npm run lint` and `npm run test:run`; then run `npm run test:coverage` in src/cloud-imaging-portal/server and src/cloud-imaging-portal/client and gate on at least 80% line coverage for `src/routes/session-history.ts` (including the new completed-timestamps route) and `src/middleware/roleGuard.ts` (server) and `src/lib/portalRoles.ts` (client)

**Checkpoint**: US1 and US2 together deliver the full Portal behaviour; no existing role lost a capability.

---

## Phase 5: User Story 3 - Operations user generates boot images in Media Builder (Priority: P2)

**Goal**: Operations users get Generate Boot Image (still subject to the ADK and certificate checks) and see pre-production boot images in Prepare USB Storage Device; Technicians see role text naming Operations or Administrator (contracts/media-builder-gates.md).

**Independent Test**: With `ci-ops` (Operations on Media Builder and Portal, MediaBuilderAccess on the Operator API), Home shows Generate Boot Image available, Prepare USB lists pre-production images; generate, upload in the Portal, prepare a USB drive while pre-production, promote. `ci-tech` sees the role restriction naming Operations or Administrator and production images only.

### Tests for User Story 3 (write first, must fail)

- [ ] T043 [P] [US3] Create tests/CloudImaging.MediaBuilder.Tests/MediaBuilderRolesTests.cs: `Expand` returns Administrator, Operations and Technician for Administrator; Operations and Technician for Operations; Technician only for Technician; empty for no roles; closure invariant; idempotent; ordinal comparison (a differently cased role value is not matched); unknown values ignored; and the expanded sets drive the capability table in data-model section 5 (Technician present means a Media Builder role; Operations present means generate and pre-production visibility). Test names follow `MethodOrScenario_StateUnderTest_ExpectedBehavior`
- [ ] T044 [P] [US3] Update tests/CloudImaging.MediaBuilder.Tests/OperationSelectionViewModelTests.cs: rename the `isAdministrator` argument to `canGenerateBootImage`; add Operations cases (generate available with ADK and configured certificate; ADK reason without ADK; certificate reasons for each non-configured status); assert the new reason texts `Requires the Operations or Administrator role.`, `Requires the Technician, Operations or Administrator role.` for generate (no role) and Prepare USB (no role)
- [ ] T045 [P] [US3] Update tests/CloudImaging.MediaBuilder.Tests/PrepareStorageDeviceViewModelTests.cs: `VisibleBootImages(images, canSeePreProduction: true)` lists active production and pre-production images, `false` lists active production only; assert the empty-list status `No production boot images are available yet. An Operations or Administrator user must promote one in the portal.`; remove the U+2014 in its comment

### Implementation for User Story 3

- [ ] T046 [US3] Create src/CloudImaging.MediaBuilder/Services/MediaBuilderRoles.cs: static class with the `CloudImaging.Administrator`, `CloudImaging.Operations` and `CloudImaging.Technician` constants, the implication closure, and `Expand(IEnumerable<string>)` returning an ordinal set (T043 goes green)
- [ ] T047 [US3] Update src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs: compute the expanded set with `MediaBuilderRoles.Expand` from the ID token `roles` claim; `HasMediaBuilderRole` true when it contains Technician; add `CanGenerateBootImage` and `CanSeePreProductionBootImages`, true when it contains Operations; keep `IsAdministrator` and `IsTechnician` for diagnostics only; remove the three U+2014 in comments (depends on T046)
- [ ] T048 [P] [US3] Update src/CloudImaging.MediaBuilder/ViewModels/OperationSelectionViewModel.cs: rename the constructor parameter and field `isAdministrator` to `canGenerateBootImage` (keeping the `hasMediaBuilderRole || canGenerateBootImage` fallback), use it in `IsGenerateBootImageAvailable`, `IsRestrictedByRole`, `IsBlockedByMissingAdk` and the certificate status checks, change `GenerateBootImageUnavailableReason` and `PrepareUsbUnavailableReason` texts per contracts/media-builder-gates.md, and remove the U+2014 in its comment (T044 goes green)
- [ ] T049 [P] [US3] Update src/CloudImaging.MediaBuilder/ViewModels/PrepareStorageDeviceViewModel.cs: `VisibleBootImages(IEnumerable<BootImageDto> images, bool canSeePreProduction)` and its caller using `_authService.CanSeePreProductionBootImages`; empty-list status text per the contract; boot image label `v{Version}, {Architecture}, Wi-Fi ({Size}){Stage}`; remove every U+00B7, U+2013 and U+2014 in the file (36 lines) (depends on T047; T045 goes green)
- [ ] T050 [P] [US3] In src/CloudImaging.MediaBuilder/Services/OperatorApiErrorDescription.cs change the access-denied text to `... granted separately from your Technician, Operations or Administrator role ...` as in the contract
- [ ] T051 [P] [US3] Update src/CloudImaging.MediaBuilder/Views/OperationSelectionView.xaml: Generate tile captions (role and no role), Prepare tile caption (no role), no-role banner, role banner (`Operations role required.` and `Generating boot images requires the Operations or Administrator role. Ask your Cloud Imaging administrator to assign it.`) and Operator API denied banner, exactly as in contracts/media-builder-gates.md; remove the three U+2014 in XML comments
- [ ] T052 [US3] Update src/CloudImaging.MediaBuilder/ViewModels/ShellViewModel.cs: replace the `IsAdministrator` property with `CanGenerateBootImage` from the auth service and use it for the Generate Boot Image gate, the navigation reason and the boot media certificate check trigger in the constructor (gate 2: the check must run for Operations); pass `canGenerateBootImage:` to `OperationSelectionViewModel`; change the three reason texts per the contract; remove the seven U+2014 in comments (depends on T047 and T048)
- [ ] T053 [US3] Run `dotnet build CloudImaging.sln -c Release` (zero warnings), `dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release --collect "XPlat Code Coverage" --results-directory TestResults/coverage-mediabuilder` and `dotnet format --verify-no-changes`; gate on at least 80% line coverage for the `CloudImaging.MediaBuilder.Services.MediaBuilderRoles` class (its `line-rate` in the newest `coverage.cobertura.xml`, read with the snippet in quickstart.md section 1); search src/CloudImaging.MediaBuilder and tests/CloudImaging.MediaBuilder.Tests for remaining `isAdministrator` arguments or gate reads of `IsAdministrator` (depends on T073)

**Checkpoint**: Media Builder gates match the contract for Administrator, Operations, Technician and no role. The certificate check trigger is verified manually in T069.

---

## Phase 6: User Story 4 - Existing tenant adds the role after upgrade (Priority: P2)

**Goal**: Existing tenants learn from the upgrade instructions and the published release notes how to add and assign the role without touching existing roles; release bodies come from committed notes files; the deployment form tooltip lists all five Portal user roles.

**Independent Test**: Follow docs/upgrade-instructions.md on a tenant with existing assignments: Operations exists on both registrations, every prior role and assignment is unchanged, and existing users keep their access until assigned. A workflow run for a version without a notes file fails inside "Determine version tag" naming the expected path, and creates no tag.

### Implementation for User Story 4

- [ ] T054 [P] [US4] In .github/workflows/release-iac.yml check and stage the release notes inside the "Determine version tag" step, after the tag pattern check (`if ($tag -notmatch ...)`, lines 76 to 78) and before the existence check that creates and pushes a missing tag on manual runs (`git rev-parse -q --verify`, `git tag`, `git push`, lines 79 to 91): move the `$version = $tag -replace '^mse-ci-', ''` assignment up to that point, read `.github/release-notes/backend/$version.md` from the working tree, throw naming the expected path when the file is missing or whitespace only (so a manual run without notes creates and pushes no tag), and copy it to `$env:RUNNER_TEMP/release-notes.md`; change "Create GitHub Release" from `body` to `body_path: ${{ runner.temp }}/release-notes.md`; leave the `-latest` alias release body unchanged
- [ ] T055 [P] [US4] Apply the same change to .github/workflows/release-client.yml with stream `client` and prefix `mse-ci-client-` (pattern check at lines 67 to 69, tag creation at lines 70 to 82)
- [ ] T056 [P] [US4] Apply the same change to .github/workflows/release-mediabuilder.yml with stream `mediabuilder` and prefix `mse-ci-mediabuilder-` (pattern check at lines 70 to 72, tag creation at lines 73 to 85), removing the fixed multi-line body (its U+2192 navigation reference moves into the notes file as ">")
- [ ] T057 [P] [US4] Add a release-specific section to docs/upgrade-instructions.md: add `CloudImaging.Operations` (value and display name, Users/Groups, enabled, descriptions from data-model section 1) to the Cloud Imaging Portal and Cloud Imaging Media Builder registrations; do not change, remove or re-create any existing role definition, role ID or assignment, and skip the step when the role already exists (FR-032); assign it; users sign out and back in; upgrade both the backend and Media Builder streams before relying on it; existing access is unchanged until someone is assigned; rollback leaves Operations-only users without access; replace the existing U+2192 navigation path with ">"
- [ ] T058 [P] [US4] In src/deploy/uiFormDefinition.json change the Portal client ID tooltip to `Client ID of the Cloud Imaging Portal SPA app registration. Exposes user_impersonation and the CloudImaging.Administrator, CloudImaging.Operations, CloudImaging.Technician, CloudImaging.Reader and CloudImaging.AutopilotApprover user roles. Used by the portal frontend (MSAL) and validated by the portal backend.`; leave the Media Builder and Operator API tooltips unchanged; confirm the file still parses with `ConvertFrom-Json`
- [ ] T059 [US4] Create .github/release-notes/backend/v1.1.0.md: the stream description and artifact table moved from the old fixed `body` in release-iac.yml with the version written out, plus an Operations notice telling administrators of existing tenants to add `CloudImaging.Operations` to the Portal and Media Builder registrations, assign it, and follow the upgrade instructions section from T057 (linked) (depends on T054 and T057)
- [ ] T060 [US4] Create .github/release-notes/mediabuilder/v1.1.0.md: the stream description and artifact table moved from the old fixed body in release-mediabuilder.yml (navigation written with ">"), plus a notice that Generate Boot Image and pre-production boot images are now available to `CloudImaging.Operations` on the Media Builder registration, with a link to the same upgrade section (depends on T056 and T057)
- [ ] T071 [US4] Create .github/release-notes/client/v1.1.0.md: the stream description and artifact table moved from the old fixed body in release-client.yml with the version written out; no Operations notice (the Client has no user roles). Without it the next client release fails at the notes step by design (depends on T055)
- [ ] T075 [P] [US4] Update CONTRIBUTING.md "Open a pull request" (lines 142 to 147, which say release notes are generated from PR titles and labels): release bodies now come from committed files `.github/release-notes/<stream>/<version>.md` (stream `backend`, `client` or `mediabuilder`; version is the tag minus its stream prefix, for example `v1.1.0`); a release run fails before any tag or build when its file is missing or empty; prerelease tags such as `v1.1.0-beta.1` need their own file; the file must be on `main` before the tag task runs; a new file starts as a copy of the previous one for the stream. Keep the PR title and label guidance only where it still applies. Also extend the `detail` text of the three "Release: Push ... tag" tasks in .vscode/tasks.json to say the matching notes file must already be on `origin/main` (text only)
- [ ] T061 [US4] Validate locally: run the PowerShell of the new staging step against `v1.1.0` for backend and mediabuilder (copies the file), against a missing version and against a whitespace-only temporary file (both throw naming the path); review the three workflow diffs to confirm the check runs inside "Determine version tag" before the tag existence check (so no tag is created or pushed for a missing file) and before any build or checkout of the tag, and that alias bodies are untouched; confirm the three notes files are free of the banned characters

**Checkpoint**: Upgrade path, release notes and tooltip are ready; the notes files must be on `main` before the tag tasks run (T070).

---

## Phase 7: User Story 5 - Administrators can tell who should get Operations (Priority: P3)

**Goal**: The roles reference and setup instructions describe the role, its capabilities and assignment, consistent with server behaviour.

**Independent Test**: Someone unfamiliar with the feature assigns Operations to a group using only docs/setup-instructions.md and docs/roles-and-access.md, and a member completes a catalog write after signing in again (SC-006).

### Implementation for User Story 5

- [ ] T062 [P] [US5] Update docs/roles-and-access.md: seven app roles including Operations with its description; hierarchy line "`Administrator` implies `Operations`, which implies `Technician`, which implies `PortalAccess`" (replacing the U+27F9 line); an Operations column in the Portal capability matrix and the Autopilot table and an Operations row in the Media Builder table, matching contracts/portal-server-routes.md and contracts/media-builder-gates.md; state that Operations includes every report and every Autopilot decision without Reader or AutopilotApprover; state that Reports session data is refused to Technician tokens even when called directly while the Dashboard Completed Sessions stat reads completion times only; state that the Media Builder Generate Boot Image gate is a workflow guard and publishing is authorised by the Portal backend (FR-030); add troubleshooting rows (Operations user cannot reach Branding or Configuration; Technician sees Generate Boot Image restricted; Operations cannot be assigned because the role was not added to the registration; role not effective until sign-out and sign-in); replace the six U+2192 navigation paths with ">"
- [ ] T063 [P] [US5] Update docs/setup-instructions.md: add Operations to the role lists of the Portal and Media Builder registrations (Phase 1) with the values and descriptions from data-model section 1; add a Cloud Imaging Operations group to the group-per-persona step and a column in its assignment table (Portal Operations, Media Builder Operations, and `CloudImaging.MediaBuilderAccess` on the Operator API for Operations users who build boot media) (Phase 3); change the Phase 5 Generate Boot Image prerequisite from Technician to Operations or Administrator and update the Media Builder sign-in prerequisites; state in the role-creation step that adding Operations must not change, remove or re-create any existing role definition, role ID or role assignment on any registration, and that the step is skipped when the role already exists (FR-032); replace all 33 U+2192 navigation paths with ">"
- [ ] T072 [P] [US5] Update README.md as a whole file: replace every U+00B7 link separator in the two header link rows (11 lines, lines 12 to 25) with a separator that is not a banned character (for example " | "); replace the role wording that lists only Administrator and Technician (step 2 around line 62, the Media Builder bullet around line 216, and "share the same two user-level roles" around line 241) with the current user roles including Operations, linking to docs/roles-and-access.md instead of repeating the matrix; fix the "Generate a boot image" step (lines 72 to 74), which says an administrator generates it and calls it Administrator-only, to say Operations or Administrator; leave no banned character anywhere in the file
- [ ] T074 [P] [US5] Update docs/image-upload-pipeline.md: the Portal Backend row of the component table (line 56) validates `CloudImaging.Operations` or `CloudImaging.Administrator`; the pre-production paragraph (lines 265 to 266) says only Operations and Administrator users see a pre-production boot image in Media Builder and test it before promoting; the demote paragraph (line 275) says sticks an Operations or Administrator user prepared with a pre-production image; the Authorization summary (lines 307 to 308) lists `CloudImaging.Operations` or `CloudImaging.Administrator` for start, publish, abandon and delete an upload and for polling an upload job; the file has no banned characters today and must stay clean
- [ ] T064 [US5] Cross-check docs/roles-and-access.md, docs/setup-instructions.md, docs/upgrade-instructions.md, docs/image-upload-pipeline.md and README.md against contracts/portal-server-routes.md, contracts/portal-client-surfaces.md and contracts/media-builder-gates.md: every Operations cell or statement matches server and UI behaviour, every cross-document link and anchor resolves, and the upgrade section from T057 points to the setup steps from T063 where it reuses them (depends on T057, T062, T063, T072, T074)

**Checkpoint**: All user stories are complete and documented.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Gates that span every story, then real-tenant verification.

- [ ] T065 Run the quickstart.md section 8 banned-character scan over its explicit file list (the plan's Character cleanup table, the plan's Source Code list and the new files of this feature: release notes files, new tests and new modules; not `git diff` against `main`, because the branch carries unrelated work) and fix every hit until it reports `missing: 0` and `hits: 0` (FR-040, SC-008)
- [ ] T066 [P] Search string literals, XAML text and log messages in every changed .ts, .tsx, .cs and .xaml file for requirement, task or story identifiers (`FR-`, `SC-`, `T0`, `US` followed by a digit) and remove any found from user-facing text (FR-039)
- [ ] T067 Run the full quickstart.md section 1 command list, including the three coverage runs (`npm run test:coverage` in src/cloud-imaging-portal/server and src/cloud-imaging-portal/client, `dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release --collect "XPlat Code Coverage"`), plus `dotnet format --verify-no-changes`; zero lint errors, zero build warnings, all suites green, and at least 80% line coverage on the new and changed code paths of this feature: `src/middleware/roleGuard.ts` and `src/routes/session-history.ts` (server), `src/lib/portalRoles.ts` (client) and `MediaBuilderRoles` (Media Builder)
- [ ] T068 In a test tenant, run quickstart.md sections 2 to 6 (tenant setup, direct API probes, Portal UI per role, Media Builder per role, regression baseline before and after) and record the results against the expected tables
- [ ] T069 Manual verification item: in a test deployment, remove the active boot media certificate and confirm `ci-ops` sees the certificate reason on Media Builder Home and generation is blocked (ShellViewModel certificate check trigger, no automated harness)
- [ ] T070 Before running the tag tasks, confirm .github/release-notes/backend/v1.1.0.md, .github/release-notes/client/v1.1.0.md and .github/release-notes/mediabuilder/v1.1.0.md are on `origin/main`; after each release workflow finishes, run the quickstart.md section 7 comparison and confirm the published body equals the committed file

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup. Blocks US1 and US2.
- **US1 (Phase 3)**: Depends on Foundational.
- **US2 (Phase 4)**: Depends on Foundational. Two tasks also depend on US1: T038 extends the matrix created in T009, and T040 edits DashboardPage.tsx after T025.
- **US3 (Phase 5)**: Depends only on Setup. Independent of the Portal phases and can run in parallel with them.
- **US4 (Phase 6)**: Documentation, workflows and tooltip have no code dependency and can start after Setup. The notes files (T059, T060) should describe shipped behaviour, so finalise them after US1 to US3 are merged or at least agreed.
- **US5 (Phase 7)**: Can start after Setup; T064 depends on T057 from US4. Final wording should follow the completed US1 to US3 behaviour.
- **Polish (Phase 8)**: Depends on all stories. T070 happens after merge.

### Notable cross-story dependencies

- T009 (US1 matrix) before T038 (US2 matrix rows): same file.
- T025 (US1 Dashboard gating) before T040 (US2 Completed Sessions fetch): same file.
- T039 (server completed-timestamps route) and T040 (Dashboard fetch) ship together in the backend stream so the Dashboard never calls a missing route.
- T057 (upgrade section) before T059, T060 (notes link to it) and T064.
- T073 (coverage collector) before T053 and T067, which read the Media Builder coverage report.
- T072 and T074 (README and image upload pipeline doc) before T064.
- Rollout: the backend and Media Builder streams must both be upgraded before Operations is relied on (documented in T057).
- US4 AS4 (fresh install has Operations from the start) is only verifiable after T063 in US5.

### Within Each User Story

- Test tasks first and red; implementation turns them green.
- Server: roleGuard.ts (T006) before every router task.
- Client: portalRoles.ts (T007) before authContext.tsx (T008) before pages, navigation and guards; T030 last.
- Media Builder: MediaBuilderRoles.cs before EntraAuthenticationService.cs before view models; ShellViewModel.cs last.

### Parallel Opportunities

- Setup: T002 alongside T001; T073 after the T001 baseline (it changes the Media Builder test project).
- Foundational: T003, T004, T005 together; T007 alongside T006.
- US1: T010, T011, T012, T014 together after T009 is started; T015 to T022 (8 routers) together; T024 to T029 together after T023 is started; T031 to T035 together and in parallel with all Portal work.
- US2: T037 alongside T038.
- US3: T043, T044, T045 together; T048, T049, T050, T051 together after T047.
- US4: T054 to T058 and T075 together.
- US5: T062, T063, T072 and T074 together.
- Across stories: US3 (Media Builder) and US4/US5 (docs and workflows) can run in parallel with US1 and US2 by different people.

---

## Parallel Example: User Story 1

```text
# Tests (after T009 has the harness in place):
Task: "T010 Add Operations cases to tests/cloud-imaging-portal/server/autopilot-routes.test.ts"
Task: "T011 Add Operations cases to tests/cloud-imaging-portal/server/regions-routes.test.ts"
Task: "T012 Add Operations cases to tests/cloud-imaging-portal/server/wifi-networks-routes.test.ts"
Task: "T014 Update reason text in tests/CloudImaging.OperatorApi.Tests/Contracts/*ContractTests.cs"

# Routers (after T006):
Task: "T015 images.ts"  Task: "T016 boot-images.ts"  Task: "T017 recovery-images.ts"
Task: "T018 upload-jobs.ts"  Task: "T019 locations.ts"  Task: "T020 regions.ts"
Task: "T021 wifi-networks.ts"  Task: "T022 autopilot.ts"

# Client pages (after T008):
Task: "T024 Sidebar.tsx"  Task: "T025 DashboardPage.tsx"  Task: "T026 OsImagesPage.tsx"
Task: "T027 BootImagesPage.tsx"  Task: "T028 RecoveryImagesPage.tsx"  Task: "T029 Reports and Autopilot doc comments"
```

## Parallel Example: User Story 3

```text
Task: "T043 Create tests/CloudImaging.MediaBuilder.Tests/MediaBuilderRolesTests.cs"
Task: "T044 Update tests/CloudImaging.MediaBuilder.Tests/OperationSelectionViewModelTests.cs"
Task: "T045 Update tests/CloudImaging.MediaBuilder.Tests/PrepareStorageDeviceViewModelTests.cs"

# After T047:
Task: "T048 OperationSelectionViewModel.cs"  Task: "T049 PrepareStorageDeviceViewModel.cs"
Task: "T050 OperatorApiErrorDescription.cs"  Task: "T051 OperationSelectionView.xaml"
```

## Parallel Example: User Story 4

```text
Task: "T054 release-iac.yml"  Task: "T055 release-client.yml"  Task: "T056 release-mediabuilder.yml"
Task: "T057 docs/upgrade-instructions.md"  Task: "T058 src/deploy/uiFormDefinition.json"
Task: "T075 CONTRIBUTING.md and .vscode/tasks.json release tag details"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 Setup, Phase 2 Foundational.
2. Phase 3 US1: route matrix red, routers and client surfaces green, Operator API comments corrected.
3. Stop and validate with the US1 independent test (quickstart.md sections 3 and 4 for `ci-ops`).
4. Do not release the MVP alone: US2 (P1) carries the Technician Reports narrowing and the Dashboard read, and must ship in the same backend release so the regression baseline holds.

### Incremental Delivery

1. Setup and Foundational: implication tables and capability source in place, no behaviour change for existing roles.
2. US1 and US2: complete Portal behaviour, backend stream releasable.
3. US3: Media Builder stream releasable.
4. US4 and US5: upgrade path, release notes, setup and roles docs; required before tagging either release.
5. Polish: character and identifier scans, full checks, tenant verification, then tag both streams with their notes files on `main`.

### Parallel Team Strategy

- Developer A: Foundational, then US1 and US2 (Portal backend and frontend).
- Developer B: US3 (Media Builder) from the start.
- Developer C: US4 and US5 (workflows, notes, docs, tooltip), finalising wording once A and B land.

---

## Notes

- 75 tasks. [P] tasks touch different files with no incomplete dependency. T071 and T072 were added after generation (client release notes, README), and T073 to T075 after analysis (coverage collector, image upload pipeline doc, CONTRIBUTING.md), so they sit inside their phases out of numeric order.
- Driver management: FR-011 and the Drivers part of FR-019 are delivered by spec 005 (`specs/005-os-driver-management`), not by tasks here. The Drivers parts of US1 acceptance scenario 1, SC-002 and quickstart.md section 4 apply once spec 005 ships; this feature supplies the role, `requireOperations` and the capability flags that spec 005 consumes (research R12).
- Commit after each task or logical group; stop at any checkpoint to validate the story on its own.

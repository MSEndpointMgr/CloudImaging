---

description: "Task list for OS driver management"
---

# Tasks: OS Driver Management

**Input**: Design documents from `specs/005-os-driver-management/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. The constitution makes test-first development non-negotiable (Principle II)
and requires a contract test for every new HTTP function (Principle III). In every phase the test
tasks come first and must fail against the current code before their implementation tasks start.
Tests run over real code and the in-memory doubles; they never call vendor hosts (fixtures only).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested on
its own. Action names in code and UI are Offline, Offline + Specialize, Specialize and Skip (wire
values `Offline`, `OfflineAndSpecialize`, `Specialize`, `Skip`).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: User story the task belongs to (US1 to US11 from spec.md)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Fixtures, spec alignment and shared contract types

- [X] T001 Capture the real vendor catalogs into tests/fixtures/drivers/ (`platformList.cab`, `8c26_64_11.0.24h2.cab`, `8cbe_arm64_11.0.24h2.cab`, `CatalogIndexPC.cab`, `Latitude_0CB2.cab`, `Latitude_0CB4.cab`, `Latitude_0C85.cab`) and record each source URL, capture date and compression (MSZIP for `CatalogIndexPC.cab` and the Dell catalogs; LZX for `platformList.cab` and the HP reference files) in tests/fixtures/drivers/sources.json
- [X] T002 [P] Amend specs/005-os-driver-management/spec.md to match the plan before any implementation:
  (a) action names Offline, Offline + Specialize, Specialize and Skip replace "WinPE" in FR-013, FR-016, FR-017, FR-062, FR-066, User Stories 1 to 3, the tiles and the timeline text ("26 installed offline");
  (b) ARM64: add to Scope that x64 and ARM64 models are supported and a set is only used when the OS image's architecture matches the model's; add to FR-016 that on HP ARM64 models Offline and Offline + Specialize are not allowed (HP's packages are x64 programs that ARM64 WinPE cannot run), defaults that would be either become Specialize, and Specialize runs the vendor package during the specialize pass; state in FR-050 and FR-051 that this applies "where the vendor package can run in the boot image"; extend SC-003 to "present in the installed OS at first boot, after the specialize pass for HP ARM64 models"; add an acceptance scenario to User Story 1 for an ARM64 device and to User Story 6 for a Dell ARM64 model;
  (c) FR-005: HP's model list comes from HP's platform list (system ID, products, per-release architecture), not the driver pack catalog;
  (d) FR-044: the release names the Windows version too ("Windows 11, version 24H2");
  (e) FR-045: Imaging Core matches (or takes the chosen model) and pins at assignment; the Device Gateway relays the pinned set;
  no identifiers outside the spec
- [X] T003 [P] Create src/CloudImaging.Contracts/Enums/DriverEnums.cs with `DriverVendor`, `DriverAction`, `DriverActionSource`, `DriverRuleScope`, `DriverRuleMatch`, `DriverComponentState`, `WindowsRelease` (`Windows10_22H2`, `Windows11_23H2`, `Windows11_24H2`, `Windows11_25H2`), `DriverSyncSchedule`, `DriverSyncTrigger`, `DriverSyncResult`, `DriverSyncItemOutcome`, `DriverSkipReason` (`NoMatchingModel`, `NoWindowsRelease`, `ReleaseNotOnboarded`, `NoDriverSet`, `ArchitectureMismatch`, `NoneChosen`), `DriverChoiceKind` (`Model`, `None`), `DriverComponentOrigin` (`Vendor`, `Custom`, `RolledBack`), `DriverRollBackUntil` (`NewerVersion`, `Undone`), with `DriverActionSource` as `Locked`, `ReplacedByCustom`, `Custom`, `RolledBack`, `Override`, `Rule`, `Default`, all `JsonStringEnumConverter`, per data-model.md section 1
- [X] T004 [P] Append `InstallDrivers` to `ImagingStepName` in src/CloudImaging.Contracts/Enums/ImagingEnums.cs and `DriverComponent` to `StepItemKind` and `Skipped` to `StepItemOutcome` in src/CloudImaging.Contracts/Enums/SetupProfileEnums.cs, with comments that they are appended so stored values stay stable
- [X] T005 [P] Add the contract fields of data-model.md section 2: `OsImage.WindowsRelease` in src/CloudImaging.Contracts/Models/OsImage.cs; `DeviceHardwareMetadata.SystemSku` (at most 64 chars) in Models/DeviceHardwareMetadata.cs; `DriverModelId`, `DriverModelName`, `DriverRelease`, `DriverSetVersion`, `DriverSkipReason`, `DriverMatchKey`, `DriverChosenManually`, `DriverChosenBy`, `DriverCriticalCategories` in Models/DeviceSession.cs; `DriverAction`, `ComponentVersion`, `DriverOrigin` and `FromUsbCache` in Models/SetupProfiles/ImagingStepItem.cs; `StatusNote` (at most 300 chars) on the progress report and step models
- [X] T006 [P] Create the API and descriptor models of contracts/operator-and-core-api.md section 1 and contracts/device-gateway.md under src/CloudImaging.Contracts/Models/Drivers/ (model row, model page, component, rule, sync settings, run, run item, storage, removal impact, preview request and result, assignment preview request and result, driver choice, session driver set descriptor with `matchKey`, component link)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Catalog reading, storage, vendor adapter seam, policy, sets, reporting and the portal shell. No user story can start before this phase is done.

**Tests first**

- [X] T007 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/CabinetReaderTests.cs: every fixture cabinet decompresses to well-formed XML (MSZIP for `CatalogIndexPC.cab` and the Dell catalogs, LZX for `platformList.cab` and both HP reference files); the 20-byte signature reserve is skipped; MSZIP history carries across blocks; a truncated cabinet, a wrong signature, a multi-folder cabinet and an unknown compression type throw `InvalidDataException` with a reason
- [X] T008 [P] Extend tests/CloudImaging.ImagingCoreApi.Tests/TestDoubles/InMemoryBlobContainerClient.cs with staged blocks and block list commit (uncommitted blocks invisible), and create tests/CloudImaging.ImagingCoreApi.Tests/TestDoubles/DriverTestBed.cs wiring every driver repository and service over the in-memory doubles with a `ManualTimeProvider` and a fake vendor `HttpMessageHandler` serving fixtures by URL
- [X] T009 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Repositories/DriverRepositoriesTests.cs: match key reservation is unique per vendor (409 names the owning model) and released on removal; set pointer writes fail on a stale ETag; `DriverWorkQueue` enqueues each message kind of data-model.md section 3.12 with its delay (more than 7 days is capped and the message re-enqueues itself) and creates `driver-work` and `driver-work-poison` when missing; the `run/active` row admits one run; a draft model's match keys are reserved like an active one's
- [X] T010 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverStorageServiceTests.cs: allowed HP and Dell hosts download; `http`, credentials, another host and a redirect to another host are refused with a reason; the SHA-256 is computed while streaming and blocks are committed only when length and hash match, otherwise nothing is visible and the reason is recorded; read SAS for one blob expires after 60 minutes; the `drivers` container is created when missing
- [X] T010a [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Integration/DriverStorageLiveTests.cs, run only when `CI_STAGING_CORE_STORAGE_ACCOUNT` is set (skipped otherwise): against the staging Core storage account with the CI's short-lived identity, the `drivers` container is created when missing, a fixture file streamed through `DriverStorageService` with a matching hash is committed and readable through its 60-minute user-delegation SAS, a mismatching hash leaves no committed blob, and the test deletes what it created; vendor HTTP stays served from fixtures
- [X] T010b Add a job to .github/workflows/ci.yml that signs in with the staging federated identity, sets `CI_STAGING_CORE_STORAGE_ACCOUNT` and runs the `Integration` tests of tests/CloudImaging.ImagingCoreApi.Tests against the isolated staging resource group
- [X] T011 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/HpDriverVendorAdapterTests.cs over the fixtures: platform list yields x64 and ARM64 models with system ID, display name and releases per architecture; reference file URLs use `_64_` or `_arm64_`; firmware, BIOS, dock firmware and names containing "Firmware" are excluded; "installable" per FR-015; every row of the HP classification table in contracts/vendor-catalogs.md section 1 gets its default and allowed actions; on ARM64 models Offline and Offline + Specialize are never allowed and defaults move to Specialize with install command `"<sp>.exe" /s`; lineage follows `Supersedes`; a non-installable `DPBCompliant` component with `Uwp` content defaults to Offline with Offline and Skip allowed
- [X] T012 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverPolicyResolverTests.cs: Locked when only Skip is allowed; override beats vendor rules beats all-vendor rules beats default; ID beats name beats category within a layer; same layer and kind, the most recently changed rule wins; a disallowed action is ignored and the next layer decides; `*` wildcard name patterns are case-insensitive and run in linear time on a 10,000-character name; Source and rule name are returned
- [X] T013 [P] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSetBuilderTests.cs: the fingerprint is stable under item order and changes with version, action or origin; Skip components are not in the set or the fingerprint, so a new version of a skipped component creates no set; the set holds no critical flag; rolled-back components use their held version and custom components replace their vendor lineage; an unchanged fingerprint or one equal to `RolledBackFromFingerprint` creates no set; a new set waits as pending until every file is verified; a failed file keeps the component's last verified version or leaves it out; withdrawn components stay at their last verified version; items are ordered Offline first, then category and name
- [X] T014 [P] Extend tests/CloudImaging.ImagingCoreApi.Tests/Functions/ReportProgressItemsTests.cs and the overall progress tests: `InstallDrivers` is optional with weight 0, its final report stores items with `DriverAction`, `ComponentVersion`, `DriverOrigin`, `Skipped` outcomes and a `statusNote`, and is capped at 1500 items
- [X] T015 [P] Create tests/cloud-imaging-portal/client/drivers-shell.test.tsx: the Drivers sidebar item sits between Recovery Images and Setup Profiles for Technician and above and is absent for Reader; `/drivers` renders the five tabs (Models, Rules, Sync, Downloads, Storage) from `?tab=`; `canViewDrivers` and `canManageDrivers` in tests/cloud-imaging-portal/client/portal-roles.test.ts

**Implementation**

- [X] T016 Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/CabinetReader.cs (MSZIP inflater with a carried 32 KB window and an LZX decoder, written from the published specifications, single folder, streaming output) (T007 green)
- [X] T017 Implement the repositories of data-model.md sections 3 and 4 in src/CloudImaging.ImagingCoreApi/Repositories/ (`DriverVendorSettingsRepository`, `DriverSyncSettingsRepository`, `DriverCatalogModelRepository`, `DriverModelRepository` with `DriverMatchKeys`, `DriverComponentRepository`, `DriverOverrideRepository`, `DriverRuleRepository`, `DriverCriticalCategoryRepository`, `DriverSetPointerRepository`, `DriverSetDocumentRepository`, `DriverFileRepository`, `DriverSyncRunRepository`, `DriverChangeLogRepository`, `DriverComponentVersionRepository`, `DriverRollBackRepository`, `DriverCustomComponentRepository` with `DriverCustomTargets` and `DriverReplacements`, `DriverSetBuildRepository`); implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverWorkQueue.cs over `AzureWebJobsStorage`; add `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` to Directory.Packages.props and src/CloudImaging.ImagingCoreApi/CloudImaging.ImagingCoreApi.csproj and `extensions.queues` (`batchSize` 4, `newBatchThreshold` 2, `maxDequeueCount` 3, `maxPollingInterval` `00:00:10`) to src/CloudImaging.ImagingCoreApi/host.json; register them and create the tables, the `drivers` container and both queues at startup in src/CloudImaging.ImagingCoreApi/Program.cs (T009 green)
- [X] T018 Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverStorageService.cs: vendor host allowlist, streaming download into staged blocks with `IncrementalHash`, commit on match, read SAS per blob through `BlobSasUrlGenerator`, register a named `HttpClient` for vendor downloads (T010 and T010a green)
- [X] T019 Create `IDriverVendorAdapter` and implement src/CloudImaging.ImagingCoreApi/Services/Drivers/HpDriverVendorAdapter.cs per contracts/vendor-catalogs.md section 1 and research R24, parsing with `XmlReader` (T011 green)
- [X] T020 [P] Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverPolicyResolver.cs as a pure function (T012 green)
- [X] T021 [P] Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetBuilder.cs (fingerprint, pointer transitions, pending sets, set documents) (T013 green)
- [X] T022 Accept `InstallDrivers`, `DriverAction`, `Skipped` and `statusNote` in src/CloudImaging.ImagingCoreApi/Functions/ReportProgressFunction.cs, src/CloudImaging.ImagingCoreApi/Repositories/ImagingStepItemRepository.cs and src/CloudImaging.ImagingCoreApi/Services/OverallProgressCalculator.cs, and relay `statusNote` in src/CloudImaging.DeviceGatewayApi/Functions/ReportProgressFunction.cs (T014 green)
- [X] T023 Create src/cloud-imaging-portal/server/src/routes/drivers.ts with the path parameter validators of contracts/portal-server-routes.md (`:vendor`, `:release`, `:lineageKey`, GUIDs, 400 without an upstream call) and mount it at `/api/drivers` in src/cloud-imaging-portal/server/src/app.ts; add the Operator API proxy class src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with its `ImagingCoreClient` plumbing, relaying status and body unchanged
- [X] T024 Add `canViewDrivers` and `canManageDrivers` to src/cloud-imaging-portal/client/src/lib/portalRoles.ts, the Drivers item (`Cpu`, size 18) to src/cloud-imaging-portal/client/src/components/Sidebar.tsx, the lazy route `/drivers` to src/cloud-imaging-portal/client/src/App.tsx, "Drivers" to src/cloud-imaging-portal/client/src/lib/routeTitles.ts, src/cloud-imaging-portal/client/src/lib/drivers.ts (types, action labels, `vendorLabel`, API calls) and the tab shell src/cloud-imaging-portal/client/src/pages/DriversPage.tsx (T015 green)

**Checkpoint**: Catalogs parse, files can be stored and verified, policy and sets are computable, the portal has a Drivers page shell.

---

## Phase 3: User Story 1 - Onboard an HP model and have its drivers installed during deployment (Priority: P1) MVP

**Goal**: An Operator onboards an HP model for a Windows release as a draft, reviews it on the model page and completes onboarding, which stores and verifies its files; assignment pins the set, the Client injects the Offline components after Apply image, and the timeline shows the step.

**Independent Test**: Onboard one HP model with one release, complete onboarding, deploy a device of that model, and confirm the Offline components are in the installed driver store at first boot and the timeline lists them (quickstart sections 4.1, 4.2 and 5).

**Tests first**

- [X] T025 [P] [US1] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSyncServiceTests.cs: a manual run (Sync now) refreshes the HP model list while a `Check` run reads no model list, a sync records per vendor the models it added to the list (`newModels`), resolves each onboarded model and release, queues downloads for files not yet stored, builds sets when all files are verified, records a `DriverSetBuilds` row per build and a `DriverComponentVersions` row per new component version, records run items and a result, and deletes files no current, previous or pinned set references at the end; a component no longer published stays in the set at its last verified version, is marked `Withdrawn`, and is recorded as a `Withdrawn` run item, and a republished lineage continues as a newer version and clears the state; each step is one `driver-work` message enqueued by the step before it, the run's `Outstanding` count reaches zero exactly once and only that message enqueues `FinalizeRun`, a message handled twice changes nothing the second time, a poison message records its item as failed so the run still finishes, and the 2-hour `FinalizeRun` ends a run still Running as Failed; a draft model's components are refreshed but no `DownloadFile` or `BuildSet` is enqueued for it
- [X] T026 [P] [US1] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverOnboardingFunctionsTests.cs: catalog models list with onboarded ones flagged; a missing list or one older than 7 days is fetched on the call whether the vendor's sync is on or off and records `FetchedAt`, a list fetched within 7 days is not fetched again, a failed fetch with an older list returns 200 with `stale` and the warning naming the catalog URL, a failed fetch with no list returns 503 with "Can't reach catalog at <url>", and none of these creates a run or moves a limit; preview returns counts per action and size per model for up to 20 models with per-model errors; onboard reserves match keys (409 naming the owning model), creates `Draft` models with resolved components, returns `modelIds` in request order, writes a change log row with the actor, and enqueues nothing; Complete onboarding sets `Active`, turns the vendor on, enqueues `ResolveModel` per release and returns 409 for an active model; drafts are ignored by assignment and the assignment preview; add release (400 when the catalog model does not list it, marks the new pair dirty); models list and model page shapes of contracts/operator-and-core-api.md section 1; 400 without an actor
- [X] T027 [P] [US1] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSetResolverTests.cs and extend tests/CloudImaging.ImagingCoreApi.Tests/Integration (assignment): HP matches `MotherboardModel` case-insensitively; each skip reason (`NoWindowsRelease`, `NoMatchingModel`, `ArchitectureMismatch`, `ReleaseNotOnboarded`, `NoDriverSet`) is pinned with the device's `DriverMatchKey`; single and bulk assignment pin model, release and set version and copy the vendor's critical categories to `DriverCriticalCategories`; a later sync, edit, critical category change or removal does not change the pin
- [X] T028 [P] [US1] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/SessionDriverSetFunctionTests.cs: the descriptor lists the pinned set's components with version, origin and one 60-minute SAS each, marks `critical` from the session's `DriverCriticalCategories` (not from the set), the skip reason and `matchKey` when set, 409 outside SessionStarted and SessionInProgress, 404 unknown session; the link endpoint returns a new SAS for one index and 400 outside the set; files of a pinned set of a removed model still resolve
- [X] T029 [P] [US1] Create tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/DriverSetContractTests.cs (route session must equal the token's, explicit field mapping, only the SAS host is logged, 429 and 503 relay) and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs (every onboarding, model and sync run route proxies method, path, query and body unchanged and relays status and body)
- [X] T030 [P] [US1] Create tests/cloud-imaging-portal/server/drivers-routes.test.ts for routes 3 to 8, 26 to 27 and 43 of contracts/portal-server-routes.md (guards, actor from the token, browser actor fields discarded, upstream errors relayed) and add those routes to tests/cloud-imaging-portal/server/role-route-matrix.test.ts; extend tests/cloud-imaging-portal/server/image-management.test.ts and os-image-chunked-upload.test.ts for `windowsRelease`
- [X] T031 [P] [US1] Create tests/CloudImaging.Client.Tests/Drivers/DriverSetLoaderTests.cs and DriverInstallServiceTests.cs: descriptor 200, 404 (step not shown), 409 and 3 failed attempts; per component download with streamed SHA-256 and retry, link refresh on 403 or near expiry; HP extraction runs `/s /e /f "<x>"` then the legacy switches when nothing was produced, success needs an `.inf`; DISM arguments are `/Image:<W>:\ /Add-Driver /Driver:"<x>" /Recurse` without `/ForceUnsigned`; outcomes and reasons of contracts/client-install-drivers.md section 3, each item with `componentVersion` and `driverOrigin`; step result rows of section 4 including each skip note (the `NoMatchingModel` note names the match key when the descriptor has one) and critical failure; status lines; folders deleted after injection; nothing written to X:; vendor extraction writes only inside the component's own folder on the Windows volume and never to the USB drive; DISM is never run against the image file itself (no `/Mount-Image` or `/Image:` pointing at the WIM), so stored OS images stay unchanged; progress for the step is aggregated to at most 10 Gateway calls per 30 seconds; a critical failure's message names what failed, the likely cause and the fix
- [X] T031a [P] [US1] Extend tests/CloudImaging.Client.Tests/UiThreadResponsivenessTests.cs: `DriverInstallService` and `DriverSetLoader` expose only async entry points; running the step's download, hashing, extraction and DISM through the view model with fakes never blocks the dispatcher for more than 100 ms (dispatcher timer probe), and status lines and percent reach the UI while it runs
- [X] T032 [P] [US1] Create tests/cloud-imaging-portal/client/drivers-models.test.tsx (Models tab columns and widths with Status widest, vendor filter, search, Size info text, Status as icon and label (Draft, Downloading n of m, waiting for approval, failed, Sync off, empty otherwise), empty state; onboarding dialog steps with preview loading per model and the "Nothing is downloaded yet" note, the four model list states (fetching, ready with "list from" in the footer, stale with the warning, failed with Try again and Next disabled) and the "Sync is off" line, and Onboard navigating to the first model's page; model page header with breadcrumb, name and the match key badge to its right, and the Check for updates button on its own row, disabled on a draft (Roll back driver set, Remove model and Add custom component join that row with their stories), with no vendor badge, set version or build time, the Draft banner whose Complete onboarding calls route 43 and is replaced by the Preparing banner, the Preparing banner, tiles including Size, toolbar filters (search, Windows release, action, category, "N updated by today's sync") and read-only component table) and extend tests/cloud-imaging-portal/client/image-management-flow.test.tsx (Windows release select in edit and upload) and session-progress-details.test.tsx (Install drivers card, summary texts, skip note, warning marker, item Version column with Custom and Rolled back labels, five columns without a driver pin). Every control has an accessible name; tabs use `role="tablist"`/`tab`/`tabpanel` with `aria-selected`; the Action dropdowns, switches, chevrons and dialogs work with the keyboard and return focus on close; locked and status badges convey their meaning in text, not colour alone. Also: the component table lists every component without paging and shows the filtered count; HP component details show every HP field and the release notes link and never an install command, hash or supported releases; every Drivers table has one-line rows with truncation and absolute timestamps (`2026-10-22 02:00`); the Install drivers card shows Specialize components as staged with no result; a deployment without an Install drivers step renders as before

**Implementation**

- [X] T033 [US1] Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncService.cs (run creation with the `run/active` row and `Outstanding` count, handlers for the `FetchVendorCatalog`, `ResolveModel`, `DownloadFile`, `BuildSet` and `FinalizeRun` messages, clean-up, withdrawal detection, change log) and, in src/CloudImaging.ImagingCoreApi/Functions/DriverSyncFunctions.cs, the queue triggers `DriverWorkProcessor` (`driver-work`) and `DriverWorkPoison` (`driver-work-poison`) and `StartDriverSyncRun` (T025 green)
- [X] T034 [US1] Implement the onboarding (drafts), Complete onboarding, add release, model list and model page endpoints in src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs with the 24-hour catalog cache for preview and the on-demand model list fetch (7-day freshness, `list/<vendor>` row), and exclude drafts in `DriverSetResolver` (T026 green; `PreviewDriverAssignment` leaves drafts out in T093)
- [X] T035 [US1] Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetResolver.cs and call it after the setup profile pin in src/CloudImaging.ImagingCoreApi/Functions/AssignSessionFunction.cs and src/CloudImaging.ImagingCoreApi/Services/BulkAssignmentService.cs; persist the session fields in src/CloudImaging.ImagingCoreApi/Repositories/DeviceSessionRepository.cs (T027 green)
- [X] T036 [US1] Implement src/CloudImaging.ImagingCoreApi/Functions/SessionDriverSetFunction.cs (descriptor and link refresh) (T028 green)
- [X] T037 [US1] Implement src/CloudImaging.DeviceGatewayApi/Functions/SessionDriverSetFunction.cs with its `ImagingCoreClient` calls and OpenApiConfig entries, and complete the Operator API proxies and OpenApiConfig entries for the US1 routes in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs (T029 green)
- [X] T038 [US1] Add `WindowsRelease` to the OS image catalog: map it in src/CloudImaging.ImagingCoreApi/Functions/ImageCatalogFunctions.cs, src/CloudImaging.ImagingCoreApi/Repositories/OsImageRepository.cs, the upload start in src/CloudImaging.ImagingCoreApi/Functions/OsImageUploadFunctions.cs and src/CloudImaging.ImagingCoreApi/Services/UploadPublishService.cs (400 on an unknown value), and pass it through src/cloud-imaging-portal/server/src/routes/images.ts
- [X] T039 [US1] Implement the US1 routes in src/cloud-imaging-portal/server/src/routes/drivers.ts and their calls in src/cloud-imaging-portal/server/src/services/operatorApiClient.ts (T030 green)
- [X] T040 [US1] Implement src/CloudImaging.Client/Services/Drivers/DriverSetLoader.cs and the driver set calls in src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs
- [X] T041 [US1] Implement src/CloudImaging.Client/Services/Drivers/DriverInstallService.cs, HpSoftPaqExtractor.cs (through `RestrictedProcessRunner`, 10-minute timeout) and DismDriverInjector.cs (output captured like `ImageApplyService`), working off the UI thread with a 1 MB download buffer (T031 green)
- [X] T042 [US1] Wire the step in src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs after Apply image (descriptor fetch, step shown on 200, final report with items, critical failure stops before Prepare setup and Configure boot through the existing failure path) and add the optional step to src/CloudImaging.Client/ViewModels/ProgressViewModel.cs `ConfigureSteps` (T031 and T031a green)
- [X] T043 [P] [US1] Implement the Models tab, onboarding dialog (Onboard navigates to the first draft's page) and model page with the Draft banner and Complete onboarding (read-only actions for now) in src/cloud-imaging-portal/client/src/pages/DriversPage.tsx, src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx (with its lazy route `/drivers/models/:modelId` in src/cloud-imaging-portal/client/src/App.tsx) and src/cloud-imaging-portal/client/src/components/drivers/ (ModelsTable.tsx, OnboardModelDialog.tsx, ComponentTable.tsx, ComponentDetails.tsx) per contracts/portal-client-surfaces.md sections 2 and 3
- [X] T044 [P] [US1] Add the Windows release `Select` to src/cloud-imaging-portal/client/src/components/ImageEditorDialog.tsx and src/cloud-imaging-portal/client/src/components/ChunkedUploadDialog.tsx and a column to src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx
- [X] T045 [P] [US1] Add `InstallDrivers` to src/cloud-imaging-portal/client/src/lib/imagingProgress.ts and the Install drivers card to src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx with the session's driver pin passed from the sessions page (T032 green)

**Checkpoint**: The MVP works end to end for HP Offline components, x64 and ARM64 models matched by architecture.

---

## Phase 4: User Story 2 - Decide per component what happens, with defaults, rules and overrides (Priority: P1)

**Goal**: Rules, overrides, locked components, the Source column, suggestions, critical categories, and policy changes that take effect without waiting for a sync.

**Independent Test**: With one onboarded model, create a name rule and a model override and confirm the resolved set and Source follow the precedence order, and a locked component stays at Skip (quickstart 4.3 to 4.5).

**Tests first**

- [X] T046 [P] [US2] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverPolicyFunctionsTests.cs: rule create, update and delete with field errors (name 1 to 100 chars unique ignoring case, pattern 1 to 200 chars with `*` only, scope, match kind) and 409 on a duplicate name; hits computed over current sets; suggestions exclude existing rules and are never inserted unasked; override set and undo with 400 for a disallowed action; an override on a draft model enqueues no `BuildSet`; critical categories per vendor; every change writes a change log row and marks the affected pairs dirty with `BuildAfter` 2 minutes after the change and `PendingChanges` counted; five changes within 2 minutes build one set; Build now builds at once and returns 409 when nothing is waiting; a critical category change marks nothing dirty; a dirty pair is re-resolved by its delayed `BuildSet` message (which re-enqueues itself while `BuildAfter` is in the future) without reading catalogs, downloads missing files and switches the set only when they are verified; no category is critical by default
- [X] T047 [P] [US2] Extend tests/cloud-imaging-portal/server/drivers-routes.test.ts and role-route-matrix.test.ts for routes 12, 13, 17 to 23 and 33, and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs for their proxies
- [X] T048 [P] [US2] Create tests/cloud-imaging-portal/client/drivers-rules.test.tsx: Action dropdown lists only allowed actions, locked rows are disabled with the lock tooltip, Source shows Locked, Override with Undo override, rule name over Rule, or Default; Rules tab table, info text, rule dialog validation, Suggested rules dialog; Deployment failure card applies on change; the waiting changes banner shows the count and build time and Build now calls route 33; read-only users see no edit controls. Every control has an accessible name; the Action dropdowns, switches, chevrons and dialogs work with the keyboard and return focus on close; locked and status badges convey their meaning in text, not colour alone

**Implementation**

- [X] T049 [US2] Implement the rule, suggestion, override, category, critical category and Build now endpoints in src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs and the dirty-pair resolution with the `BuildAfter` wait in src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncService.cs (T046 green)
- [X] T050 [US2] Implement routes 12, 13, 17 to 23 and 33 in src/cloud-imaging-portal/server/src/routes/drivers.ts, src/cloud-imaging-portal/server/src/services/operatorApiClient.ts and the matching proxies in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with OpenApiConfig entries (T047 green)
- [X] T051 [US2] Make the Action column editable with override and undo in src/cloud-imaging-portal/client/src/components/drivers/ComponentTable.tsx, add the waiting changes banner with Build now to src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx, and implement the Rules tab in src/cloud-imaging-portal/client/src/components/drivers/RulesTab.tsx, RuleDialog.tsx, SuggestedRulesDialog.tsx and DeploymentFailureCard.tsx (T048 green)

**Checkpoint**: US1 and US2 together are the minimum viable product.

---

## Phase 5: User Story 3 - Install vendor companion apps and installer-only drivers during Specialize (Priority: P2)

**Goal**: Offline + Specialize and Specialize components are staged and handed to Prepare setup; the setup runner installs them before OOBE. HP ARM64 components run as `<sp>.exe /s` during specialize.

**Independent Test**: Deploy a device whose set has one Offline + Specialize and one Specialize component and confirm after OOBE that the companion apps are installed for all users and SetupRunner.log lists each exit code (quickstart 5.3 and 6a.4).

**Tests first**

- [X] T052 [P] [US3] Extend tests/CloudImaging.Client.Tests/Drivers/DriverInstallServiceTests.cs: Offline + Specialize injects and returns a `SpecializeInstaller` whose files are every staged file with its SHA-256 relative to the volume root, command from the descriptor with the staged folder as working directory, timeout 1200 seconds, success codes 0 and 3010, cleanup the component folder; Specialize components are staged without DISM and reported `Queued`; HP ARM64 components are staged unextracted with `"<sp>.exe" /s`; without Specialize work no installer is returned and every folder is deleted
- [X] T053 [P] [US3] Extend tests/CloudImaging.Client.Tests/PrepareSetupServiceTests.cs and ImagingWorkflowViewModelTests.cs: the installers from Install drivers reach Prepare setup, are listed before profile scripts in the manifest, and make Prepare setup run without a setup profile; extend tests/CloudImaging.Client.Tests/SetupRunnerScriptTests.cs to run the real Invoke-CloudImagingSetup.ps1 over a manifest with two driver installers and one profile script: installers run first in order, a changed staged file is refused, exit code 3010 counts as success, a failed installer does not stop the next item, no restart is requested, results go only to the local log, and the staged folders are deleted afterwards, also after the failure

**Implementation**

- [X] T054 [US3] Build `SpecializeInstaller` records in src/CloudImaging.Client/Services/Drivers/DriverInstallService.cs and pass them instead of the empty list in src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs (T052, T053 green)

**Checkpoint**: Companion apps and installer-only components install during specialize.

---

## Phase 6: User Story 4 - Keep driver sets current with a scheduled, observable sync (Priority: P2)

**Goal**: Schedule in a chosen time zone driven by delayed queue messages (no timer), Sync now and Check for updates on the Models tab with their shared limit, a per-model Check for updates, vendor switches, per-vendor isolation, and run history.

**Independent Test**: Configure Selected days at 02:00 in a time zone and confirm exactly one run per slot, Sync now refused within 60 minutes with the time it becomes available, and history rows with items (quickstart 4.1).

**Tests first**

- [X] T055 [P] [US4] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSyncSchedulerTests.cs with an injected clock: Daily (the default, at 02:00), Selected days and Manual; the next slot is computed from the stored schedule and a `ScheduledSync` message is enqueued delayed until it; saving the schedule and a Core start each enqueue one; of several messages for one slot only the one that claims `LastScheduledSlotUtc` (ETag) starts a run and enqueues the next slot, the rest are dropped, so the message count never grows; Manual enqueues nothing and a stale message for a changed schedule starts no run; no run on unselected days; Selected days every 2, 3 and 4 weeks run only in weeks a multiple of the repeat away from `AnchorWeek`, a save rewrites `AnchorWeek` so the first run is the next selected day (saved Sunday 2026-10-25 with Monday every 2 weeks: 2026-10-26 and 2026-11-09, not 2026-11-02), and `nextRunAt` equals the enqueued slot; a slot missed by more than 6 hours is skipped; a slot that passes while a run is still in progress starts no second run; DST gap runs at the next valid minute and DST overlap runs once; every `ianaId` in the embedded time zone list resolves on Linux (the CI runner), and the list holds 142 entries with unique `ianaId` values, including "(UTC) Coordinated Universal Time" for `Etc/UTC`
- [X] T056 [P] [US4] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverSyncFunctionsTests.cs: settings get and set with field errors and immediate effect; the get returns `timeZones` from the embedded list in Windows order with `ianaId`, `displayName` and `covers`, leaving out entries the host cannot resolve; a time zone that is not an `ianaId` in the list (`Europe/Stockholm`, `W. Europe Standard Time`) and Selected days with no day are refused with 400 and leave the stored settings unchanged; `repeatWeeks` 0 or 5 is refused with 400 "Choose a repeat from the list."; get and set return `nextRunAt` (null for Manual); Sync now (route 26, refreshes the model lists) and the all-models check (route 45, components only) share one limit: each returns 409 with `availableAt` while running and 429 with `availableAt` and `Retry-After` within 60 minutes of the last `Scheduled`, `Manual` or `Check` start; vendor switch on and off (Lenovo refused); a run reads only enabled vendors and their onboarded models; a vendor turned off while a run reads its catalog lets that run finish and is skipped by the next run; one vendor's catalog failure leaves the other's changes applied and ends Partly failed; Completed, Partly failed and Failed per FR-037; runs and items older than 90 days are deleted at the end of a run; Check for updates on one model starts a `ModelCheck` run that reads only that model's vendor catalog, enqueues downloads only for components that are not skipped, returns 409 for a draft, returns 409 with `availableAt` while any run is active (one run at a time) and 429 with `availableAt` and `Retry-After` within 15 minutes of the model's last check, and does not move the 60-minute all-models limit; clean-up runs at the end of `Scheduled`, `Manual` and `Check` runs and not after a `ModelCheck`; Surface is never offered as a vendor, and every catalog and file request goes to a published catalog URL on the vendor host allowlist (no web pages are read); a fake third `IDriverVendorAdapter` registered in the test host flows through policy resolution, sync, storage and matching with no change outside the adapter; Check for updates on a model whose vendor has Sync off returns 409 "Sync is off for HP." and starts no run; the model page returns `checkBlockedReason`; a vendor switch and every schedule change write a `Vendor` or `Sync` change log row with the actor and time
- [X] T057 [P] [US4] Extend tests/cloud-imaging-portal/server/drivers-routes.test.ts, role-route-matrix.test.ts and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs for routes 1, 2, 24, 25 and 28 and the 409 and 429 relay of routes 26, 44 and 45
- [X] T058 [P] [US4] Create tests/cloud-imaging-portal/client/drivers-sync.test.tsx: schedule controls apply on change with no Save button, the time zone dropdown lists the `timeZones` from the settings response by Windows display name ("(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna") with search, and a browser reporting `Europe/Stockholm` gets that entry first under "Your browser" and preselected; a browser zone no entry covers falls back to `Etc/UTC`, clearing every day shows "Choose at least one day." and saves nothing, the repeat select (Every week to Every 4 weeks) shows only for Selected days, the next-sync line renders `nextRunAt` in the schedule's time zone with the repeat ("then every day.", "then every week on Monday to Friday.", "then every week on Monday, Wednesday and Friday.", "then every 2 weeks on Monday.") and is absent for Manually only, and the day error replaces that line without changing the card height, browser time zone default, the Sync now row (inset divider above, full-width divider below) whose Sync button calls route 26, the Models tab's Check for updates calls route 45, both show "Last checked" with the last run start and are disabled while running and until `syncNowAvailableAt` with the time in their tooltip, Vendors card rows with switches and Lenovo disabled, "Sync off" badge on models of a vendor that is off, History rows with result badge, generic summary (a model check names the model first; a sync that added models shows "HP: 3 new models available"), chevron only with items, item lines linking to the model page row; the model page's Check for updates calls route 44, shows the toast, and is disabled until `checkAvailableAt` with "Available at 14:32". Every control has an accessible name; switches, day toggles, chevrons and dialogs work with the keyboard and return focus on close; result badges convey their meaning in text, not colour alone; the model page's Check for updates is disabled with "Sync is off for HP." when the vendor is off and with "A sync is running." while any run is active

**Implementation**

- [X] T059 [US4] Copy specs/005-os-driver-management/contracts/time-zones.json to src/CloudImaging.ImagingCoreApi/Data/time-zones.json as an embedded resource with a `DriverTimeZoneCatalog` that loads it once and filters by `TimeZoneInfo`; implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncScheduler.cs (next slot, `ScheduledSync` enqueue on save and at Core start, slot claim) and the `ScheduledSync` handler in `DriverWorkProcessor`; no timer trigger (T055 green)
- [X] T060 [US4] Implement settings, vendor, Sync now and Check for updates on all models (`CheckAllDriverModelUpdates`, `Check` runs that skip the model lists) with their shared limit, Check for updates on one model (`CheckDriverModelUpdates`, `ModelCheck` runs, `LastCheckedAt`), run history and retention in src/CloudImaging.ImagingCoreApi/Functions/DriverSyncFunctions.cs and src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncService.cs (T056 green)
- [X] T061 [US4] Implement routes 1, 2, 24, 25, 28, 44 and 45 in src/cloud-imaging-portal/server/src/routes/drivers.ts and operatorApiClient.ts, and the proxies in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with OpenApiConfig entries (T057 green)
- [X] T062 [US4] Implement the Sync tab in src/cloud-imaging-portal/client/src/components/drivers/SyncTab.tsx, VendorsCard.tsx and SyncHistory.tsx, the "Sync off" status, the last check time and Check for updates in the Models tab toolbar in ModelsTable.tsx and src/cloud-imaging-portal/client/src/pages/DriversPage.tsx, and Check for updates in src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx (T058 green)

**Checkpoint**: Driver sets stay current on a schedule, and every run is visible.

---

## Phase 7: User Story 5 - Approve new components before they reach devices (Priority: P2)

**Goal**: New lineages wait for approval when the setting is on; newer versions of known components never wait.

**Independent Test**: Turn on Wait for approval, introduce a new component and a newer version of a known one, and confirm only the new one is held until approved, including as Skip (quickstart 4.6).

**Tests first**

- [X] T063 [P] [US5] Extend tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSyncServiceTests.cs and create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverApprovalFunctionsTests.cs: with the setting on a new lineage is `WaitingForApproval`, excluded from the set, counted in the model status, and does not change the run result ("1 new" Completed); a newer version keeps its action and never waits; Approve confirms the current action including Skip and rebuilds the set; with the setting off new components enter the set and are recorded as new; every approval writes an `Approval` change log row with the actor and time
- [X] T064 [P] [US5] Extend tests/cloud-imaging-portal/client/drivers-models.test.tsx: amber New badge, approval banner text, Approve per row, Status badge count; and tests/cloud-imaging-portal/server/drivers-routes.test.ts for route 14. Approve buttons have accessible names naming the component; the New badge conveys its meaning in text, not colour alone

**Implementation**

- [X] T065 [US5] Implement component states and the approval endpoint in src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncService.cs and src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs, route 14 in src/cloud-imaging-portal/server/src/routes/drivers.ts and the proxy in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs (T063 green)
- [X] T066 [US5] Add the New badge, banner and Approve button to src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx and src/cloud-imaging-portal/client/src/components/drivers/ComponentTable.tsx (T064 green)

**Checkpoint**: No unexpected software reaches devices while routine updates still flow.

---

## Phase 8: User Story 6 - Dell models with hardware-matched downloads (Priority: P3)

**Goal**: Dell models (x64 and ARM64) onboard, sync, match on system SKU, and download only components for hardware the device has.

**Independent Test**: Onboard one Dell model, deploy a device without optional hardware, and confirm only matching components were downloaded and 32-bit packages never ran in WinPE (quickstart sections 6 and 6a.3).

**Tests first**

- [X] T067 [P] [US6] Create tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DellDriverVendorAdapterTests.cs over the fixtures: index entries grouped by model name with all SKUs and their catalog path and SHA-256 (a per-SKU catalog whose SHA-256 differs is refused); components of all SKUs merged by lineage (`componentID`, else normalised name), newest per lineage; Windows filter drops Windows 10-only components; `FRMW`, `BIOS` and "Firmware" names excluded; classification rows of contracts/vendor-catalogs.md section 2; `LWXP` never offers Offline; ARM64 from `osArch` (Latitude 0C85); PCI IDs parsed from `SupportedDCHDevices` and `SupportedDevices`
- [X] T068 [P] [US6] Extend tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSetResolverTests.cs for Dell matching on `SystemSku` across all SKUs of a model
- [X] T069 [P] [US6] Create tests/CloudImaging.Client.Tests/Drivers/PciInventoryTests.cs and extend DriverInstallServiceTests.cs: hardware IDs parse vendor, device and subsystem; a component matches when vendor and device match and, if listed, subsystem; no match is `Skipped` with "No matching hardware in this device." and never downloaded; components without PCI IDs are processed; Dell extraction runs `/s /drivers="<x>"`; `LWXP` Specialize stages the package with `"<file>" /s`; and extend tests/CloudImaging.Client.Tests for `SystemSku` collection and its copy in `DeviceGatewayApiClient.SignPayload`
- [X] T070 [P] [US6] Extend tests/cloud-imaging-portal/client/drivers-models.test.tsx for the Dell detail fields (Installer, Hardware match, Package type, Criticality) and the vendor filter, and assert that a Dell model page, its dialogs and tooltips say "Dell" and never "HP"

**Implementation**

- [X] T071 [US6] Implement src/CloudImaging.ImagingCoreApi/Services/Drivers/DellDriverVendorAdapter.cs (UTF-16 namespaced `XmlReader`) and register it (T067 green)
- [X] T072 [US6] Add Dell matching to src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetResolver.cs (T068 green)
- [X] T073 [US6] Collect `Win32_ComputerSystem.SystemSKUNumber` in src/CloudImaging.Client/ViewModels/OperationSelectionViewModel.cs, copy it in `SignPayload` in src/CloudImaging.Client/Services/DeviceGatewayApiClient.cs, and implement src/CloudImaging.Client/Services/Drivers/PciInventory.cs and DellPackageExtractor.cs with the hardware match in DriverInstallService.cs (T069 green)
- [X] T074 [US6] Add the Dell detail fields to src/cloud-imaging-portal/client/src/components/drivers/ComponentDetails.tsx (T070 green)

**Checkpoint**: Both vendors work through the same policy, sync, storage and Client phases.

---

## Phase 9: User Story 7 - Understand storage, roll back and remove models (Priority: P3)

**Goal**: Storage tab, roll back, removal of models and releases with an impact overlay, removal of withdrawn components, and automatic clean-up.

**Independent Test**: With two models sharing a component, the Storage tab counts it once; roll one back and new sessions get the previous set; remove a model and the overlay's freed space matches what the next clean-up deletes (quickstart 4.7 and 4.8).

**Tests first**

- [X] T075 [P] [US7] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverLifecycleFunctionsTests.cs: storage totals count shared files once, per vendor and per model `sizeBytes` counts each file once; the storage model endpoint returns per-release blocks with the current and roll-back set, and per component its version, size, sets (or next), shared-with count, status and downloaded time; the downloads endpoint returns sets in progress with component and byte counts and status, and builds ready in the last 7 days; roll back swaps current and previous and a later sync with the rolled-back fingerprint creates no set, 409 without a previous set; removal impact numbers; removing a model or a release takes effect for new assignments at once, keeps rules and history, deletes sets and overrides, refuses removing the last release (409); a withdrawn component can be removed (409 when not withdrawn); clean-up keeps files of pinned sets until their sessions end; clean-up keeps the files of the `PreviousVersionsToKeep` versions directly before each vendor component's current one (default 1, so after 5382 is replaced by 5592 the 5382 file stays and 5186 goes), custom components do not count, 0 keeps none, raising the number downloads nothing, lowering it frees files at the next clean-up; `PUT storage/settings` accepts 0 to 3 and returns 400 otherwise; `GET storage` returns `previousVersionsToKeep` and `previousVersionsBytes`, and the storage model endpoint marks a kept previous version `previous`; `previousVersionsBytes` counts each file once and only files no other reference keeps; changing the setting writes a `Storage` change log row ("Previous versions", "1 to 2"); removing a model and onboarding it again starts from the current catalog with none of its earlier overrides; removing a model or release and rolling back a driver set each write a `Model`, `Release` or `RollBack` change log row with the actor and time
- [X] T076 [P] [US7] Extend tests/cloud-imaging-portal/server/drivers-routes.test.ts, role-route-matrix.test.ts and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs for routes 9 to 11, 15, 16, 29, 31, 32 and 46
- [X] T077 [P] [US7] Create tests/cloud-imaging-portal/client/drivers-storage.test.tsx and drivers-downloads.test.tsx: Storage tab tiles, info text and table with Driver sets ("2 current, 2 roll-back") and Size and no Freed if removed; the Previous versions row with its help, "Uses 1.4 GB now" from `previousVersionsBytes`, the select (Keep none to Keep 3 previous versions, default 1) calling route 46 with a toast, disabled for read-only users; an expanded model shows one block per release with both sets and build times, and the component table with Driver sets pills, Shared with, Status as icon and label (Failed reason in the icon tooltip) and Downloaded; Downloads tab count in the tab label, In progress rows with progress bar and component counts, the empty state with the table header kept, Finished in the last 7 days; Roll back driver set and Remove model overlays with their impact, details and confirm labels; release add and remove; Withdrawn badge and Remove in the details. Every control has an accessible name; overlays trap and return focus; the Withdrawn badge conveys its meaning in text, not colour alone

**Implementation**

- [X] T078 [US7] Implement roll back, removal impact, model and release removal, removal of withdrawn components, storage, the storage settings (`PreviousVersionsToKeep`, kept previous versions in clean-up), the storage model endpoint and the downloads endpoint in src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs and src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetBuilder.cs (T075 green)
- [X] T079 [US7] Implement routes 9 to 11, 15, 16, 29, 31, 32 and 46 in src/cloud-imaging-portal/server/src/routes/drivers.ts and operatorApiClient.ts, and the proxies in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with OpenApiConfig entries (T076 green)
- [X] T080 [US7] Implement src/cloud-imaging-portal/client/src/components/drivers/StorageTab.tsx (Previous versions setting row and expandable model rows per contracts/portal-client-surfaces.md section 7), src/cloud-imaging-portal/client/src/components/drivers/DownloadsTab.tsx (section 6, with the tab count in DriversPage.tsx), and the Roll back driver set (header button, disabled without a previous set), Remove model (header button), release and withdrawn actions in src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx (T077 green)

**Checkpoint**: Every user story is independently functional.

---

## Phase 10: User Story 8 - Check and correct the driver set before imaging starts (Priority: P2)

**Goal**: The Coupled Devices table shows the matched model per device and lets anyone who can start imaging pick another model or no drivers; the session pins the chosen model's current set and records who chose it.

**Independent Test**: With two onboarded models, couple a matching and an unmatched device, choose a model for the unmatched one and No drivers for the other, start imaging, and confirm the pins and the "Chosen manually by" line (quickstart 5.6 and 5.7).

**Tests first**

- [X] T087 [P] [US8] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverAssignmentPreviewTests.cs: per assignable session the matched model or the skip reason with `matchKey`; options are the models with a current set for the image's release and architecture, ordered by vendor then name, with `maxDownloadBytes`; an image without a release returns `release: null` and no options; unassignable and unknown sessions are left out; 404 unknown image; 400 over 200 session IDs; nothing is written; a draft model is never matched or offered
- [X] T088 [P] [US8] Extend tests/CloudImaging.ImagingCoreApi.Tests/Integration/BulkAssignIntegrationTests.cs and tests/CloudImaging.ImagingCoreApi.Tests/Services/Drivers/DriverSetResolverTests.cs: a `Model` choice pins the chosen model's set that is current at assignment (also after a sync between preview and start); `None` pins `NoneChosen`; both set `DriverChosenManually` and `DriverChosenBy`; a removed model, a model without a current set for the release, or another architecture returns 409 with `errors` per session and assigns nothing; a choice for a session not in the request, two choices for one session, `Model` without `modelId`, or a choice without an actor return 400; sessions without a choice keep the automatic match; the single-session assign applies the same rules
- [X] T089 [P] [US8] Extend tests/CloudImaging.OperatorApi.Tests/Contracts/BulkAssignContractTests.cs (driverChoices and actor forwarded unchanged, 409 body relayed) and DriverContractTests.cs (assignment preview proxied)
- [X] T090 [P] [US8] Extend tests/cloud-imaging-portal/server/session-bulk-assign.test.ts, drivers-routes.test.ts and role-route-matrix.test.ts: choice shape errors return 400 without an upstream call; only `sessionId`, `kind` and `modelId` are forwarded; `actor` comes from the token and a browser-sent actor is discarded; no actor is added without choices; 401 without an identity when choices are present; the 409 with `errors` is relayed; route 30 for Technician, Operator and Administrator, 403 for Reader, Autopilot Approver and no role
- [X] T091 [P] [US8] Create tests/cloud-imaging-portal/client/driver-set-column.test.tsx and extend session-assign-flow.test.tsx, bulk-assignment-flow.test.tsx, session-progress-details.test.tsx and select.test.tsx: column hidden when nothing is onboarded; "Choose an OS image first" and "The OS image has no Windows release"; matched model preselected; no match shows the placeholder, amber border and the alert tooltip naming the match key; options grouped by vendor with the matched model first, sizes, No drivers last, no set versions; a change shows the undo button and no badge, undo restores the match; choices cleared when the OS image changes; only differing rows are sent; the overlay lists driver lines with "(chosen manually)"; a 409 shows the detail, refetches and resets the affected rows; a preview error shows Retry and Start imaging still works; a Technician can change a row; the timeline shows "Chosen manually by <upn>"; `Select` group headings are skipped by arrow keys and type-ahead and `meta` text is not matched. Every `Select` has the accessible name "Driver set for <serial>"; the alert icon and undo button are keyboard reachable and named
- [X] T092 [P] [US8] Extend tests/CloudImaging.Client.Tests/Drivers/DriverInstallServiceTests.cs: `NoneChosen` reports the step `Completed` with no warning and the note "No drivers were chosen for this device when imaging started."

**Implementation**

- [X] T093 [US8] Implement `PreviewDriverAssignment` in src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs over a side-effect-free match method of src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetResolver.cs, leaving out draft models (T087 green)
- [X] T094 [US8] Accept `driverChoices` in src/CloudImaging.ImagingCoreApi/Functions/BulkAssignFunction.cs and `driverChoice` in src/CloudImaging.ImagingCoreApi/Functions/AssignSessionFunction.cs, validate every choice before assigning any session in src/CloudImaging.ImagingCoreApi/Services/BulkAssignmentService.cs (next to the architecture check), apply choices in DriverSetResolver.cs and persist the new session fields in src/CloudImaging.ImagingCoreApi/Repositories/DeviceSessionRepository.cs (T088 green)
- [X] T095 [US8] Add the assignment preview proxy and OpenApiConfig entry to src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs; confirm src/CloudImaging.OperatorApi/Functions/BulkAssignFunction.cs and AssignSessionFunction.cs forward the body unchanged (T089 green)
- [X] T096 [US8] Validate and forward choices with the token's actor in src/cloud-imaging-portal/server/src/routes/sessions.ts and `assignSession`/`bulkAssign` in src/cloud-imaging-portal/server/src/services/operatorApiClient.ts; add route 30 to src/cloud-imaging-portal/server/src/routes/drivers.ts (T090 green)
- [X] T097 [P] [US8] Add optional `group` and `meta` to `SelectOption` in src/cloud-imaging-portal/client/src/components/ui/select.tsx without changing existing call sites (select.test.tsx green)
- [X] T098 [US8] Implement src/cloud-imaging-portal/client/src/components/drivers/DriverSetCell.tsx, `useDriverAssignmentPreview` and `formatBytes` in src/cloud-imaging-portal/client/src/lib/drivers.ts, the Driver set column, choice state, `StartImagingDriverLines` and the choices on Start imaging in src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx, and the "Chosen manually by" line in src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx per contracts/portal-client-surfaces.md sections 9 and 10 (T091 green)
- [X] T099 [US8] Add the `NoneChosen` note without a warning to src/CloudImaging.Client/Services/Drivers/DriverInstallService.cs (T092 green)

**Checkpoint**: A wrong or missing match is visible and correctable before imaging starts.

---

## Phase 11: User Story 9 - Roll back one component without undoing the others (Priority: P2)

**Goal**: Every component keeps its version history; an Operator rolls one component back to an earlier version, by default until the vendor publishes a newer one, while every other component keeps updating.

**Independent Test**: Roll back one of two updated components to a version no longer stored and confirm the next set has the older version of that one (downloaded again and verified) and the newer version of the other, then simulate a newer vendor version and confirm the component moves forward (quickstart 4.12).

**Tests first**

- [X] T100 [P] [US9] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverComponentHistoryTests.cs: each new component version adds a history row with its driver sets, from and to dates and what changed it; the versions endpoint returns them with `status` `Stored`, `DownloadsOnRollBack` or `Unavailable` and their size; with `check=true` it sends a HEAD to each recorded URL that is not stored (allowlisted hosts only, 10-second timeout, a timeout counts as unavailable), records `Available` and `AvailabilityCheckedAt`, checks nothing that is stored, and the route passes `check=true` only for Operator and Administrator; Roll back component with `NewerVersion` makes the next build use the held version while other components update, and a sync that finds a version newer than `SkippedVersion` ends it; `Undone` holds until the DELETE; a version that is not stored is downloaded again through the host allowlist and verified before a set uses it, and a failed download returns 409 with the reason and writes nothing; 400 for the current or an unknown version; clean-up keeps the held version's file; every change writes a change log row and marks the pair dirty
- [X] T101 [P] [US9] Extend tests/cloud-imaging-portal/server/drivers-routes.test.ts, role-route-matrix.test.ts and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs for routes 34 to 36
- [X] T102 [P] [US9] Create tests/cloud-imaging-portal/client/drivers-component-history.test.tsx: the expanded row shows the read-only Version history (Version, Driver sets, From, To, Changed by, Status as icon and label: Stored, Downloads on roll back, No longer available from <vendor>) with one Roll back component button; the dialog loads the versions with `check=true`, shows "Checking <vendor>" until they return, lists only earlier versions with "Stored", "Downloads <size>" or a disabled "No longer available from <vendor>", preselects the newest available one, shows the stored note or the download note to match, defaults Until to the newer-version option, names the component's vendor through `vendorLabel` in the not-stored note, and shows a 409 reason without closing; the Source column shows Rolled back with who, the skipped version and when it moves forward in the icon tooltip; Undo roll back; read-only users see neither button. The dialog's controls have accessible names, the radio group is keyboard operable and focus returns on close

**Implementation**

- [X] T103 [US9] Write `DriverComponentVersions` rows in src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSetBuilder.cs, apply `DriverRollBacks` after policy resolution, end `NewerVersion` roll backs in src/CloudImaging.ImagingCoreApi/Services/Drivers/DriverSyncService.cs, and add the versions (with the `check=true` availability check) and roll back endpoints to src/CloudImaging.ImagingCoreApi/Functions/DriverFunctions.cs with the re-download through DriverStorageService.cs (T100 green)
- [X] T104 [US9] Implement routes 34 to 36 in src/cloud-imaging-portal/server/src/routes/drivers.ts and operatorApiClient.ts, and the proxies in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with OpenApiConfig entries (T101 green)
- [X] T105 [US9] Add the Version history and Roll back component button to src/cloud-imaging-portal/client/src/components/drivers/ComponentDetails.tsx, create src/cloud-imaging-portal/client/src/components/drivers/RollBackComponentDialog.tsx, and add the Rolled back source to ComponentTable.tsx (T102 green)

**Checkpoint**: A bad update to one component is undone without touching the others.

---

## Phase 12: User Story 10 - Use a driver the vendor has not published yet (Priority: P3)

**Goal**: An Operator uploads a custom component for one or more models, optionally replacing a vendor component, until removing it; the portal says when the vendor publishes a new version of the replaced component.

**Independent Test**: Upload a signed driver ZIP replacing a vendor component on one model, deploy a device and confirm the custom driver is installed and the replaced one is not, then simulate a new vendor version and confirm the banner and that removing the custom component restores the vendor component (quickstart 4.13 and 5.8).

**Tests first**

- [X] T106 [P] [US10] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/DriverCustomComponentTests.cs over fixtures in tests/fixtures/drivers/custom/ (a ZIP with INF files, a ZIP without, an installer): upload start refuses over 5 GB and other extensions; complete commits the staging blob, returns the SHA-256 and the `.inf` count from the central directory; create refuses Offline without INF, Specialize without a command, unknown models and a second replacement of one lineage on a model (409 naming the other); each target pair is marked dirty; the next set holds the custom component with origin `Custom` and the replaced lineage is Skip with source `ReplacedByCustom`; deleting restores the vendor component; clean-up keeps custom blobs while the component or its history refers to them; a sync that finds a new version of a replaced lineage sets the notice and dismiss records who dismissed it
- [X] T107 [P] [US10] Extend tests/cloud-imaging-portal/server/drivers-routes.test.ts, role-route-matrix.test.ts and tests/CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs for routes 37, 37a, 37b and 38 to 42
- [X] T108 [P] [US10] Extend tests/CloudImaging.Client.Tests/Drivers/DriverInstallServiceTests.cs: `Zip` components are unpacked in process, an entry whose path leaves the component folder fails the component without writing outside it, Offline custom drivers are injected with DISM without `/ForceUnsigned`, Specialize custom installers are staged with their command, and items report origin `Custom`
- [X] T109 [P] [US10] Create tests/cloud-imaging-portal/client/drivers-custom.test.tsx: Add custom component form fields and help texts, upload progress and the "INF files found" line, field errors from Core under their fields, Replaces lists the vendor components the selected models share; Source Custom with the icon tooltip and Replaced by custom with the action locked at Skip; the notice banner names the vendor through `vendorLabel`, Dismiss calls route 42, Remove custom component opens the impact overlay. Form controls have labels, the dialog traps and returns focus

**Implementation**

- [X] T110 [US10] Implement src/CloudImaging.ImagingCoreApi/Functions/DriverCustomComponentFunctions.cs (staged upload start, complete and abandon following OsImageUploadFunctions.cs, create, list, update, delete, dismiss), the custom component and replacement handling in DriverPolicyResolver.cs and DriverSetBuilder.cs, the notice in DriverSyncService.cs, and the clean-up rule (T106 green)
- [X] T111 [US10] Implement routes 37, 37a, 37b and 38 to 42 in src/cloud-imaging-portal/server/src/routes/drivers.ts and operatorApiClient.ts, and the proxies in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs with OpenApiConfig entries (T107 green)
- [X] T112 [US10] Implement src/CloudImaging.Client/Services/Drivers/CustomZipExtractor.cs and the `Zip` and custom installer branches in DriverInstallService.cs (T108 green)
- [X] T113 [US10] Implement src/cloud-imaging-portal/client/src/components/drivers/AddCustomComponentDialog.tsx (reusing the OS image staged upload helper), the Custom and Replaced by custom sources in ComponentTable.tsx, the custom fields in ComponentDetails.tsx, and the notice banner and Remove custom component overlay in src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx (T109 green)

**Checkpoint**: A driver the vendor has not published yet can be deployed, and the portal says when the vendor catches up.

---

## Phase 13: User Story 11 - Reuse and preload drivers on the USB drive (Priority: P2)

**Goal**: The Client keeps verified driver files in the USB drive's CACHE partition and reuses them; Media Builder can preload chosen driver sets when preparing a drive.

**Independent Test**: Image two devices of one model with the same USB drive and confirm the second downloads no driver files and its timeline says they came from the USB drive; prepare a drive with one model's set in Media Builder and confirm the first deployment of that model downloads nothing (quickstart 5.9 and 5.10).

**Tests first**

- [X] T114 [P] [US11] Create tests/CloudImaging.Client.Tests/Drivers/DriverFileCacheTests.cs over a temporary CACHE folder: a verified download is written under `drivers\<sha256>\` with `metadata.json` last; a hit is copied to the component folder and hashed there; a copy that fails the check deletes the cache entry and reports a miss; lookups still work when writes are disabled below 500 MB free; entries without metadata are removed when the step starts; nothing is evicted during a step; no CACHE volume means no reads and no writes; and extend ImageCacheMaintenanceServiceTests.cs so the 30-day idle purge covers `drivers\`
- [X] T115 [P] [US11] Extend tests/CloudImaging.Client.Tests/Drivers/DriverInstallServiceTests.cs: the hardware match runs before the cache lookup; hits report `fromUsbCache` and the "Copying drivers from the USB drive (n of m)" status line; misses download and then cache; a failed cache write never fails the component; each item reports whether its file came from the USB drive or was downloaded
- [X] T116 [P] [US11] Create tests/CloudImaging.ImagingCoreApi.Tests/Functions/MediaBuilderDriverSetFunctionsTests.cs (sets per architecture, links deduplicated by SHA-256 with one 60-minute SAS each, custom packages included, at most 20 sets, 404 for unknown pairs, nothing written) and tests/CloudImaging.OperatorApi.Tests/Contracts/MediaBuilderDriverSetContractTests.cs (MediaBuilderAccess allowed, PortalAccess refused, body and status relayed)
- [X] T117 [P] [US11] Create tests/CloudImaging.MediaBuilder.Tests/DriverSetPreloadTests.cs: the section lists sets for the boot image's architecture with vendor names from the response; sizes count shared files once; the 20 GB warning and the does-not-fit error that disables Prepare; files already in the local cache are not downloaded again; a failed download stops before partitioning with its message; the elevated worker copies into `CACHE:\CloudImagingCache\drivers\<sha256>\` with metadata last and fails on a hash mismatch; the manifest records `DriverSets`; the failed-download and does-not-fit errors show a support reference code and the fix; downloads and copies run off the UI thread with no dispatcher block over 100 ms; each `DriverSets` entry records model, Windows release, set version, component count and bytes; preloading pins nothing, and an assignment after preloading still pins the set chosen at assignment
- [X] T118 [P] [US11] Extend tests/cloud-imaging-portal/client/session-progress-details.test.tsx: the Install drivers summary adds "24 from the USB drive, 2 downloaded" and items show "From the USB drive" when `fromUsbCache` is set, and nothing when no item has it

**Implementation**

- [X] T119 [US11] Implement src/CloudImaging.Client/Services/Drivers/DriverFileCache.cs, use it in DriverInstallService.cs, and extend src/CloudImaging.Client/Services/ImageCacheMaintenanceService.cs to purge `drivers\` (T114, T115 green)
- [X] T120 [US11] Implement the Media Builder endpoints in src/CloudImaging.ImagingCoreApi/Functions/MediaBuilderDriverSetFunctions.cs and the proxies `GetDriverSetsForMediaBuilder` and `GetDriverSetLinksForMediaBuilder` in src/CloudImaging.OperatorApi/Functions/DriverFunctions.cs, added to `MediaBuilderAllowedFunctions` and `MediaBuilderOnlyFunctions` in src/CloudImaging.OperatorApi/Middleware/AppRoleAuthorizationMiddleware.cs with OpenApiConfig entries (T116 green)
- [X] T121 [US11] Implement the Driver sets section in src/CloudImaging.MediaBuilder/Views/PrepareStorageDeviceView.xaml and ViewModels/PrepareStorageDeviceViewModel.cs, src/CloudImaging.MediaBuilder/Services/DriverSetDownloadService.cs, the driver calls in Services/OperatorApiClient.cs, `DriverFiles` in `PreparationParams` and the copy in the elevated worker of Services/UsbPreparationService.cs, and `DriverSets` in src/CloudImaging.Contracts/Models/UsbPreparationManifest.cs per contracts/media-builder-driver-sets.md section 2 (T117 green)
- [X] T122 [US11] Add the USB drive summary and item line to src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx (T118 green)

**Checkpoint**: Driver payloads come from the USB drive whenever the same files were used or preloaded before.

---

## Phase 14: Polish & Cross-Cutting Concerns

- [X] T081 [P] Write docs/drivers.md (onboarding, actions, rules, approvals, sync, the Downloads and Storage tabs, roll back of a driver set and of one component, custom components and signing, driver files on the USB drive and preloading driver sets in Media Builder, ARM64 behaviour, choosing a device's driver set when imaging starts, the vendor hosts Imaging Core must reach, and that holding sets for test deployments is planned for a later version) and update docs/session-lifecycle.md (Install drivers step), docs/troubleshooting-index.md (skip notes, critical failures, vendor host unreachable), docs/roles-and-access.md (Technicians may choose a device's driver set when starting imaging) and docs/SUMMARY.md; no em dashes, no identifiers
- [X] T082 [P] Add the feature to the next backend and client release notes under .github/release-notes/
- [ ] T083 Run the quickstart section 1 gates over every changed file: Release build with zero warnings, all .NET and portal suites, `dotnet format --verify-no-changes`, lint, `tsc --noEmit`, coverage at least 80% on new code, and the text scan for the em dash and spec identifiers outside comments; scan src/cloud-imaging-portal/client/src/components/drivers/ and pages/Driver*.tsx for the literals "HP" and "Dell" in rendered strings (vendor names come from `vendorLabel`); extend tests/cloud-imaging-portal/client/accessibility/accessibility-a11y.test.tsx to cover the Drivers page tabs including Downloads, the model page with its banners, the onboarding, Roll back component and Add custom component dialogs and the Driver set column with no serious or critical violations. Constitution gates: every new test method is named `MethodOrScenario_StateUnderTest_ExpectedBehavior`; the text scan also refuses smart quotes and every other non-ASCII character in user-facing strings; scan src/CloudImaging.ImagingCoreApi/ for vendor names written into API messages, errors or logs instead of the adapter's display name; compare the quickstart section 7 results with the recorded baseline and block merge on a regression above 10%; confirm the real-endpoint CI coverage: Blob storage (T010a), vendor hosts (T084), Entra-authenticated Operator API and Device Gateway (T084a); the pull request has one approving review by another engineer before merge
- [ ] T084 In staging, run quickstart section 3 (Core downloads from HP and Dell hosts with matching hashes, live catalogs parse, Function RSS under 256 MB) and record the results in specs/005-os-driver-management/quickstart.md
- [ ] T084a Extend tests/validation/staging-smoke-test.ps1 with an `-IncludeDrivers` switch that, against the deployed staging resource group with the CI's short-lived identity, onboards one HP model for Windows 11, version 24H2 through the Operator API, completes onboarding, waits for driver set 1 to be built with every file `Verified`, creates, couples and assigns a session with src/deploy/scripts/simulate-device-session.ps1, asserts the assignment pins set 1 and that every link the Device Gateway returns downloads with its SHA-256, then removes the model and asserts the next clean-up leaves none of its files; exits non-zero on any failure; run it with `-IncludeDrivers` in the staging job of .github/workflows/ci.yml (T010b) so it gates merge to main (quickstart section 3, step 4)
- [ ] T085 Manual: quickstart sections 4 to 6a on HP x64, HP ARM64, Dell x64 and Dell ARM64 devices with x64 and ARM64 boot images
- [ ] T086 Manual: quickstart sections 7 and 8 (performance budgets including onboarding with preview in under 3 minutes, a cached preview under 300 ms p95, every Drivers GET route under 300 ms p95 at 50 users, Client and Media Builder startup under 5 seconds, Imaging Core cold start under 3 seconds, Media Builder working set under 512 MB while preloading, the main-branch baseline for the 10% regression gate, correcting an unmatched device in under 30 seconds, and an end-to-end deployment without drivers under 15 minutes as the constitution's reference workload, plus the hardware validation items of research R23 and R24, rolling back one component in under one minute (SC-013), and download progress on the Downloads tab within one minute of the event that starts it (SC-014)), recording each result in specs/005-os-driver-management/quickstart.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on Setup; blocks every user story.
- **US1 (Phase 3)**: depends on Foundational. MVP.
- **US2 (Phase 4)**: depends on Foundational; uses the model page from US1 (T043) for its UI.
- **US3 (Phase 5)**: depends on US1's Client step (T041, T042); spec 003's Prepare setup is already in place.
- **US4 (Phase 6)**: depends on US1's sync service (T033).
- **US5 (Phase 7)**: depends on US1's sync service (T033) and model page (T043).
- **US6 (Phase 8)**: depends on Foundational; its Client part depends on T041.
- **US7 (Phase 9)**: depends on US1 (sets, clean-up, model page).
- **US8 (Phase 10)**: depends on US1's resolver and assignment pin (T035), descriptor (T036) and Install drivers card (T045).
- **US9 (Phase 11)**: depends on US1's set builder and sync service (T021, T033) and US2's editable component table (T051).
- **US10 (Phase 12)**: depends on US1's set builder, sync service and Client step (T021, T033, T041) and US2's component table (T051); its upload reuses the OS image staged upload.
- **US11 (Phase 13)**: its Client cache depends on US1's Client step (T041); the Media Builder preload depends on US1's sets (T021) and is independent of the portal stories.
- **Polish (Phase 14)**: after the stories in scope.

### User Story Dependencies

```text
Setup -> Foundational -> US1 -> US2 (MVP complete)
                          |-> US3
                          |-> US4 -> (history UI)
                          |-> US5
                          |-> US7
                          |-> US8
          US1 + US2 -> US9
          US1 + US2 -> US10
          US1 Client step -> US11
          Foundational -> US6 (adapter) -> US1 Client step -> US6 Client part
```

### Within Each Story

- Test tasks first; they must fail before implementation.
- Core before Operator API and Gateway relays, relays before portal server routes, routes before portal UI.
- Client step after the descriptor endpoint.

### Parallel Opportunities

- Phase 1: T002 to T006 in parallel after T001.
- Phase 2: all test tasks T007 to T015 in parallel; T020 and T021 in parallel; T016 to T019 sequential only where they share Program.cs registration.
- Each story: its test tasks in parallel; UI tasks marked [P] in parallel with backend work once routes are agreed.
- After US1: US2, US3, US4, US5, US7 and US8 can proceed in parallel by different people; US6 can start right after Foundational; US9 and US10 follow US2.

---

## Parallel Example: User Story 1

```text
Tests together:   T025, T026, T027, T028, T029, T030, T031, T032
Backend then:     T033 -> T034 -> T035 -> T036 -> T037; T038 alongside
Portal together:  T043, T044, T045 (after T039)
Client:           T040 -> T041 -> T042
```

---

## Implementation Strategy

### MVP First (US1 and US2)

1. Phases 1 and 2.
2. US1: onboard, sync, pin, inject Offline components, timeline.
3. US2: rules, overrides, locked components, critical categories.
4. Stop and validate with quickstart sections 4.1 to 4.5 and 5.1 to 5.4 on an HP device.

### Incremental Delivery

1. MVP (US1, US2).
2. US3 (Specialize) and US4 (scheduled sync) next: both P2 and independent.
3. US5 (approvals).
4. US6 (Dell), US7 (storage, downloads, roll back, removal) and US8 (driver set choice when imaging starts).
5. US9 (component roll back), then US10 (custom components); US11 (USB drive cache and preload) alongside.
6. Polish and hardware validation.

### Notes

- [P] tasks touch different files and do not depend on an unfinished task.
- Commit after each task or logical group; stop at any checkpoint to validate the story on its own.
- No identifiers in user-facing text, logs or API responses; code comments only.

## Phase 15: Convergence

- [X] T123 CRITICAL: Replace the non-ASCII ellipsis in the new "Installing drivers" status message with ASCII "..." in src/CloudImaging.Client/ViewModels/ImagingWorkflowViewModel.cs, and update any Client test that asserts the string, per Constitution IV (contradicts)
- [X] T124 Make `DriverSyncService.HandlePoisonAsync` enqueue the builds waiting on a `DownloadFile` that lands in the poison queue (the same `BuildsWaitingOnAsync` path a failed download takes), so a pending set falls back to the last verified file or leaves the component out instead of staying unbuilt until the next sync, with a test in tests/CloudImaging.ImagingCoreApi.Tests/ per FR-031 (partial)
- [X] T125 Add the `DriverCustomComponentRepository`, custom target and `DriverReplacements` tables and repositories that T017 lists, plus the `Replacements` name in `DriverTables` and their creation in `DriverStore.EnsureTablesExistAsync`, in src/CloudImaging.ImagingCoreApi/Repositories/Drivers/ per data-model sections 3 and 4 (partial)
- [X] T126 Align the stored driver set document with data-model section 5.1 (`schemaVersion`, item `file`/`sizeBytes`/`install{extract,command}` shape, fingerprint over sorted lineage|version|action|origin) in src/CloudImaging.ImagingCoreApi/Services/Drivers/ and keep the device descriptor unchanged, per data-model 5.1 (contradicts)
- [X] T127 Show the failure reason in the Models tab tooltip for a failed model, and disable the onboarding dialog's search while the model list is being fetched, in src/cloud-imaging-portal/client/src/components/drivers/, per portal-client-surfaces section 2 (partial)
- [X] T128 Accept and validate `windowsRelease` on the OS image upload start (400 for an unknown release before any bytes are staged) in addition to publish, in src/CloudImaging.ImagingCoreApi/Functions/OsImageUploadFunctions.cs and the portal server upload route, per T038 (contradicts)
- [X] T129 Review the vendor list, single-model check and all-models check routes already built ahead of T056/T060 (Core, Operator API and portal server) against those tasks so they are extended and tested, not added again, per plan: route table (unrequested)

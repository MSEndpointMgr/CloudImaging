# Implementation Plan: Setup Profiles

**Branch**: none created (work happens on the current branch; feature directory `specs/003-setup-profiles`) | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/003-setup-profiles/spec.md`

## Summary

Operators define setup profiles (registry values, PowerShell scripts, an optional answer file and a
failure handling setting) on a new Setup Profiles page and target each at All devices or at
locations. Imaging Core stores profiles as immutable, numbered versions (index rows in Table
storage, content as one JSON blob per version), enforces targeting uniqueness through a
location-keyed index table, and pins the resolved profile version on the session at Start imaging.
The Client fetches the pinned version once through a new Device Gateway endpoint (descriptor plus a
short-lived SAS and SHA-256), before it formats the disk, and runs a new **Prepare setup** step
after Apply image (and after spec 005's Install drivers) and before Configure boot: it writes
registry values into the applied image's offline SOFTWARE, SYSTEM and default user hives, runs In
WinPE scripts as SYSTEM under a restricted token without shutdown privileges and a kill-on-close job
with a timeout, stages During Windows setup scripts (and spec 005's driver installers) with a
SHA-256 manifest, and writes the device's single `Windows\Panther\unattend.xml`, merging one setup
runner command (Order 1, specialize, device architecture) into the profile's own answer file. The
setup runner is a Windows PowerShell script shipped with the Client release; during specialize it
verifies, runs, logs and cleans up every item without allowing restarts. Per-item outcomes ride on
the step's final progress report and are stored in a new step items table; the portal timeline
gains optional steps, a warning marker and an item list without assuming a fixed step count.
Answer file rules are enforced live in the portal and authoritatively in Core, both pinned by one
shared case file. Media Builder adds WinPE-Scripting and WinPE-PowerShell to every boot image.
Operator and Administrator edit, Technician reads, and only Administrator changes version
retention, reusing spec 004's guards and capability flags.

**Amendment 2026-10-24** ([research.md](research.md) R18, design source
`docs/design/setup-profile-scripts-options.html`): scripts up to 24 MiB each and up to 1000 per
profile, the answer file up to 2 MiB, no total. Script and answer file texts are uploaded one per
request to a content-addressed `content/{sha256}` blob store and referenced from the version
document, so saves and profile reads stay small. Each script runs at all of the profile's
locations or at selected ones, and the device's descriptor lists only the scripts for its
location. The phases are called Offline and Specialize in the portal (wire values unchanged),
order is changed in the scripts table, and a Script environment dialog replaces the variable help
text. Touch-points added by the amendment:

| Area | Files |
|---|---|
| Contracts | `SetupScript` (locations, contentSha256, sizeBytes), `SetupAnswerFile` (contentSha256, sizeBytes), content upload result, descriptor `scripts` |
| Imaging Core | `SetupProfileContentRepository` (new), `SetupProfileContentFunctions` (new: upload, read), `SetupProfileValidator` (limits, locations), `SetupProfileService` (reference checks, answer file from blob, effective locations), `SetupProfileDescriptorFunction` (per-location scripts and SAS), `ChangeSummaryBuilder` (references, locations), `SetupProfileService.SweepAsync` (content cleanup), `ImagingStepItemRepository` cap |
| Operator API | `SetupProfileFunctions` (content relay, raw body) |
| Device Gateway | descriptor pass-through test only |
| Portal server | `routes/setup-profiles.ts` (content routes, 25 MiB raw parser on the upload route) |
| Portal client | `lib/setupProfiles.ts` (labels, limits, order moves, location filter, effective locations), `components/setup-profiles/ScriptDialog.tsx`, new `ScriptEnvironmentDialog.tsx`, `AnswerFileDialog.tsx` (upload on Done, 2 MiB), `pages/SetupProfileEditorPage.tsx` (scripts card), `components/SessionProgressDetails.tsx` (kind labels) |
| Client | `SetupProfileContentLoader` (per-script downloads), `PrepareSetupService` / `WinPeScriptRunner` (Offline scripts on the applied volume), `SetupRunnerManifestBuilder` (four-digit names) |
| Docs | `docs/setup-profiles.md`, `docs/troubleshooting-index.md`, `docs/session-lifecycle.md`, release notes |

---

## Technical Context

**Language/Version**:
- C# on .NET 10 (`net10.0`): Contracts, Imaging Core API, Operator API, Device Gateway API (Azure Functions v4 isolated)
- C# on .NET 10 (`net10.0-windows`, WPF): Cloud Imaging Client (WinPE), Media Builder
- TypeScript on Node.js 22 (Express): Portal backend
- TypeScript, React 19: Portal frontend
- Windows PowerShell 5.1: setup runner (runs on the deployed OS during specialize) and In WinPE scripts (operator content, run by the Client)
- Bicep: one new blob container

**Primary Dependencies**:
- Existing: `Azure.Data.Tables`, `Azure.Storage.Blobs` (`BlobSasUrlGenerator` for user delegation SAS), `System.Xml` (`XmlReader`, `XDocument`), `Microsoft.Win32.Registry` and advapi32 interop (pattern of `OfflineDriveLetterService`), Express, React Router, lucide-react
- New: `saxes` (portal client only; strict XML 1.0 parser with line numbers, code-split into the answer file dialog). No new NuGet packages.
- ADK optional components: `WinPE-Scripting`, `WinPE-PowerShell` (and their `en-us` language cabs)

**Storage** (Imaging Core storage account):
- New tables `SetupProfiles`, `SetupProfileTargets`, `SetupProfileVersions`, `SetupProfileHistory`, `ImagingStepItems` (created at Core start-up in `Program.cs`)
- New blob container `setup-profiles` (declared in `src/deploy/bicep/modules/storage.bicep`)
- Changed entities: `DeviceSessions` (pin columns), `ImagingSteps` (warning count, item counts), portal configuration row (retention)
- Details: [data-model.md](data-model.md)

**Testing**:
- .NET: xUnit + FluentAssertions; Core integration tests in `tests/CloudImaging.ImagingCoreApi.Tests/Integration`; contract tests in the Gateway and Operator API test projects
- Setup runner: xUnit tests in `tests/CloudImaging.Client.Tests` that run the real script under `powershell.exe` against a temp manifest (Windows)
- Portal: Vitest + Supertest (server), Vitest + Testing Library (client)
- Shared answer file cases: `tests/fixtures/answer-file-cases.json` (both suites)
- Manual WinPE, specialize and hardware verification: [quickstart.md](quickstart.md) sections 5 to 7

**Target Platform**: Azure Functions (Flex Consumption), Azure App Service (Portal backend), Azure
Static Web Apps (Portal frontend), WinPE and Windows RE boot images (x64, ARM64), Windows 11 23H2,
24H2 and 25H2 deployed images, Windows technician workstation (Media Builder).

**Project Type**: Polyglot web application plus WinPE client and desktop tool; a new feature across all six components.

**Design source**: [docs/design/os-customisation.html](../../docs/design/os-customisation.html) for every portal surface; [contracts/portal-client-surfaces.md](contracts/portal-client-surfaces.md) follows it.

**Performance Goals**:
- Prepare setup adds at most 30 seconds to a deployment, excluding the profile's own scripts (SC-005)
- Portal list and detail reads at most 300 ms at p95 under 50 users (constitution V)
- Client start-up unchanged (at most 5 seconds to the main window); the profile fetch happens after assignment, not at start-up
- Progress reporting stays within 10 calls per 30 seconds per session

**Constraints**:
- WinPE: SYSTEM context, RAM disk `X:`, no shell; PowerShell present only in boot images built by the new Media Builder
- Offline SYSTEM hive has no `CurrentControlSet`; mapped through `Select\Current`
- Table storage: 1 MB per entity, 64 KB per property, 100 operations per transaction; versions therefore live in blobs
- A profile version is at most about 2 MB (20 scripts of 64 KB, a 256 KB answer file, 200 registry values); portal router body limit 4 MB
- Setup runner depends only on what every Windows 11 image has (Windows PowerShell 5.1, .NET Framework 4.8)
- No restarts inside the step or inside specialize (FR-015)
- The device gets exactly one answer file and exactly one Cloud Imaging command in it (FR-021, FR-022, SC-007)
- User-facing text: ASCII punctuation only; no requirement, criterion, task or story identifiers anywhere outside code comments (FR-044, constitution IV)
- PowerShell follows `.github/copilot-instructions.md` General PowerShell Practices; portal follows `.github/instructions/portal-ui.instructions.md`

**Scale/Scope**:
- Portal backend: 1 new router with 9 routes, 1 new session route, 2 changed routes (location delete, portal configuration), 1 new guard, body limit and error relay in `app.ts` ([contracts/portal-server-routes.md](contracts/portal-server-routes.md))
- Operator API: 10 new proxy functions, 1 changed (location delete forwards `changedBy`)
- Imaging Core: 9 profile functions, 1 device descriptor function, 1 step items function; 8 changed functions or services (assign, bulk assign, report progress, session query, location delete, portal configuration, lifecycle timer, progress calculator); 5 new repositories; 6 new services ([contracts/operator-and-core-api.md](contracts/operator-and-core-api.md))
- Device Gateway: 1 new function; progress relay unchanged ([contracts/device-gateway.md](contracts/device-gateway.md))
- Client: 1 new step, 10 new files under `Services/Setup/`, the setup runner script, 9 changed files ([contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md))
- Media Builder: 2 optional components, manifest flag
- Portal frontend: 2 pages, 5 profile components, 2 new libraries, 3 extracted or new shared components, 13 changed files ([contracts/portal-client-surfaces.md](contracts/portal-client-surfaces.md))
- Limits per profile: 200 registry values, 20 scripts (64 KB each), one answer file (256 KB); retention 5 to 100 versions (default 25)

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result | Enforcement in this feature |
|---|---|---|
| I. Zero-Warning .NET Build | PASS | New Contracts, Core, Gateway, Operator API, Client and Media Builder code builds in Release with zero warnings and no suppressions. Interop follows the existing `OfflineDriveLetterService` pattern; nullable stays enabled. |
| II. Test-First Development | PASS | Every rule-bearing unit is pure and written test-first: `AnswerFileValidator` and `answerFileValidation.ts` (shared case file), `SetupProfileValidator`, `ChangeSummaryBuilder`, `SetupProfileResolver`, `OverallProgressCalculator`, `UnattendComposer`, `SetupRunnerManifestBuilder`, `OfflineRegistryPath`, `PrepareSetupService` (behind interfaces for hive, process and file system), portal `setupProfiles.ts`. Coverage gate: at least 80% line coverage on the files listed in [quickstart.md](quickstart.md) section 1. Tests are deterministic: `TimeProvider` for timestamps and timeouts, no network in unit tests. |
| III. Integration and E2E Testing | PASS | Contract tests for every new HTTP contract (Gateway descriptor and progress relay, Operator API proxies, Core functions); Supertest for every new portal route plus the role matrix; Core integration tests for target uniqueness, pinning, retention and location deletion. WinPE, specialize and hardware behaviour (hive loads per Windows release, restart guard, PowerShell in boot images) are documented manual verification items in [quickstart.md](quickstart.md) sections 5 and 6, as Principle III allows for privileged OS operations. |
| IV. UX Consistency | PASS | Client: all step work runs in the pipeline's background task; the UI gets status lines and step percent only; the Results screen explains warnings. Portal: existing primitives, extracted Wi-Fi picker and badges (FR-004, FR-006), `ConfirmImpactDialog` for delete, restore and discard, WCAG labels on the code editor and problem list. All proposed text is ASCII and free of identifiers. No user-facing interface breaks: the new step is additive, and the location delete refusal only applies to locations a profile targets, which cannot exist before this release. |
| V. Performance | PASS with one tracked item | Prepare setup is budgeted at 30 seconds excluding scripts (step weight 0, hives loaded only as needed). Client start-up is untouched. Boot image growth is measured. Large saves are tracked in Complexity Tracking. |
| .NET Diagnostic Policy | PASS with one tracked item | All projects stay on `net10.0` or `net10.0-windows`. The setup runner compiles a small C# block at run time through `Add-Type` on the deployed OS; this is not a project and is recorded in Complexity Tracking. |
| Development Workflow and Gates | PASS | Build, unit, integration, lint (`npm run lint` both portal packages, `dotnet format --verify-no-changes`), performance ([quickstart.md](quickstart.md) section 7) and UI responsiveness gates apply unchanged. |

**Gate: PASSED. Proceeding to Phase 0.**

### Post-Design Constitution Re-check

The design adds no new project, no new runtime layer and no new NuGet package. Two items need a
recorded justification (Complexity Tracking): the setup runner's run-time C# block and the portal
latency budget for maximum-size profile saves. The duplicated answer file rule set crosses a
runtime boundary (TypeScript and C#), which the constitution permits ("single source of truth per
runtime boundary"); the shared case file is the existing mechanism (`autopilot-group-tag-cases.json`)
that keeps the two identical. Interfaces introduced for testability (`IOfflineRegistryHive`,
`IRestrictedProcessRunner`, `ISetupFileSystem`) each have exactly one production implementation
and exist only so WinPE-only behaviour can be unit-tested (Principle II), not as speculative
abstraction.

**Gate: PASSED after Phase 1 design.**

---

## Project Structure

### Documentation (this feature)

```text
specs/003-setup-profiles/
    spec.md
    plan.md                                  This file
    research.md                              Phase 0 decisions (R1 to R17)
    data-model.md                            Entities, storage, limits
    quickstart.md                            Automated checks, tenant and device walkthrough
    contracts/
        portal-server-routes.md              Portal backend routes, guards, payloads, errors
        operator-and-core-api.md             Operator API proxies, Core internal endpoints and processing
        device-gateway.md                    Device descriptor endpoint, progress extension, Client handling
        setup-runner-and-unattend.md         Prepare setup phases, spec 005 hand-off, manifest, runner, answer file merge
        answer-file-rules.md                 Shared validation rules and case file format
        portal-client-surfaces.md            Pages, dialogs, timeline, overlay, configuration, extractions
    checklists/
    tasks.md                                 Phase 2 output (/speckit.tasks), not created here
```

### Source Code (files to add or change)

```text
src/CloudImaging.Contracts/
    Enums/ImagingEnums.cs                    ImagingStepName.PrepareSetup
    Enums/SetupProfileEnums.cs               NEW: hive, value type, script phase, failure handling, item kind, item outcome
    Models/ImagingStep.cs                    WarningCount, ItemCounts, ItemsTruncated
    Models/DeviceSession.cs                  SetupProfileId, SetupProfileVersion, SetupProfileName
    Models/PortalConfiguration.cs            SetupProfileVersionRetention (+ changed at/by)
    Models/BootImageManifest.cs              PowerShellIncluded
    Models/SetupProfiles/SetupProfileContent.cs      NEW: version document (registry values, scripts, answer file)
    Models/SetupProfiles/SetupProfileDescriptor.cs   NEW: device descriptor
    Models/SetupProfiles/ImagingStepItem.cs          NEW: item outcome, item count

src/CloudImaging.ImagingCoreApi/
    Program.cs                               DI registrations, EnsureTableExistsAsync for 5 tables
    Repositories/SetupProfileRepository.cs           NEW
    Repositories/SetupProfileTargetRepository.cs     NEW: uniqueness index, transactions
    Repositories/SetupProfileVersionRepository.cs    NEW: version rows and blobs
    Repositories/SetupProfileHistoryRepository.cs    NEW
    Repositories/ImagingStepItemRepository.cs        NEW
    Repositories/DeviceSessionRepository.cs  pin columns; active sessions by profile
    Repositories/ImagingStepRepository.cs    WarningCount, ItemCountsJson, ItemsTruncated
    Repositories/PortalConfigurationRepository.cs    retention fields
    Services/SetupProfileService.cs          NEW: create, save, restore, delete, prune
    Services/SetupProfileValidator.cs        NEW: field and limit rules
    Services/AnswerFileValidator.cs          NEW: shared rule set, server side
    Services/ChangeSummaryBuilder.cs         NEW
    Services/SetupProfileResolver.cs         NEW: location to profile at assignment
    Services/SetupProfileVersionSweeper.cs   NEW: tombstoned and pruned versions
    Services/OverallProgressCalculator.cs    PrepareSetup weight 0, IsPipelineComplete, ordered optional steps
    Services/BulkAssignmentService.cs        pin per distinct location
    Functions/SetupProfileFunctions.cs       NEW: 9 profile endpoints
    Functions/SessionSetupProfileFunction.cs NEW: device descriptor
    Functions/SessionStepItemsFunction.cs    NEW
    Functions/AssignSessionFunction.cs       pin
    Functions/ReportProgressFunction.cs      items, warning count, completion rule
    Functions/SessionQueryFunctions.cs       setupProfile and step fields in the summary
    Functions/LocationFunctions.cs           only-location refusal, target removal, history, changedBy
    Functions/PortalConfigurationFunctions.cs retention validation and audit
    Functions/SessionLifecycleTimerFunction.cs version sweep, step items purge

src/CloudImaging.OperatorApi/
    Functions/SetupProfileFunctions.cs       NEW: 9 proxies
    Functions/SessionQueryFunctions.cs       step items proxy
    Functions/LocationFunctions.cs           forwards changedBy
    Services/ImagingCoreClient.cs            new calls
    OpenApi/OpenApiConfig.cs                 new operations

src/CloudImaging.DeviceGatewayApi/
    Functions/SessionSetupProfileFunction.cs NEW: GET v1/sessions/{id}/setup-profile
    Services/ImagingCoreClient.cs            GetSessionSetupProfileAsync
    OpenApi/OpenApiConfig.cs                 new operation, progress body fields

src/CloudImaging.Client/
    CloudImaging.Client.csproj               SetupRunner\*.ps1 as Content (copied to output)
    SetupRunner/Invoke-CloudImagingSetup.ps1 NEW: setup runner
    Services/DeviceGatewayApiClient.cs       GetSetupProfileAsync
    Services/BootImageCapabilities.cs        SupportsPowerShell
    Services/OfflineDriveLetterService.cs    uses OfflineRegistryHive (behaviour unchanged)
    Services/ImagingProgressReporter.cs      final report with warningCount and items
    Services/Setup/OfflineRegistryHive.cs    NEW: extracted load/unload interop
    Services/Setup/OfflineRegistryPath.cs    NEW: CurrentControlSet mapping, data parsing (pure)
    Services/Setup/OfflineRegistryWriter.cs  NEW
    Services/Setup/RestrictedProcessRunner.cs NEW: restricted token, job object, timeout, output capture
    Services/Setup/WinPeScriptRunner.cs      NEW
    Services/Setup/SetupProfileContentLoader.cs NEW: descriptor, download, verify, parse
    Services/Setup/UnattendComposer.cs       NEW (pure)
    Services/Setup/SetupRunnerManifestBuilder.cs NEW (pure)
    Services/Setup/SpecializeInstaller.cs    NEW: hand-off type for spec 005
    Services/Setup/PrepareSetupService.cs    NEW: phases, failure handling, item outcomes
    ViewModels/ImagingWorkflowViewModel.cs   fetch before Format disk; Prepare setup step
    ViewModels/ProgressViewModel.cs          step list built per session; warning state
    ViewModels/ResultsViewModel.cs           completed-with-warnings message
    Views/ProgressView.xaml                  warning visual for a step

src/CloudImaging.MediaBuilder/
    Services/BootImageGenerationService.cs   WinPE-Scripting, WinPE-PowerShell after NetFx
    Services/BootImageManifestService.cs     powerShellIncluded

src/cloud-imaging-portal/server/src/
    app.ts                                   mount router, 4 MB parser for it, relay bounded errors array
    middleware/roleGuard.ts                  requireTechnician
    routes/setup-profiles.ts                 NEW: 9 routes
    routes/sessions.ts                       step items route
    routes/locations.ts                      delete forwards changedBy
    routes/portal-config.ts                  PUT forwards changedBy
    services/operatorApiClient.ts            new calls

src/cloud-imaging-portal/client/
    package.json                             saxes
    src/lib/portalRoles.ts                   canViewSetupProfiles, canManageSetupProfiles
    src/lib/setupProfiles.ts                 NEW: types, API calls, overlay grouping, draft diff
    src/lib/answerFileValidation.ts          NEW: shared rule set, portal side
    src/lib/imagingProgress.ts               optional steps, PrepareSetup
    src/lib/routeTitles.ts                   Setup Profiles
    src/App.tsx                              routes
    src/components/Sidebar.tsx               navigation item
    src/components/LocationPicker.tsx        NEW: extracted from WifiNetworkDialog, disabledReason
    src/components/TargetingBadges.tsx       NEW: extracted from WifiNetworksPage, allLabel
    src/components/WifiNetworkDialog.tsx     uses LocationPicker
    src/components/ConfirmImpactDialog.tsx   optional details slot
    src/components/SessionProgressDetails.tsx dynamic rail, warning marker, Not run, Prepare setup card
    src/components/ui/code-text-area.tsx     NEW
    src/components/setup-profiles/TargetingSection.tsx     NEW
    src/components/setup-profiles/RegistryValueDialog.tsx  NEW
    src/components/setup-profiles/ScriptDialog.tsx         NEW
    src/components/setup-profiles/AnswerFileDialog.tsx     NEW
    src/components/setup-profiles/ProfileHistoryDialog.tsx NEW
    src/pages/SetupProfilesPage.tsx          NEW
    src/pages/SetupProfileEditorPage.tsx     NEW
    src/pages/WifiNetworksPage.tsx           uses TargetingBadges
    src/pages/SessionsPage.tsx               Start imaging overlay lines
    src/pages/LocationsPage.tsx              delete overlay names the profile
    src/pages/DeploymentConfigPage.tsx       retention field

src/deploy/bicep/modules/storage.bicep       container setup-profiles

docs/
    setup-profiles.md                        NEW: concepts, editing, targeting, versions, device behaviour, logs
    SUMMARY.md                               link
    roles-and-access.md                      Setup Profiles rows (Operator, Administrator edit; Technician view; retention Administrator)
    session-lifecycle.md                     Prepare setup step, pinning
    troubleshooting-index.md                 SetupRunner.log, common item failures
    upgrade-instructions.md                  container, boot image regeneration, Client update

.github/release-notes/
    backend/<version>.md                     NEW: Setup Profiles, upgrade order
    client/<version>.md                      NEW: Prepare setup step, setup runner
    mediabuilder/<version>.md                NEW: PowerShell in boot images, measured size

tests/
    fixtures/answer-file-cases.json          NEW: shared cases
    CloudImaging.ImagingCoreApi.Tests/
        CloudImaging.ImagingCoreApi.Tests.csproj     link the fixture
        Services/AnswerFileValidatorTests.cs         NEW
        Services/SetupProfileValidatorTests.cs       NEW
        Services/SetupProfileServiceTests.cs         NEW
        Services/ChangeSummaryBuilderTests.cs        NEW
        Services/SetupProfileResolverTests.cs        NEW
        Services/OverallProgressCalculatorTests.cs   PrepareSetup cases
        Functions/ReportProgressPayloadTests.cs      items, truncation
        Functions/SetupProfileFunctionsTests.cs      NEW
        Integration/SetupProfileIntegrationTests.cs  NEW: uniqueness, pinning, retention, location delete, sweep
        Integration/AssignSessionIntegrationTests.cs pin cases
        Integration/BulkAssignIntegrationTests.cs    pin cases
    CloudImaging.DeviceGatewayApi.Tests/Contracts/
        SetupProfileContractTests.cs                 NEW
        ReportProgressContractTests.cs               items relayed
    CloudImaging.OperatorApi.Tests/Contracts/
        SetupProfileContractTests.cs                 NEW
    CloudImaging.Client.Tests/
        UnattendComposerTests.cs                     NEW
        SetupRunnerManifestBuilderTests.cs           NEW
        OfflineRegistryPathTests.cs                  NEW
        PrepareSetupServiceTests.cs                  NEW
        SetupProfileContentLoaderTests.cs            NEW
        SetupRunnerScriptTests.cs                    NEW: real runner under powershell.exe
        ImagingWorkflowViewModelTests.cs             placement, skip, fetch failure before format
        ProgressViewModelStepProgressTests.cs        dynamic steps
    CloudImaging.MediaBuilder.Tests/
        BootImageGenerationTests.cs                  component order, missing cab
    cloud-imaging-portal/server/
        setup-profiles-routes.test.ts                NEW
        role-route-matrix.test.ts                    10 rows
        wifi-networks-routes.test.ts                 unchanged behaviour after extraction (sanity)
    cloud-imaging-portal/client/
        answer-file-validation.test.ts               NEW (shared fixture)
        setup-profiles.test.ts                       NEW
        portal-roles.test.ts                         new flags
        session-progress-details.test.tsx            optional steps, warning, Not run
        wifi-networks.test.ts                        extraction keeps behaviour
```

Not changed: `GetSessionStatusFunction` (Gateway and Core status payloads), `RateLimitingMiddleware`,
`AppRoleAuthorizationMiddleware`, the Wi-Fi network contracts, `SessionHistoryRecord` and the
Reports data (the failure report shows "Prepare setup" through `progressStepLabel`), the Media
Builder USB preparation flow, `.github/copilot-instructions.md`.

**Structure Decision**: The feature stays inside the existing six-component architecture. Imaging
Core owns every rule and all state; the Operator API and Portal backend stay pass-through layers
that add only authorization and the acting user; the Device Gateway adds one read endpoint; the
Client owns everything that touches the applied volume. The Client-side unit is
`Services/Setup/`, which spec 005 extends through the `SpecializeInstaller` hand-off rather than
owning a second answer file writer.

---

## Implementation Order

1. **Contracts and tests first**: enums and models; shared answer file case file; failing tests for
   `AnswerFileValidator`, `answerFileValidation.ts`, `SetupProfileValidator`, `ChangeSummaryBuilder`,
   `OverallProgressCalculator`, `UnattendComposer`, `SetupRunnerManifestBuilder`, `OfflineRegistryPath`.
2. **Core storage and services**: repositories, `SetupProfileService` (create, save, restore,
   delete, prune), resolver, Core functions, integration tests green.
3. **Pinning and progress**: assign and bulk assign pin; report progress items, warning count,
   completion rule; session summary fields; step items endpoint; lifecycle timer sweep and purge;
   location delete rule; retention field.
4. **Operator API and Device Gateway**: proxies, descriptor endpoint, contract tests, OpenAPI.
5. **Portal backend**: guard, router, body limit, error relay, session items route, location and
   configuration actor forwarding; route tests and matrix rows.
6. **Portal frontend**: capability flags; extract `LocationPicker` and `TargetingBadges` (Wi-Fi
   tests stay green); list and editor pages, dialogs, history; Start imaging overlay; timeline;
   Locations and Configuration changes.
7. **Media Builder**: PowerShell components, manifest flag, tests; measure size.
8. **Client**: extract `OfflineRegistryHive`; registry writer; restricted process runner; WinPE
   script runner; content loader; composer and manifest builder; `PrepareSetupService`; workflow,
   progress and results view models; setup runner script and its tests.
9. **Bicep, docs, release notes**: container; docs pages; notes per stream.
10. **Text scan** ([quickstart.md](quickstart.md) section 2) and **manual verification** (sections 3 to 7).

---

## Release Considerations

- **Streams and order**: backend (`mse-ci-v#.#.#`: Portal, Operator API, Imaging Core, Device
  Gateway, Bicep container) first; then Media Builder (`mse-ci-mediabuilder-v#.#.#`) and the Client
  (`mse-ci-client-v#.#.#`); then regenerate and publish boot images. Each needs its
  `.github/release-notes/<stream>/<version>.md` on `main` before tagging.
- **Mixed versions**: new backend with an old Client applies no profile and the timeline says so
  (Not run); new Client with an old backend sees 404 and does no profile work. Release notes
  state that devices apply profiles only after the boot image is updated.
- **Upgrade**: the template spec upgrade adds the `setup-profiles` container; Core creates the new
  tables at start-up. No data migration. Existing sessions and history render as before.
- **Spec 004**: if a deployment has not created the Operator role yet, Administrators hold the
  write permissions; nothing in this feature depends on the role existing.
- **Spec 005**: ships later and plugs into `SpecializeInstaller` and the runner manifest; this
  feature works alone (the runner then only runs scripts, and Prepare setup runs after Apply image).

---

## Risks and Items to Validate

| # | Risk | Mitigation and validation |
|---|---|---|
| 1 | Default user hive (`NTUSER.DAT`) loading in WinPE differs across Windows 11 23H2, 24H2, 25H2 | Per-hive failure with a clear reason; quickstart 6.7 on all three releases. |
| 2 | No network during specialize for scripts that need it (NIC driver missing) | Documented in `docs/setup-profiles.md`; spec 005 injects NIC drivers; quickstart 6.13. |
| 3 | Boot image size and WinPE boot time after adding PowerShell | Measured per architecture (quickstart 5.2, 5.3); Client start-up budget unaffected by design. |
| 4 | `CreateRestrictedToken` and `CreateProcessAsUser` behaviour in WinPE and Windows RE; `Add-Type` during specialize | Fail closed (items not run, reason logged); quickstart 6.8. |
| 5 | Restarts initiated by the Windows Installer service on behalf of an MSI (spec 005 installers) bypass the caller's token | Spec 005 must pass no-restart switches; runner records 1641 as failed; quickstart in spec 005. |
| 6 | Static Web Apps or App Service limits on a 2 MB request body | Quickstart 7.3 before release; fallback is per-section saves (would change the save bar design, so it is validated first). |
| 7 | ARM64 ADK may lack WinPE-Scripting or WinPE-PowerShell cabs | Generation fails with the existing clear missing-cab message; quickstart 5.1 on ARM64. |
| 8 | OS images that are not generalized never run specialize | Detected offline; answer file item fails with the reason (quickstart 6.12). |
| 9 | Restart guard is not a security boundary against a hostile SYSTEM script | Stated in docs; write access limited to Operator and Administrator; every change audited. |

---

## Complexity Tracking

| Item | Principle | Why needed | Simpler alternative rejected because |
|---|---|---|---|
| Setup runner compiles a small C# block with `Add-Type` on the deployed OS | .NET Diagnostic Policy (all projects target .NET 10) | Blocking every restart path needs a restricted token and a job object, which PowerShell cannot create without Win32 interop. The applied OS has no .NET 10 runtime, so the runner cannot be a .NET 10 program. The block is not a project, is not built by the solution and is covered by `SetupRunnerScriptTests`. | A .NET 10 helper would add a self-contained runtime (about 70 MB) to every device; a .NET Framework 4.8 helper adds a second target framework to the solution for about 100 lines. |
| `GET /api/setup-profiles/:id`, version reads and saves may carry up to about 2 MB | V. Portal API calls at most 300 ms at p95 | The spec requires whole-version saves ("Save as version N") with scripts up to 64 KB and an answer file up to 256 KB. Typical profiles are a few KB and meet the budget; only near-maximum profiles approach the limit. The list call, the one used most, carries no content. Measured in quickstart 7.2 and 7.3; a regression above 10% blocks merge as usual. | Per-item save endpoints would break the single save bar and versioning model the spec defines. |

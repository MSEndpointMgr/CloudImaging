# Implementation Plan: OS Driver Management

**Branch**: `feat/005-os-driver-mgmt` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/005-os-driver-management/spec.md`

## Summary

Operators onboard HP and Dell device models per Windows release on a new Drivers page. A sync in
Imaging Core reads the vendors' catalogs, classifies every component into Offline, Offline +
Specialize, Specialize or Skip, applies rules, overrides and approvals, and keeps a resolved driver
set per model and release whose original vendor files are copied into the customer's storage and
verified against the vendor's SHA-256. Assignment pins the matching set on the session. The WinPE
Client runs a new Install drivers step after Apply image: it downloads each component through its
own short-lived link, verifies it, extracts it with the vendor's tool onto the applied volume,
injects its INF drivers with DISM, and hands Specialize work to spec 003's Prepare setup step and
setup runner. Outcomes appear on the deployment timeline.

Technical approach (from [research.md](research.md)): event-driven background work on a Storage
queue in Imaging Core, with the scheduled sync as a delayed message and no polling timer (R1);
models onboarded as drafts and completed on their model page (R31); vendor files downloaded by Core over its existing outbound access and
streamed into storage while their SHA-256 is checked (R2, R5); a managed MSZIP and LZX cabinet
reader (R3); vendor adapters for HP and Dell
([contracts/vendor-catalogs.md](contracts/vendor-catalogs.md)); a pure policy resolver (R18);
per-component SAS links relayed by the Device Gateway (R10); reuse of spec 003's item reporting,
timeline card and Specialize hand-off (R13, R15); x64 and ARM64 models, with HP ARM64 components
installed during Specialize because HP's packages are x64 programs (R24).

Spec alignment (folded into the spec on 2026-10-25): action names are Offline, Offline +
Specialize, Specialize and Skip everywhere; matching and pinning happen in Imaging Core at
assignment with the Device Gateway relaying the pinned set (FR-045); the OS image's Windows release
names the Windows version as well ("Windows 11, version 24H2"), because 22H2 exists for Windows 10
and 11 (FR-044).

## Technical Context

**Language/Version**: C# on .NET 10 (`net10.0`: Contracts, Imaging Core API, Operator API, Device
Gateway API on Azure Functions v4 isolated; `net10.0-windows`: Cloud Imaging Client in WinPE);
TypeScript on Node.js 22 (Express portal backend); TypeScript with React 19 (portal frontend).

**Primary Dependencies**: Azure.Data.Tables 12.9, Azure.Storage.Blobs 12.22 (block upload,
user delegation SAS), Azure.Storage.Queues, Azure.Identity, Microsoft.Azure.Functions.Worker 2.x
with the Storage Queues extension (`Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues`,
the only new package, R1); System.Management (WMI) in the Client; Vite, Tailwind and the existing
`components/ui` primitives in the portal. The cabinet reader is in-repo (R3).

**Storage**: Azure Table Storage (new tables in [data-model.md](data-model.md) section 3) and the
new private Blob container `drivers` on the Imaging Core storage account (catalogs, vendor files,
set documents).

**Testing**: xUnit, FluentAssertions and Moq (.NET) with the existing in-memory table and blob
doubles in Core tests; Vitest and Supertest (portal server); Vitest and Testing Library (portal
client); real vendor catalogs as fixtures under `tests/fixtures/drivers/`.

**Target Platform**: Imaging Core on Linux Elastic Premium (EP1, `functionTimeout` 1 hour, private
inbound, outbound through VNet integration as already used for Microsoft Graph); Device Gateway and
Operator API on Azure Functions; Client in x64 and ARM64 WinPE and WinRE boot images built by Media
Builder (WinPE-WMI, NetFx, Scripting,
PowerShell); portal on Static Web Apps and App Service.

**Project Type**: Polyglot web service plus WinPE desktop client, extending existing projects.

**Performance Goals**: Install drivers under 10 minutes on reference models at 500 Mbit/s;
portal Drivers reads under 300 ms p95; an 800 MB file verified within one queue message (R5);
downloads start within seconds of the event that needs them.

**Constraints**: Function RSS under 256 MB (streaming copy, hash and XML); Client working set
under 512 MB, nothing extracted to X:, all I/O off the UI thread; no unsigned drivers; vendor
hosts allowlisted; at most 10 device calls per 30 seconds per session; ASCII-only user-facing text
and no spec identifiers outside comments.

**Scale/Scope**: Tens of onboarded models per tenant, 30 to 60 components per model and release,
2 to 3 GB of files per HP model; 48 portal routes (1 to 46 plus 37a and 37b), about 27 Core functions plus 2 queue triggers
(no timers), 2 Device Gateway functions; one new Client step; one portal page with five tabs and a
model page.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Pre-research | Post-design |
|---|---|---|---|
| Architecture: Core owns state transitions and SAS | Pinning, sets and SAS issuance in Core; Gateway relays; Operator API proxies | Pass | Pass (R9, R10, contracts) |
| I. Zero warnings | Release build with zero diagnostics, no suppressions | Pass | Pass (quickstart section 1) |
| II. Test-first, 80% coverage, deterministic | Parsers, classification, resolver, schedule and Client step logic are pure units over fixtures and fakes with an injected `TimeProvider` | Pass | Pass (data-model section 6, fixtures) |
| III. Contract and integration tests | Every new HTTP function has a contract test; portal routes in Supertest and the role matrix; vendor downloads checked against real endpoints in staging; the staging smoke test covers the driver path | Pass | Pass (contracts, quickstart sections 1 and 3) |
| IV. UX consistency | Portal primitives, impact overlays for Remove model and Roll back, WCAG labels; Client non-blocking with live status; ASCII text | Pass | Pass ([contracts/portal-client-surfaces.md](contracts/portal-client-surfaces.md), [contracts/client-install-drivers.md](contracts/client-install-drivers.md)) |
| V. Performance (constitution 1.5.0) | Client 512 MB, Function 256 MB, API 300 ms p95, 15-minute end to end for the base OS image, a stated budget for added payload | Pass | Pass: the driver payload budget is Install drivers under 10 minutes at 500 Mbit/s (SC-011); the onboarding preview and model list wait on a vendor catalog only on a cache miss, with a loading line per model, and meet 300 ms from cache (R21) |
| .NET policy | net10.0, nullable, analyzers | Pass | Pass |
| Quality gates 1 to 6 | Build, unit, integration, lint, performance (no regression over 10% against the main baseline) and UI responsiveness (no dispatcher block over 100 ms in the Client Install drivers step and the Media Builder preload); one approving review by another engineer before merge | Pass | Pass (quickstart sections 1 and 7, T031, T083, T117) |

No gate fails.

## Project Structure

### Documentation (this feature)

```text
specs/005-os-driver-management/
├── plan.md              # This file
├── research.md          # Phase 0 decisions R1 to R33
├── data-model.md        # Entities, tables, blobs, state transitions
├── quickstart.md        # Validation guide
├── contracts/
│   ├── portal-server-routes.md
│   ├── operator-and-core-api.md
│   ├── device-gateway.md
│   ├── client-install-drivers.md
│   ├── vendor-catalogs.md
│   ├── media-builder-driver-sets.md
│   └── portal-client-surfaces.md
└── tasks.md             # Phase 2 (/speckit.tasks)
```

### Source Code (repository root)

```text
src/CloudImaging.Contracts/
├── Enums/DriverEnums.cs                       # new enums (data-model section 1)
├── Enums/ImagingEnums.cs                      # + InstallDrivers
├── Enums/SetupProfileEnums.cs                 # + DriverComponent, Skipped
├── Models/Drivers/*.cs                        # API and descriptor models
├── Models/OsImage.cs                          # + WindowsRelease
├── Models/DeviceHardwareMetadata.cs           # + SystemSku
├── Models/DeviceSession.cs                    # + driver pin fields
└── Models/SetupProfiles/ImagingStepItem.cs    # + DriverAction

src/CloudImaging.ImagingCoreApi/
├── Functions/DriverFunctions.cs               # portal-facing endpoints, incl. storage, downloads, component roll back
├── Functions/DriverCustomComponentFunctions.cs # custom component staged upload, CRUD, notices
├── Functions/DriverSyncFunctions.cs           # settings, runs, model check, driver-work and poison queue triggers
├── Functions/SessionDriverSetFunction.cs      # device descriptor and link refresh
├── Functions/AssignSessionFunction.cs         # + driver pin, optional manual choice
├── Functions/BulkAssignFunction.cs            # + driverChoices, 409 with errors
├── Functions/ReportProgressFunction.cs        # + InstallDrivers, status note
├── Functions/ImageCatalogFunctions.cs         # + WindowsRelease on update
├── Functions/MediaBuilderDriverSetFunctions.cs # driver sets and file links for Media Builder preload
├── Data/time-zones.json                       # embedded Windows time zone list (DriverTimeZoneCatalog)
├── Repositories/Driver*Repository.cs          # tables and blobs (data-model sections 3 and 4)
├── Services/Drivers/CabinetReader.cs          # MSZIP and LZX
├── Services/Drivers/HpDriverVendorAdapter.cs
├── Services/Drivers/DellDriverVendorAdapter.cs
├── Services/Drivers/DriverPolicyResolver.cs
├── Services/Drivers/DriverSetBuilder.cs       # fingerprint, pointer, roll back
├── Services/Drivers/DriverSyncScheduler.cs    # slots, time zones, ScheduledSync messages
├── Services/Drivers/DriverWorkQueue.cs        # enqueue with delay, message shapes, queue creation
├── Services/Drivers/DriverSyncService.cs      # runs, message handlers, clean-up, history retention
├── Services/Drivers/DriverSetResolver.cs      # assignment match and skip reasons
├── Services/Drivers/DriverStorageService.cs   # download from vendor, verify, SAS, container
├── Services/BulkAssignmentService.cs          # + driver pin, manual choices validated before any assignment
├── Services/OverallProgressCalculator.cs      # + InstallDrivers, weight 0
├── Services/UploadPublishService.cs           # + WindowsRelease on publish
└── Program.cs                                 # registrations, table and queue creation, first ScheduledSync

src/CloudImaging.OperatorApi/
├── Functions/DriverFunctions.cs               # proxies
├── Services/ImagingCoreClient.cs
└── OpenApi/OpenApiConfig.cs

src/CloudImaging.DeviceGatewayApi/
├── Functions/SessionDriverSetFunction.cs
├── Services/ImagingCoreClient.cs
└── OpenApi/OpenApiConfig.cs

src/CloudImaging.Client/
├── Services/Drivers/DriverSetLoader.cs        # descriptor fetch and link refresh
├── Services/Drivers/DriverInstallService.cs   # per-component flow and outcomes
├── Services/Drivers/HpSoftPaqExtractor.cs
├── Services/Drivers/DellPackageExtractor.cs
├── Services/Drivers/DismDriverInjector.cs
├── Services/Drivers/PciInventory.cs
├── Services/Drivers/CustomZipExtractor.cs     # custom component ZIPs, path-checked
├── Services/Drivers/DriverFileCache.cs        # driver files on the USB drive's CACHE partition
├── Services/ImageCacheMaintenanceService.cs   # + 30-day idle purge of drivers\
├── Services/DeviceGatewayApiClient.cs         # + driver set calls, SystemSku in SignPayload
├── ViewModels/OperationSelectionViewModel.cs  # + SystemSKUNumber
├── ViewModels/ImagingWorkflowViewModel.cs     # + Install drivers step, installers to Prepare setup
└── ViewModels/ProgressViewModel.cs            # + optional step

src/CloudImaging.MediaBuilder/
├── Services/OperatorApiClient.cs              # + driver sets and file links
├── Services/DriverSetDownloadService.cs       # download and verify into the local cache
├── Services/UsbPreparationService.cs          # + DriverFiles to the elevated worker, copy to CACHE, manifest DriverSets
├── ViewModels/PrepareStorageDeviceViewModel.cs # + Driver sets section, size and space checks
└── Views/PrepareStorageDeviceView.xaml        # + Driver sets section

src/CloudImaging.Contracts/Models/UsbPreparationManifest.cs  # + DriverSets

src/CloudImaging.OperatorApi/Middleware/AppRoleAuthorizationMiddleware.cs  # + two Media Builder functions

src/cloud-imaging-portal/server/src/
├── routes/drivers.ts
├── routes/sessions.ts                         # + driverChoices on assign and bulk assign, actor from token
├── routes/images.ts                           # + windowsRelease
├── services/operatorApiClient.ts
└── app.ts

src/cloud-imaging-portal/client/src/
├── pages/DriversPage.tsx                      # tabs Models, Rules, Sync, Downloads, Storage
├── pages/DriverModelPage.tsx
├── components/drivers/*.tsx                   # onboarding, component table and version history, roll back and custom component dialogs, rules, sync, downloads, storage
├── components/ImageEditorDialog.tsx           # + Windows release
├── components/ChunkedUploadDialog.tsx         # + Windows release
├── components/SessionProgressDetails.tsx      # + Install drivers card
├── components/drivers/DriverSetCell.tsx       # Driver set column on the Devices page
├── components/ui/select.tsx                   # + option group headings and trailing meta text
├── pages/SessionsPage.tsx                     # + Driver set column, overlay lines, choices on start
├── lib/drivers.ts                             # types, labels, API calls
├── lib/imagingProgress.ts                     # + InstallDrivers
└── lib/portalRoles.ts, components/Sidebar.tsx, App.tsx, lib/routeTitles.ts

src/deploy/bicep/                              # no change: Core creates the drivers container

tests/
├── CloudImaging.ImagingCoreApi.Tests/{Services/Drivers,Functions,Integration,Repositories}/
├── CloudImaging.ImagingCoreApi.Tests/TestDoubles/DriverTestBed.cs, InMemoryBlobContainerClient.cs
├── CloudImaging.OperatorApi.Tests/Contracts/DriverContractTests.cs
├── CloudImaging.DeviceGatewayApi.Tests/Contracts/DriverSetContractTests.cs
├── CloudImaging.Client.Tests/Drivers/, PrepareSetupServiceTests.cs, SetupRunnerScriptTests.cs
├── CloudImaging.MediaBuilder.Tests/DriverSetPreloadTests.cs
├── cloud-imaging-portal/server/drivers-routes.test.ts, role-route-matrix.test.ts
├── cloud-imaging-portal/client/drivers-*.test.tsx
├── validation/staging-smoke-test.ps1          # + -IncludeDrivers
└── fixtures/drivers/                          # real vendor catalogs

docs/drivers.md (new), docs/session-lifecycle.md, docs/troubleshooting-index.md,
docs/roles-and-access.md, .github/release-notes/{backend,client}/ (next version)
```

**Structure Decision**: Extend the six existing components; no new project or hosting. Driver
code sits in its own `Drivers` folders inside Core and the Client, the way spec 003 grouped setup
profile code. Media Builder gains only the optional Driver sets section for preloading the USB
drive's CACHE partition; its boot images need no change (WinPE-WMI and DISM are already in them;
quickstart section 8 confirms DISM in WinRE-based images).

### Delivery order

1. Contracts, tables, blob container, cabinet reader and vendor adapters over fixtures.
2. Policy resolver, set builder, vendor download and verification, sync service with the
   `driver-work` queue and scheduled-sync messages, clean-up (US1, US2, US4, US5, US7 foundations).
3. Portal backend and Operator API routes; Models tab, model page, draft onboarding and Complete
   onboarding; OS image Windows release (US1, US2).
4. Assignment pin, Device Gateway descriptor, Client Install drivers step for HP, timeline card
   (US1).
5. Specialize hand-off to Prepare setup (US3).
6. Sync tab and history, approvals, Rules tab with critical categories, Storage tab, roll back and
   removal (US2, US4, US5, US7).
7. Dell adapter end to end with PCI match (US6).
8. Driver set choice on the Devices page: assignment preview, manual choices on start, timeline
   line (US8).
9. Component version history and roll back (US9), then custom components with the staged upload
   and the Client ZIP extractor (US10).
10. Driver files on the USB drive: Client cache, then Media Builder preload (US11).
11. Docs, release notes, gates, hardware validation (quickstart sections 5 to 8).

## Complexity Tracking

| Exception | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| In-repo cabinet reader (MSZIP, LZX) | Vendor catalogs are cabinets; HP's reference files use LZX; Core runs on Linux | Native `libmspack` adds an LGPL native dependency; WixToolset needs Windows; a Windows Function App adds a host |

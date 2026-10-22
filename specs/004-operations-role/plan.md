# Implementation Plan: Operations Role

**Branch**: none created (work happens on the current branch; feature directory `specs/004-operations-role`) | **Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/004-operations-role/spec.md`

## Summary

Add a user app role `CloudImaging.Operations` between Technician and Administrator. Each of the three
role-aware applications (Portal backend, Portal frontend, Media Builder) gets the same implication
table (Administrator implies Operations, Operations implies Technician) and evaluates gates against
the expanded role set, because tokens carry only directly assigned roles. The Portal backend widens
31 route guards from Administrator-only (or Approver/Administrator/Reader allowlists) to include
Operations, keeps 18 routes Administrator-only, and names every accepted role in its guards so the
existing 403 detail stays accurate. It also closes a pre-existing gap: `GET /api/session-history`
stops accepting Technician, and a new completion-times read keeps the Technician Dashboard's
Completed Sessions stat working. The Portal frontend replaces raw role checks with a pure
capability map derived from the token roles. The Media Builder opens Generate Boot Image and
pre-production boot images to Operations and runs the boot media certificate check for it. The
Operator API changes only in documentation comments. Role creation stays a documented manual step;
no Bicep or script logic changes. The three release workflows publish their release body from a
notes file committed per stream and version, which carries the Operations notice. Touched files are
cleaned of em dash, en dash, middle dot and arrow characters.

---

## Technical Context

**Language/Version**:
- TypeScript on Node.js 22 (Express) for the Portal backend
- TypeScript, React 19 for the Portal frontend
- C# on .NET 10 (`net10.0-windows`, WPF) for the Media Builder
- C# on .NET 10 (`net10.0`) for the Operator API (XML doc comments and contract test messages only)

**Primary Dependencies**:
- Portal backend: existing `requireRole` middleware factory in `middleware/roleGuard.ts`, Express routers, `requireGuidParams`
- Portal frontend: `@azure/msal-react` (roles read from the ID token `roles` claim), React Router guards
- Media Builder: MSAL.NET result `ClaimsPrincipal` (ID token `roles` claim), WPF view models
- No new packages in any component

**Storage**: N/A. No persistence change. Role definitions and assignments live in Entra ID app
registrations and are created by hand.

**Testing**:
- Portal backend: Vitest + Supertest route tests (pattern of `tests/cloud-imaging-portal/server/autopilot-routes.test.ts`) with a mocked `operatorApiClient` and an injected-claims auth stand-in
- Portal frontend: Vitest unit tests over a pure role/capability module (the client test suite has no DOM testing library, see `protected-route.test.tsx`)
- Media Builder: xUnit + FluentAssertions over a pure role module and the existing view model tests
- Manual end-to-end verification per role in a test tenant ([quickstart.md](quickstart.md))

**Target Platform**: Azure App Service (Portal backend), Azure Static Web Apps (Portal frontend),
Windows technician workstation (Media Builder). No change to the Operator API runtime behaviour.

**Project Type**: Polyglot web application plus desktop client; authorization change across existing components.

**Performance Goals**: No measurable change. Role expansion is a constant-size set operation per request.

**Constraints**:
- Tokens carry only directly assigned roles (including group-based assignments); every app expands roles itself
- Additive only: Administrator, Technician, Reader and Autopilot Approver keep exactly their current access (FR-005), except that Technician loses direct API access to Reports-only data the Portal never showed them (FR-015a)
- Server is the authority; UI gating mirrors it (FR-022)
- The 403 problem detail must name every role a route accepts (FR-016)
- User-facing strings: ASCII punctuation only, no requirement or task identifiers (FR-039, constitution IV)
- Touched files contain no em dash, en dash, middle dot, right arrow or long right arrow in docs, UI text, log messages or comments; navigation paths use "A > B", the hierarchy uses "implies" (FR-040)
- No Bicep, deployment script or verification script logic change (FR-031, FR-033, FR-035). Release workflows change only in where the release body comes from (FR-034)
- Portal registration and Media Builder registration are evaluated independently (FR-030, US3 AS6)

**Scale/Scope**:
- Portal backend: 73 guarded routes in 17 routers (72 existing plus 1 new). 31 widened by edit, 1 narrowed (`GET /api/session-history` drops Technician), 1 new (`GET /api/session-history/completed-timestamps`), 2 message-only edits, 20 widened implicitly through the implication table, 18 unchanged Administrator-only ([contracts/portal-server-routes.md](contracts/portal-server-routes.md))
- Portal frontend: 10 files; 14 route guards plus the access-denied gate, 9 navigation items, 7 Dashboard cards plus 2 fetches, in-page controls on 4 pages, 3 user-facing strings ([contracts/portal-client-surfaces.md](contracts/portal-client-surfaces.md))
- Media Builder: 5 gates and 15 user-facing string occurrences across 7 files ([contracts/media-builder-gates.md](contracts/media-builder-gates.md))
- Documentation: `docs/roles-and-access.md`, `docs/setup-instructions.md`, `docs/upgrade-instructions.md`, `docs/image-upload-pipeline.md` (upload and pre-production roles), `README.md` (role wording, boot image step, link separators), `CONTRIBUTING.md` (release notes convention), one tooltip in `src/deploy/uiFormDefinition.json` (all five Portal user roles)
- Release: 3 workflows read the body from `.github/release-notes/<stream>/<version>.md`; 3 notes files written at `v1.1.0` for the releases that ship this feature (backend, client, mediabuilder; only backend and mediabuilder carry the Operations notice)
- Character cleanup: 24 touched files contain banned characters, plus `docs/image-upload-pipeline.md`, which is touched but clean today (see Character cleanup below)

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result | Enforcement in this feature |
|---|---|---|
| I. Zero-Warning .NET Build | PASS | Media Builder and Operator API changes build in Release with zero warnings; no suppressions. Renamed view model parameters update every caller and test. |
| II. Test-First Development | PASS, enforced by T036, T042, T053, T067 | Implication-table tests, the route matrix test, client capability tests and Media Builder role tests are written first and fail against the current code (Operations unknown), then pass. Coverage is measured, not assumed: T036 and T042 run `npm run test:coverage` in `src/cloud-imaging-portal/server` and `src/cloud-imaging-portal/client`, T053 runs `dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release --collect "XPlat Code Coverage"` (collector added by T073), and T067 repeats all three. Gate: at least 80% line coverage on this feature's new and changed code paths: `server/src/middleware/roleGuard.ts`, `server/src/routes/session-history.ts` (new completed-timestamps route), `client/src/lib/portalRoles.ts` and `MediaBuilder/Services/MediaBuilderRoles.cs`. The client config already enforces a global 80% line threshold (`vite.config.ts`); the server config sets none, so per-file values are read from the text report. Route tests mock every service a router imports, so unit tests make no network calls (T009). |
| III. Integration & End-to-End Testing | PASS | A Supertest route matrix exercises all 73 authenticated Portal backend routes for every role, satisfying "integration tests covering all authenticated routes". Media Builder logic is covered headlessly; the shell's certificate-check trigger is a documented manual verification item. Real-tenant verification per role is in [quickstart.md](quickstart.md). |
| IV. UX Consistency | PASS | New and changed strings use ASCII punctuation and name roles plainly. Banned glyphs already present in touched files are removed (FR-040), including the boot image and upload file labels. Narrowing `GET /api/session-history` needs no deprecation notice: it is the Portal's internal backend-for-frontend API, not a user-facing interface (see Complexity Tracking). |
| V. Performance | PASS | No new I/O; set lookups only. |
| .NET Diagnostic Policy | PASS | No `NoWarn`, no `#pragma`, nullable stays enabled, targets unchanged. |
| Development Workflow & Gates | PASS | Build, unit, lint (`npm run lint` both portal packages, `dotnet format --verify-no-changes`), and portal/Media Builder test suites run before merge. |

**Gate: PASSED. Proceeding to Phase 0.**

### Post-Design Constitution Re-check

The design adds two small pure modules (one per TypeScript client, one in the Media Builder) solely
so role logic is unit-testable without a DOM or a signed-in MSAL result. That is the minimum needed
for Principle II and adds no runtime layer. No violations.

**Gate: PASSED after Phase 1 design.**

---

## Project Structure

### Documentation (this feature)

```text
specs/004-operations-role/
├── spec.md
├── plan.md                              # This file
├── research.md                          # Phase 0 decisions
├── data-model.md                        # Role, implication and capability model
├── quickstart.md                        # Manual and automated verification per role
├── contracts/
│   ├── portal-server-routes.md          # All 73 routes: old and new accepted roles
│   ├── portal-client-surfaces.md        # Route guards, navigation, Dashboard, page controls, strings
│   └── media-builder-gates.md           # Media Builder gates and strings
├── checklists/
└── tasks.md                             # Phase 2 output (/speckit.tasks), not created here
```

### Source Code (files to change)

```text
src/cloud-imaging-portal/server/src/
├── middleware/roleGuard.ts              # PortalRole union, ROLE_IMPLICATIONS closure, requireOperations guard
└── routes/
    ├── images.ts                        # 6 writes: Operations or Administrator
    ├── boot-images.ts                   # 5 writes incl. promote/demote
    ├── recovery-images.ts               # 3 writes
    ├── upload-jobs.ts                   # upload status read
    ├── locations.ts                     # 4 writes incl. Wi-Fi assignment
    ├── regions.ts                       # read and write
    ├── wifi-networks.ts                 # 5 routes
    ├── autopilot.ts                     # canView (message), canAudit, canDecide, GET group-tags
    └── session-history.ts               # Reports read: Reader, Operations, Administrator; NEW completed-timestamps read

src/cloud-imaging-portal/client/src/
├── lib/portalRoles.ts                   # NEW: implication table, expandRoles, capabilitiesFromRoles
├── context/authContext.tsx              # PortalRole union, expose capabilities from portalRoles
├── App.tsx                              # route guards keyed on capabilities; Locations/Wi-Fi guard
├── components/Sidebar.tsx               # 'manage' access level for Locations and Wi-Fi Networks
├── pages/DashboardPage.tsx              # same access levels for cards and the stats fetch; Completed Sessions reads completed-timestamps
├── pages/OsImagesPage.tsx               # isAdministrator replaced by canManageCatalog
├── pages/BootImagesPage.tsx             # canManageCatalog; demote impact text names Operations; file label without middle dot
├── pages/RecoveryImagesPage.tsx         # canManageCatalog; file label without middle dot
├── pages/ReportsPage.tsx                # doc comment only
└── pages/AutopilotRegistrationsPage.tsx # doc comment only (flags come from context)

src/CloudImaging.MediaBuilder/
├── Services/MediaBuilderRoles.cs        # NEW: role constants, implication table, Expand
├── Services/EntraAuthenticationService.cs   # CanGenerateBootImage, CanSeePreProductionBootImages, HasMediaBuilderRole
├── Services/OperatorApiErrorDescription.cs  # access-denied text names Operations
├── ViewModels/ShellViewModel.cs         # gate and certificate check on CanGenerateBootImage; reason texts
├── ViewModels/OperationSelectionViewModel.cs  # parameter canGenerateBootImage; reason texts
├── ViewModels/PrepareStorageDeviceViewModel.cs # VisibleBootImages(canSeePreProduction); status text; boot image label without middle dots
└── Views/OperationSelectionView.xaml    # tile captions and banners

src/CloudImaging.OperatorApi/Functions/  # XML doc comments only (FR-025)
├── ImageCatalogFunctions.cs
├── BootImageLifecycleFunctions.cs
├── LocationFunctions.cs
├── WifiNetworkFunctions.cs
└── AutopilotFunctions.cs

src/deploy/uiFormDefinition.json         # Portal client ID tooltip lists all five Portal user roles (text only)

.github/
├── workflows/release-iac.yml            # stage .github/release-notes/backend/<version>.md; body_path
├── workflows/release-client.yml         # same, stream client
├── workflows/release-mediabuilder.yml   # same, stream mediabuilder (fixed body text moves into the notes file)
└── release-notes/                       # NEW: one notes file per stream and version
    ├── backend/v1.1.0.md                # Operations notice: Portal and Media Builder registrations, assign, upgrade link
    ├── client/v1.1.0.md                 # stream description and artifact table only (no user roles)
    └── mediabuilder/v1.1.0.md           # Operations notice: Media Builder registration, Generate Boot Image

.vscode/tasks.json                       # release tag task `detail` text names the notes file (text only)
Directory.Packages.props                 # coverlet.collector version (coverage for the Media Builder tests)
README.md                                # user roles incl. Operations, boot image step, link separators without middle dots
CONTRIBUTING.md                          # release notes come from .github/release-notes/<stream>/<version>.md

docs/
├── roles-and-access.md                  # seven roles, Operations columns/rows, hierarchy with "implies", Technician refused Reports data, troubleshooting
├── setup-instructions.md                # role lists, persona group, assignment table, Phase 5 prerequisite
├── upgrade-instructions.md              # release-specific step: add and assign the role; rollback note
└── image-upload-pipeline.md             # upload, publish, abandon, delete, polling and pre-production visibility: Operations or Administrator

tests/cloud-imaging-portal/server/
├── role-hierarchy.test.ts               # Operations implications, closure check
├── role-route-matrix.test.ts            # NEW: every route x every role, driven by the contracts table
├── session-history-routes.test.ts       # NEW: Technician 403 on session history; completed-timestamps returns only SessionCompleted times
├── role-enforcement.test.ts             # tautological assertions replaced by the matrix (see research R9)
├── image-management.test.ts             # tautological role assertions removed (matrix asserts them)
├── os-image-chunked-upload.test.ts      # same
├── branding.test.ts                     # same
├── portal-configuration.test.ts         # same
├── autopilot-routes.test.ts             # Operations cases
├── regions-routes.test.ts               # Operations cases
└── wifi-networks-routes.test.ts         # Operations cases

tests/cloud-imaging-portal/client/
├── portal-roles.test.ts                 # NEW: expansion and capability matrix
├── auth-guard.test.tsx                  # tautological checks replaced by portalRoles assertions
└── protected-route.test.tsx             # auth context mock without the removed raw members

tests/CloudImaging.MediaBuilder.Tests/
├── CloudImaging.MediaBuilder.Tests.csproj # coverlet.collector reference
├── MediaBuilderRolesTests.cs            # NEW: expansion and capabilities
├── OperationSelectionViewModelTests.cs  # renamed parameter, new reason texts, Operations cases
└── PrepareStorageDeviceViewModelTests.cs # pre-production visibility for Operations

tests/CloudImaging.OperatorApi.Tests/Contracts/
├── BootImageLifecycleContractTests.cs   # assertion reason text names Operations or Administrator
└── ImageCatalogContractTests.cs         # same
```

Not changed: `AppRoleAuthorizationMiddleware.cs` (service roles only, FR-006, FR-024),
`src/deploy/scripts/verify-app-registrations.ps1` (FR-033), every Bicep template and deployment
script (FR-035), `SessionsPage.tsx` pre-flight controls, `UpdateAvailableBanner.tsx`,
`DeploymentConfigPage.tsx`, `BrandingPage.tsx`, the Report pages (they keep calling
`GET /api/session-history`), the Operator API session history route.

### Character cleanup (FR-040)

Every occurrence of U+2014 (em dash), U+2013 (en dash), U+00B7 (middle dot), U+2192 (right arrow)
and U+27F9 (long right arrow) in these touched files is replaced. Navigation paths become
"Entra ID > Enterprise applications > Cloud Imaging Portal"; the hierarchy line becomes
"`Administrator` implies `Operations`, which implies `Technician`, which implies `PortalAccess`";
ranges such as "15 to 55%"; elsewhere a period, comma, colon or parentheses. Counts are lines
with a hit as of 2026-10-22.

| File | Lines | Characters | Kind |
|---|---|---|---|
| `docs/setup-instructions.md` | 33 | U+2192 | Navigation paths |
| `docs/roles-and-access.md` | 7 | U+2192 (6), U+27F9 (1) | Navigation paths, hierarchy line |
| `docs/upgrade-instructions.md` | 1 | U+2192 | Navigation path |
| `src/CloudImaging.MediaBuilder/ViewModels/PrepareStorageDeviceViewModel.cs` | 36 | U+00B7 (2), U+2013 (2), U+2014 (32) | Boot image list label (UI), comments |
| `src/cloud-imaging-portal/client/src/pages/BootImagesPage.tsx` | 4 | U+00B7, U+2192, U+2014 (2) | Upload file label (UI), comments |
| `src/cloud-imaging-portal/client/src/pages/RecoveryImagesPage.tsx` | 3 | U+00B7, U+2192, U+2014 | Upload file label (UI), comments |
| `src/CloudImaging.OperatorApi/Functions/BootImageLifecycleFunctions.cs` | 5 | U+2192 (3), U+2014 (2) | Doc comments, one log message |
| `src/CloudImaging.OperatorApi/Functions/LocationFunctions.cs` | 1 | U+2014 | Log message |
| `.github/workflows/release-mediabuilder.yml` | 1 | U+2192 | Release body (moves into the notes file) |
| `src/CloudImaging.MediaBuilder/ViewModels/ShellViewModel.cs` | 7 | U+2014 | Comments |
| `src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs` | 3 | U+2014 | Comments |
| `src/CloudImaging.MediaBuilder/Views/OperationSelectionView.xaml` | 3 | U+2014 | XML comments |
| `src/CloudImaging.MediaBuilder/ViewModels/OperationSelectionViewModel.cs` | 1 | U+2014 | Comment |
| `src/cloud-imaging-portal/client/src/context/authContext.tsx` | 3 | U+2014 | Comments |
| `src/cloud-imaging-portal/client/src/pages/DashboardPage.tsx` | 3 | U+2014 | Comments |
| `src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx` | 1 | U+2014 | Comment |
| `src/cloud-imaging-portal/server/src/routes/session-history.ts` | 2 | U+2014 | Comment (rewritten anyway) |
| `src/cloud-imaging-portal/server/src/middleware/roleGuard.ts` | 1 | U+2014 | Comment |
| `src/cloud-imaging-portal/server/src/routes/images.ts` | 1 | U+2014 | Comment |
| `src/cloud-imaging-portal/server/src/routes/upload-jobs.ts` | 1 | U+2014 | Comment |
| `tests/cloud-imaging-portal/server/role-enforcement.test.ts` | 1 | U+2192 | Comment |
| `tests/CloudImaging.MediaBuilder.Tests/PrepareStorageDeviceViewModelTests.cs` | 1 | U+2014 | Comment |
| `README.md` | 11 | U+00B7 | Link separators in the header link rows (lines 12 to 25) |
| `Directory.Packages.props` | 4 | U+2014 | XML comments (touched by the coverage collector entry) |
| `docs/image-upload-pipeline.md` | 0 | none | Touched for role wording; must stay clean |

The other touched files have no hits today. New files must stay clean. No test asserts on the
changed labels: the Media Builder tests build `BootImageChoice` labels themselves and no portal
client test references the upload file label, so no test expectation changes.

**Structure Decision**: The feature stays inside the existing six-component architecture. Each
role-aware application owns one implication table and one place that turns roles into
capabilities; route and view code consumes capabilities instead of raw role names.

---

## Implementation Order

1. Tests first: server implication and route matrix (including the session history narrowing and the new read), client capability tests, Media Builder role tests (all red).
2. Portal backend: `roleGuard.ts`, then routers. Matrix goes green.
3. Portal frontend: `portalRoles.ts`, `authContext.tsx`, guards, navigation, Dashboard (including the completed-timestamps read), pages, strings.
4. Media Builder: `MediaBuilderRoles.cs`, auth service, view models, XAML strings.
5. Operator API doc comments and contract test messages.
6. Documentation and tooltip; release workflows switched to notes files; notes files for the backend, client and Media Builder releases written; CONTRIBUTING.md describes the convention.
7. Character cleanup of every touched file, then the explicit-list scan in [quickstart.md](quickstart.md) section 8 that must return zero hits (FR-040).
8. Manual verification per [quickstart.md](quickstart.md) in a test tenant.

---

## Release Considerations

- Portal backend and frontend ship together in the backend stream (`mse-ci-v#.#.#`); the Media Builder ships in its own stream. Until both are upgraded, an Operations user sees Operations behaviour only in the upgraded app. The upgrade instructions say to upgrade both before assigning Operations.
- The role can be created before or after the upgrade. Unknown roles are ignored by old code; missing roles change nothing for new code.
- Release notes: inside "Determine version tag", each workflow checks and stages `.github/release-notes/<stream>/<version>.md` right after the tag pattern check and before the existence check that creates and pushes a missing tag on manual runs (stream `backend`, `client` or `mediabuilder`; `<version>` is the tag minus its stream prefix, for example `v1.1.0`), and publishes it with `body_path`. A missing or empty file fails the run before any tag is created and before any build. The fixed stream description and artifact table move into the notes files, so the published body equals the file. The notes file must be on `main` before the tag task runs, because the tag tasks tag `origin/main`. Prerelease tags (for example `v1.1.0-beta.1`) need their own file. The `-latest` alias release bodies stay as they are. See research R14.
- The Technician change to `GET /api/session-history` and the new completed-timestamps read ship together in the backend stream, so the Dashboard never calls a route the backend does not have.

---

## Complexity Tracking

No constitution violations or complexity waivers. One item is recorded so it is not mistaken for one:

| Item | Principle | Why no deprecation notice is required |
|---|---|---|
| `GET /api/session-history` stops accepting Technician (FR-015a) | IV, breaking changes to a user-facing interface need a prior deprecation notice | The route is the Portal's internal backend-for-frontend API, called only by the Portal frontend that ships in the same backend stream; it is not a public or user-facing interface. Its one Technician consumer, the Dashboard's Completed Sessions stat, moves to `GET /api/session-history/completed-timestamps` in the same release, so Technicians see no UI change. |

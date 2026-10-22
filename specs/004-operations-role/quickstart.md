# Quickstart: Verify the Operations Role

**Feature**: [spec.md](spec.md) | **Contracts**: [contracts/](contracts/)

Run in a **test tenant and test deployment**. Never paste tokens into tickets, chat or logs.

---

## 1. Automated checks (local)

From the repository root:

```powershell
cd src/cloud-imaging-portal/server; npm run lint; npm run test:run; cd ../../..
cd src/cloud-imaging-portal/client; npm run lint; npx tsc --noEmit; npm run test:run; cd ../../..
dotnet build CloudImaging.sln -c Release
dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release
dotnet test tests/CloudImaging.OperatorApi.Tests -c Release
```

Expected: zero lint errors, zero build warnings, all tests pass. The server suite includes the
route matrix (all 73 routes against Administrator, Operations, Technician, Reader, Approver, and
Operations plus Technician) from [contracts/portal-server-routes.md](contracts/portal-server-routes.md),
and the session history tests (Technician refused on `GET /api/session-history`; the
completed-timestamps read returns only `SessionCompleted` times). Route tests mock
`operatorApiClient` and `updateCheck`, so no test makes a network call.

Coverage (constitution Principle II):

```powershell
cd src/cloud-imaging-portal/server; npm run test:coverage; cd ../../..
cd src/cloud-imaging-portal/client; npm run test:coverage; cd ../../..
dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release --collect "XPlat Code Coverage" --results-directory TestResults/coverage-mediabuilder
$CoverageFile = Get-ChildItem TestResults/coverage-mediabuilder -Recurse -Filter coverage.cobertura.xml | Sort-Object LastWriteTime | Select-Object -Last 1
$CoverageXml = [xml](Get-Content -Raw $CoverageFile.FullName)
$CoverageXml.coverage.packages.package.classes.class | Where-Object { $_.filename -like "*MediaBuilderRoles.cs" } | ForEach-Object { "$($_.name): $([math]::Round([double]$_.'line-rate' * 100, 1))% lines" }
```

Gate: at least 80% line coverage on the new and changed code paths of this feature, read from
the `% Lines` column of the Vitest text report and from the Cobertura `line-rate`:

| Component | File or class | Source of the number |
|---|---|---|
| Portal server | `src/middleware/roleGuard.ts` | server text report |
| Portal server | `src/routes/session-history.ts` (new completed-timestamps route) | server text report |
| Portal client | `src/lib/portalRoles.ts` | client text report |
| Media Builder | `CloudImaging.MediaBuilder.Services.MediaBuilderRoles` | Cobertura snippet above |

The client run also enforces its configured global 80% line threshold (`vite.config.ts`); the
server config has no threshold, so its per-file values are checked by reading the report. The
Media Builder run needs the `coverlet.collector` package reference added by the feature.

---

## 2. Tenant setup

1. Deploy the release (backend stream) and install the matching Media Builder.
2. Add `CloudImaging.Operations` to the **Cloud Imaging Portal** and **Cloud Imaging Media Builder**
   registrations as the updated setup instructions describe (Users/Groups, value and display name
   `CloudImaging.Operations`). Confirm the existing roles, their IDs and assignments are untouched.
3. Create five test users, each assigned exactly one role on the Portal registration:

   | Test user | Portal role | Media Builder role | Operator API |
   |---|---|---|---|
   | `ci-admin` | Administrator | Administrator | MediaBuilderAccess |
   | `ci-ops` | Operations | Operations | MediaBuilderAccess |
   | `ci-tech` | Technician | Technician | MediaBuilderAccess |
   | `ci-reader` | Reader | none | none |
   | `ci-approver` | AutopilotApprover | none | none |

4. Optional: `ci-ops-mbonly` with Operations on Media Builder only (no Portal role beyond Technician)
   for the cross-registration check.
5. Each user signs out and back in after assignment.

---

## 3. Direct API probes

The probes are side-effect free: each is a read, or uses an invalid id so that a request passing
the role guard stops at GUID validation (400) before reaching the backend. "pass" means any status
other than 401 or 403.

Get a token: sign in to the Portal as the test user, open DevTools, Network, select any `/api/`
request, and copy the `Authorization` header value after `Bearer `.

```powershell
$Base = "https://<portal-host>/api"
$Token = Read-Host -Prompt "Bearer token" -MaskInput
$Headers = @{ Authorization = "Bearer $($Token)" }
$Probes = @("GET /regions", "GET /wifi-networks", "GET /upload-jobs/x", "PATCH /images/x", "DELETE /images/x", "POST /images/upload/x/publish", "POST /images/upload/x/abandon", "POST /boot-images/x/promote", "POST /boot-images/x/demote", "DELETE /boot-images/x", "POST /boot-images/upload/x/publish", "DELETE /recovery-images/x", "POST /recovery-images/upload/x/publish", "PUT /locations/x", "DELETE /locations/x", "PUT /locations/x/wifi-networks", "PUT /wifi-networks/x", "DELETE /wifi-networks/x", "GET /autopilot/history", "GET /autopilot/group-tags", "POST /autopilot/registrations/x/approve", "POST /autopilot/registrations/x/reject", "POST /autopilot/registrations/x/retry", "GET /portal-config", "GET /cert/active", "GET /update-check", "POST /sessions/x/preflight-override", "PUT /autopilot/group-tags/x", "DELETE /autopilot/group-tags/x", "GET /sessions", "GET /locations", "GET /session-history", "GET /session-history/completed-timestamps")
foreach ($Probe in $Probes) { $Method, $Path = $Probe -split " ", 2; $Response = Invoke-WebRequest -Method $Method -Uri "$($Base)$($Path)" -Headers $Headers -SkipHttpErrorCheck; "$($Response.StatusCode) $($Probe)" }
```

**Expected results**:

| Probe group | Admin | Ops | Tech | Reader | Approver |
|---|---|---|---|---|---|
| Catalog, upload status, Locations, regions, Wi-Fi (first 18 probes) | pass | pass | 403 | 403 | 403 |
| `GET /autopilot/history` | pass | pass | 403 | pass | pass |
| `GET /autopilot/group-tags` | pass | pass | 403 | 403 | pass |
| Autopilot approve, reject, retry | pass | pass | 403 | 403 | pass |
| `GET /portal-config`, `GET /cert/active`, `GET /update-check` | pass | 403 | 403 | 403 | 403 |
| `POST /sessions/x/preflight-override`, group tag `PUT`/`DELETE` | pass | 403 | 403 | 403 | 403 |
| `GET /sessions`, `GET /locations` | pass | pass | pass | 403 | 403 |
| `GET /session-history` | pass | pass | 403 | pass | 403 |
| `GET /session-history/completed-timestamps` | pass | pass | pass | pass | 403 |

Every 403 body must list the accepted roles, for example
`CloudImaging.Operations, CloudImaging.Administrator` on catalog probes and
`CloudImaging.Reader, CloudImaging.Operations, CloudImaging.Administrator` on `GET /session-history`.
The Technician 403 on `GET /session-history` is the one intended change for an existing role
(FR-015a); before the release it returned 200.

**Test tenant only** (these would change data if a guard were wrong): as `ci-ops`, `PUT /branding`,
`PUT /branding/logo`, `DELETE /branding/logo`, `POST /cert/generate`, `POST /cert/rotate`,
`PUT /portal-config`, `PUT /partitioning-scheme`, `POST /update-check/refresh`,
`POST /autopilot/group-tags` and `DELETE /preflight-overrides?serialNumber=OPS-PROBE-0000` must all
return 403. The route matrix test already covers them; run these only to confirm a deployment.

---

## 4. Portal UI per role

**Administrator** (`ci-admin`): everything as before. Navigation shows every item including Branding
and Configuration. No Operations assignment needed.

**Operations** (`ci-ops`):
1. Navigation: Dashboard, Devices, OS Images, Boot Images, Recovery Images, Autopilot, Reports, Locations, Wi-Fi Networks, and Drivers once spec 005 ships. No Branding, no Configuration. No update notice.
2. Dashboard shows the operations stats and the Locations card; no Branding or Configuration cards.
3. OS Images: upload a small test image, edit its metadata, delete it. Capacity and in-use rules apply.
4. Boot Images: upload a test image (stays pre-production), promote it, demote it, delete it.
5. Recovery Images: upload and delete a test image.
6. Locations: add, edit and delete a test location; change one country's region and revert it; assign a Wi-Fi network.
7. Wi-Fi Networks: add a network, edit it, reorder, delete. No password is ever displayed or present in responses.
8. Autopilot: approve, reject or retry a pending test request; open Autopilot Registration History and export CSV.
9. Reports: open all five reports.
10. Open `/branding` and `/configuration` by URL: both redirect to the Dashboard.
11. Devices with pre-flight authorization on: no approve or revoke controls.
12. Header location picker and Devices location filter work.

**Technician** (`ci-tech`): unchanged. Catalogs read-only, no Locations or Wi-Fi pages (redirect),
no Reports (`/reports` redirects), Autopilot queue read-only. The Dashboard's Completed Sessions
stat shows the same count as before the release, and DevTools shows it loading from
`/api/session-history/completed-timestamps` with no 403 in the Network tab.

**Reader** (`ci-reader`): unchanged. Dashboard (Completed Sessions only) and Reports including
Autopilot Registration History.

**Autopilot Approver** (`ci-approver`): unchanged. Lands on Autopilot, can decide, opens the history
report from the Autopilot page.

**Combinations**: Operations plus Technician shows the Operations navigation once, without duplicates.

---

## 5. Media Builder per role

| User | Expected on Home | Prepare USB boot image list |
|---|---|---|
| `ci-admin` | Generate Boot Image available (ADK and certificate present) | Production and pre-production |
| `ci-ops` | Generate Boot Image available (ADK and certificate present) | Production and pre-production |
| `ci-ops` on a workstation without the ADK | ADK reason, not a role reason | Production and pre-production |
| `ci-ops` with no active certificate in the Portal | Certificate reason; generation blocked | Production and pre-production |
| `ci-tech` | Restricted by role, names Operations or Administrator | Production only |
| user with no Media Builder role | No-role banner naming Technician, Operations or Administrator; both tiles blocked | n/a |

Then, as `ci-ops`: generate a boot image, upload it on the Portal's Boot Images page, prepare a test
USB with it while pre-production, promote it. As `ci-ops-mbonly`, the Portal upload is refused.

Manual-only item: the certificate check runs at shell start for Operations (no automated harness for
`ShellViewModel`). Verify by removing the active certificate in a test deployment and confirming the
certificate reason appears for `ci-ops`.

---

## 6. Regression baseline

Run sections 3 and 4 for Administrator, Technician, Reader and Approver before the upgrade and
after it. Results must be identical (SC-003), except the Technician `GET /session-history` probe,
which changes from pass to 403 (FR-015a).

---

## 7. Release notes

Before running a tag task, confirm the notes file for that stream and version is on `main`
(the tag tasks tag `origin/main`). Tag prefixes per stream: `backend` is `mse-ci-`, `client` is
`mse-ci-client-`, `mediabuilder` is `mse-ci-mediabuilder-` (release workflows and
`.vscode/tasks.json`).

```powershell
$Version = "v1.1.0"
$Streams = [ordered]@{ backend = "mse-ci-"; client = "mse-ci-client-"; mediabuilder = "mse-ci-mediabuilder-" }
foreach ($Stream in $Streams.Keys) { "$($Stream): $(Test-Path ".github/release-notes/$($Stream)/$($Version).md")" }
```

After each workflow finishes, the published body must equal the file:

```powershell
foreach ($Stream in $Streams.Keys) {
    $Published = (gh release view "$($Streams[$Stream])$($Version)" --json body --jq .body | Out-String).Trim()
    $Committed = (Get-Content -Raw ".github/release-notes/$($Stream)/$($Version).md").Trim()
    "$($Stream): $($Published -eq $Committed)"
}
```

Expected: `True` for `backend` (`mse-ci-v1.1.0`), `client` (`mse-ci-client-v1.1.0`) and
`mediabuilder` (`mse-ci-mediabuilder-v1.1.0`). The backend and Media Builder notes for this
feature tell administrators to add `CloudImaging.Operations` to the Portal and Media Builder
registrations, assign it, and link to the upgrade instructions; the client notes carry no
Operations notice. A manual workflow run for a version without a notes file must fail inside
"Determine version tag", naming the expected path, before the tag is created or pushed and before
any build. A prerelease tag (for example `v1.1.0-beta.1`) needs its own notes file.

---

## 8. Character scan and tooltip

Scan an explicit file list, not `git diff` against `main` (the branch carries unrelated work).
The list is the plan's Character cleanup table, plus every file in the plan's Source Code list,
plus the new files of this feature (release notes files, new tests, new modules). Run from the
repository root:

```powershell
$ScanFiles = @(
    # Repository root, docs, deployment form, workflows, release notes
    "README.md", "CONTRIBUTING.md", ".vscode/tasks.json", "Directory.Packages.props",
    "docs/roles-and-access.md", "docs/setup-instructions.md", "docs/upgrade-instructions.md", "docs/image-upload-pipeline.md",
    "src/deploy/uiFormDefinition.json",
    ".github/workflows/release-iac.yml", ".github/workflows/release-client.yml", ".github/workflows/release-mediabuilder.yml",
    ".github/release-notes/backend/v1.1.0.md", ".github/release-notes/client/v1.1.0.md", ".github/release-notes/mediabuilder/v1.1.0.md",
    # Portal server
    "src/cloud-imaging-portal/server/src/middleware/roleGuard.ts",
    "src/cloud-imaging-portal/server/src/routes/images.ts", "src/cloud-imaging-portal/server/src/routes/boot-images.ts",
    "src/cloud-imaging-portal/server/src/routes/recovery-images.ts", "src/cloud-imaging-portal/server/src/routes/upload-jobs.ts",
    "src/cloud-imaging-portal/server/src/routes/locations.ts", "src/cloud-imaging-portal/server/src/routes/regions.ts",
    "src/cloud-imaging-portal/server/src/routes/wifi-networks.ts", "src/cloud-imaging-portal/server/src/routes/autopilot.ts",
    "src/cloud-imaging-portal/server/src/routes/session-history.ts",
    # Portal client
    "src/cloud-imaging-portal/client/src/lib/portalRoles.ts", "src/cloud-imaging-portal/client/src/context/authContext.tsx",
    "src/cloud-imaging-portal/client/src/App.tsx", "src/cloud-imaging-portal/client/src/components/Sidebar.tsx",
    "src/cloud-imaging-portal/client/src/pages/DashboardPage.tsx", "src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx",
    "src/cloud-imaging-portal/client/src/pages/BootImagesPage.tsx", "src/cloud-imaging-portal/client/src/pages/RecoveryImagesPage.tsx",
    "src/cloud-imaging-portal/client/src/pages/ReportsPage.tsx", "src/cloud-imaging-portal/client/src/pages/AutopilotRegistrationsPage.tsx",
    # Media Builder
    "src/CloudImaging.MediaBuilder/Services/MediaBuilderRoles.cs", "src/CloudImaging.MediaBuilder/Services/EntraAuthenticationService.cs",
    "src/CloudImaging.MediaBuilder/Services/OperatorApiErrorDescription.cs", "src/CloudImaging.MediaBuilder/ViewModels/ShellViewModel.cs",
    "src/CloudImaging.MediaBuilder/ViewModels/OperationSelectionViewModel.cs", "src/CloudImaging.MediaBuilder/ViewModels/PrepareStorageDeviceViewModel.cs",
    "src/CloudImaging.MediaBuilder/Views/OperationSelectionView.xaml",
    # Operator API doc comments
    "src/CloudImaging.OperatorApi/Functions/ImageCatalogFunctions.cs", "src/CloudImaging.OperatorApi/Functions/BootImageLifecycleFunctions.cs",
    "src/CloudImaging.OperatorApi/Functions/LocationFunctions.cs", "src/CloudImaging.OperatorApi/Functions/WifiNetworkFunctions.cs",
    "src/CloudImaging.OperatorApi/Functions/AutopilotFunctions.cs",
    # Tests
    "tests/cloud-imaging-portal/server/role-hierarchy.test.ts", "tests/cloud-imaging-portal/server/role-route-matrix.test.ts",
    "tests/cloud-imaging-portal/server/session-history-routes.test.ts", "tests/cloud-imaging-portal/server/role-enforcement.test.ts",
    "tests/cloud-imaging-portal/server/autopilot-routes.test.ts", "tests/cloud-imaging-portal/server/regions-routes.test.ts",
    "tests/cloud-imaging-portal/server/wifi-networks-routes.test.ts", "tests/cloud-imaging-portal/server/image-management.test.ts",
    "tests/cloud-imaging-portal/server/os-image-chunked-upload.test.ts", "tests/cloud-imaging-portal/server/branding.test.ts",
    "tests/cloud-imaging-portal/server/portal-configuration.test.ts",
    "tests/cloud-imaging-portal/client/portal-roles.test.ts", "tests/cloud-imaging-portal/client/auth-guard.test.tsx",
    "tests/cloud-imaging-portal/client/protected-route.test.tsx",
    "tests/CloudImaging.MediaBuilder.Tests/CloudImaging.MediaBuilder.Tests.csproj", "tests/CloudImaging.MediaBuilder.Tests/MediaBuilderRolesTests.cs",
    "tests/CloudImaging.MediaBuilder.Tests/OperationSelectionViewModelTests.cs", "tests/CloudImaging.MediaBuilder.Tests/PrepareStorageDeviceViewModelTests.cs",
    "tests/CloudImaging.OperatorApi.Tests/Contracts/BootImageLifecycleContractTests.cs", "tests/CloudImaging.OperatorApi.Tests/Contracts/ImageCatalogContractTests.cs"
)
$Missing = @($ScanFiles | Where-Object { -not (Test-Path -LiteralPath $_) })
$Hits = @(Select-String -LiteralPath ($ScanFiles | Where-Object { Test-Path -LiteralPath $_ }) -Pattern "[\u2014\u2013\u00B7\u2192\u27F9]")
$Missing | ForEach-Object { "missing: $($_)" }
$Hits | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
"files: $($ScanFiles.Count), missing: $($Missing.Count), hits: $($Hits.Count)"
```

Expected: `files: 67, missing: 0, hits: 0`. A missing file means a new file of this feature was
not created or the list is out of date; update the list when the plan's lists change.

In the deployment wizard, the Portal client ID tooltip lists
`CloudImaging.Administrator`, `CloudImaging.Operations`, `CloudImaging.Technician`,
`CloudImaging.Reader` and `CloudImaging.AutopilotApprover`.

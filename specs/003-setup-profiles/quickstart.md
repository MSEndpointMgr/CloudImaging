# Quickstart: Verify Setup Profiles

**Feature**: [spec.md](spec.md) | **Contracts**: [contracts/](contracts/) | **Model**: [data-model.md](data-model.md)

Run tenant steps in a **test tenant and test deployment**. Never paste tokens, SAS URLs or script
content that may hold customer data into tickets, chat or logs.

---

## 1. Automated checks (local)

From the repository root:

```powershell
cd src/cloud-imaging-portal/server; npm run lint; npm run test:run; cd ../../..
cd src/cloud-imaging-portal/client; npm run lint; npx tsc --noEmit; npm run test:run; cd ../../..
dotnet build CloudImaging.sln -c Release
dotnet test tests/CloudImaging.ImagingCoreApi.Tests -c Release
dotnet test tests/CloudImaging.DeviceGatewayApi.Tests -c Release
dotnet test tests/CloudImaging.OperatorApi.Tests -c Release
dotnet test tests/CloudImaging.Client.Tests -c Release
dotnet test tests/CloudImaging.MediaBuilder.Tests -c Release
dotnet format CloudImaging.sln --verify-no-changes
```

Expected: zero lint errors, zero build warnings, all tests pass. In particular:

| Suite | Proves |
|---|---|
| `answer-file-validation.test.ts` and `AnswerFileValidatorTests` | Both implementations return the same codes, lines and messages for every case in `tests/fixtures/answer-file-cases.json` ([contracts/answer-file-rules.md](contracts/answer-file-rules.md)). |
| `role-route-matrix.test.ts` | The 10 new routes accept and refuse exactly the roles in [contracts/portal-server-routes.md](contracts/portal-server-routes.md). |
| `setup-profiles-routes.test.ts` | Actor comes from the token; 4 MB body accepted on this router only; upstream `errors` relayed and bounded. |
| `SetupProfileIntegrationTests` | Two concurrent saves targeting the same location: exactly one succeeds. Retention keeps pinned versions. Location delete refusal. |
| `OverallProgressCalculatorTests` | A five-step session completes as before; a session with PrepareSetup completes only when it is Completed; weight 0. |
| `UnattendComposerTests` | Exactly one runner command, Order 1, device-architecture component, profile commands renumbered from 2, other architectures untouched. |
| `PrepareSetupServiceTests` | Phase order; Complete with warnings vs Fail the deployment (NotRun items); no PowerShell; missing default user hive; non-generalized image. |
| `SetupRunnerScriptTests` | The real runner under `powershell.exe` against a temp manifest: hash mismatch skipped, timeout stops the tree, continues after failure, cleans up after failure, log lines ASCII. |
| `BootImageGenerationTests` | WinPE-WMI, WinPE-NetFx, WinPE-Scripting, WinPE-PowerShell in that order; clear message when a cab is missing. |
| `session-progress-details.test.tsx` | Old five-step sessions render unchanged; PrepareSetup appears when reported or expected; amber marker; Not run. |

Coverage (constitution Principle II), at least 80% line coverage on the new and changed code:

```powershell
cd src/cloud-imaging-portal/server; npm run test:coverage; cd ../../..
cd src/cloud-imaging-portal/client; npm run test:coverage; cd ../../..
dotnet test tests/CloudImaging.ImagingCoreApi.Tests -c Release --collect "XPlat Code Coverage" --results-directory TestResults/coverage-core
dotnet test tests/CloudImaging.Client.Tests -c Release --collect "XPlat Code Coverage" --results-directory TestResults/coverage-client
```

| Component | Files or classes |
|---|---|
| Portal server | `src/routes/setup-profiles.ts`, `src/middleware/roleGuard.ts` |
| Portal client | `src/lib/answerFileValidation.ts`, `src/lib/setupProfiles.ts`, `src/lib/imagingProgress.ts` |
| Imaging Core | `AnswerFileValidator`, `SetupProfileValidator`, `SetupProfileService`, `SetupProfileResolver`, `ChangeSummaryBuilder`, `OverallProgressCalculator` |
| Client | `UnattendComposer`, `SetupRunnerManifestBuilder`, `PrepareSetupService`, `OfflineRegistryPath` |

## 2. Text scan

Every file added or changed by the feature must be free of em dash (U+2014), en dash (U+2013),
middle dot (U+00B7), right arrow (U+2192) and smart quotes, and no user-facing string may carry a
requirement, criterion, task or story identifier:

```powershell
$ChangedFiles = git diff --name-only origin/main -- src tests docs .github | Where-Object { Test-Path $_ }
$BannedChars = '[\u2014\u2013\u00B7\u2192\u201C\u201D\u2018\u2019]'
Select-String -Path $ChangedFiles -Pattern $BannedChars | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
Select-String -Path $ChangedFiles -Pattern '\b(FR|SC)-\d{3}\b|\bT\d{3}\b|\bUS\d\b' | Where-Object { $_.Line -notmatch '^\s*(//|///|\*|#|<!--)' } | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
```

Expected: no output from the first command. Every hit of the second must be a code comment
(review by hand; the filter only skips lines that start with a comment marker).

---

## 3. Tenant setup

1. Publish the backend release and run the documented upgrade (template spec). Confirm the new
   container exists: `az storage container show --account-name <core storage> --name setup-profiles --auth-mode login`.
2. Install the matching Media Builder. Generate a boot image for x64 (ADK base) and one with a
   Windows source (Windows RE base). Generate an ARM64 boot image if the tenant uses ARM64.
3. Publish the boot images in the portal (Boot Images page), promote, and prepare USB media for two
   locations, "Stockholm HQ" and "Gothenburg Office", plus one without a location.
4. Sign-in accounts: one each for Administrator, Operator, Technician, Reader, Autopilot Approver.

## 4. Portal walkthrough

| # | As | Do | Expect |
|---|---|---|---|
| 4.1 | Operator | Open Imaging > Setup Profiles | Empty state with "New profile". |
| 4.2 | Operator | Create "Stockholm and Gothenburg": target Stockholm HQ and Gothenburg Office; add `HKLM SYSTEM` `CurrentControlSet\Control\BitLocker` `PreventDeviceEncryption` DWORD 1, `HKLM SOFTWARE` `Policies\Microsoft\FVE` `EncryptionMethodWithXtsOs` DWORD 7, `HKLM SYSTEM` `CurrentControlSet\Services\tzautoupdate` `Start` DWORD 3; add WinPE script "Enable Wake on LAN in HP firmware" and Windows setup script "Wired 802.1X for the office LAN"; save. Time it. | Saved as version 1 in under 5 minutes (SC-001). List row shows two location badges, 3, 2, absolute time. |
| 4.3 | Operator | New profile "Lab": open the location picker | Stockholm HQ and Gothenburg Office are disabled with "Targeted by Stockholm and Gothenburg". |
| 4.4 | Operator | Target All devices on "Lab", save; start another profile and open Targeting | "All devices" is disabled with "Already used by Lab". |
| 4.5 | Operator | Answer file: paste one with `RegisteredOrganization`, `TimeZone`, `InputLocale` | "Valid answer file. Passes: specialize, oobeSystem." Row shows passes, size, time. |
| 4.6 | Operator | Answer file: add a windowsPE pass and an UnattendedJoin component | Both problems listed with line numbers at once; Done and Save disabled. |
| 4.7 | Operator | Send the same file directly: `Invoke-RestMethod -Method Put -Uri https://<portal>/api/setup-profiles/<id> -Headers @{ Authorization = "Bearer $($Token)" } -ContentType 'application/json' -Body $Body` | 400 with the same two problems in `errors` (SC-003). |
| 4.8 | Operator | Edit a script twice more (versions 2, 3), open History | Versions tab with three rows, Current on 3; expanding 2 shows labelled fields with "No change" where nothing changed. Targeting tab shows the creation and targeting rows. |
| 4.9 | Operator | Restore version 1 | Version 4 created with version 1's content, Current; targeting unchanged. Time from opening History to restored: under 1 minute (SC-006). |
| 4.10 | Operator | Duplicate the profile | Editor opens with "(copy)" name, content, no targeting, Save disabled until targeting is set. |
| 4.11 | Technician | Open Setup Profiles and a profile | Visible, no edit controls, no save bar, History without Restore. API write returns 403 naming Operator and Administrator. |
| 4.12 | Reader, Approver | Open `/setup-profiles`; call `GET /api/setup-profiles` | No navigation item; access denied page; 403. |
| 4.13 | Administrator | Configuration > Miscellaneous: set versions kept to 4, save; then 3 | 4 saved; 3 refused with "Keep between 5 and 100 versions per profile." until set to 5 or more. |
| 4.14 | Operator | Locations: delete Gothenburg Office | Overlay names the profile; after delete the profile targets Stockholm HQ only and History shows the removal with your name. |
| 4.15 | Operator | Locations: delete Stockholm HQ (now the only location) | Refused, toast names the profile. |

## 5. Media Builder and boot image

| # | Do | Expect |
|---|---|---|
| 5.1 | Mount each generated `boot.wim` read-only and check `Windows\System32\WindowsPowerShell\v1.0\powershell.exe` | Present for x64 ADK, x64 Windows RE and ARM64 images. |
| 5.2 | Compare `boot.wim` size with a boot image generated by the previous Media Builder | Growth recorded per architecture in the release notes (estimate 20 to 40 MB). |
| 5.3 | Boot the reference minimum-spec device (2 GB RAM, dual core) from the new media; measure from Client process start to main window (Client log timestamps) | Within 5 seconds (constitution V); no change from the previous release beyond noise. |
| 5.4 | Boot a device from media built by the previous Media Builder with a profile that has a WinPE script | Script item fails with "This boot image has no PowerShell, so the script cannot run. ..."; failure handling decides the result (FR-017). |

## 6. Device walkthrough

Use one Windows 11 23H2, one 24H2 and one 25H2 OS image (spec risk 1), x64, and ARM64 where available.

| # | Do | Expect |
|---|---|---|
| 6.1 | Couple a device from Stockholm HQ media, one from media without a location, press Start imaging | Overlay: "Stockholm and Gothenburg: 1 device" and "Lab: 1 device" (All devices), no versions (US5). |
| 6.2 | While the device downloads, save a new version of the profile | Device still gets the version pinned at Start imaging (SC-002); History shows the pinned version "In use". |
| 6.3 | Watch the device | Step list shows Prepare setup after Apply image; status lines change per phase; UI responsive throughout. |
| 6.4 | Portal timeline after completion | Prepare setup card names the profile and version; summary counts; "Show items" lists each registry value, the WinPE script with exit code and duration, the Windows setup script as queued. |
| 6.5 | First boot, before sign-in: open a command prompt in OOBE (Shift+F10) | `reg query HKLM\SYSTEM\CurrentControlSet\Control\BitLocker /v PreventDeviceEncryption` shows 1; FVE and tzautoupdate values present; `C:\Windows\Logs\CloudImaging\SetupRunner.log` lists the Windows setup script as Succeeded before OOBE; `C:\Windows\Setup\CloudImaging` no longer exists. |
| 6.6 | Inspect `C:\Windows\Panther\unattend.xml` | Profile settings present; exactly one `Invoke-CloudImagingSetup.ps1` command, Order 1, in the `amd64` (or `arm64`) Deployment component (SC-007). |
| 6.7 | Add a Default user value (`Control Panel\Desktop` `MenuShowDelay` String 200); image each OS version | Value present in a new user's profile after first sign-in; on failure the item reason names the hive. |
| 6.8 | WinPE script that runs `Restart-Computer -Force; exit 0`, and a Windows setup script that runs `shutdown /r /t 0` | Device does not restart during the step or during specialize; both items logged (WinPE item on the timeline). |
| 6.9 | WinPE script `Start-Sleep -Seconds 600` with a 1-minute timeout | Item failed "Stopped after 1 minute."; next script runs; with Complete with warnings the step shows the amber marker and the session completes. |
| 6.10 | Same with Fail the deployment | Step Failed before Configure boot; remaining items "Not run"; session Failed; Retry starts a new session. |
| 6.11 | Windows setup script whose staged file is changed in WinPE before the step ends (Command Prompt support tool) | Runner log: item skipped, "File does not match the manifest"; other items still run. |
| 6.12 | A profile with a Windows setup script and an image captured without Sysprep generalize | Answer file item failed with the generalization reason. |
| 6.13 | Network needed in specialize: Windows setup script that resolves an internal name | Logged result shows whether the NIC driver was available (spec risk 2); record per model. |
| 6.14 | Delete the profile while a device is imaging | Device completes with the pinned version; afterwards (sweep, within 5 minutes of the session ending) its version blobs are gone; history kept. |
| 6.15 | Image a device from a boot image built before this release, with a profile pinned | Timeline shows Prepare setup "Not run" with the update tooltip. |

## 7. Performance

| # | Measure | Budget |
|---|---|---|
| 7.1 | Prepare setup duration minus the sum of script durations (from the step's start and end times and item durations), profile with 200 registry values, 20 scripts and a 256 KB answer file | At most 30 seconds (SC-005). |
| 7.2 | Portal `GET /api/setup-profiles` and `GET /api/setup-profiles/:id` p95 at 50 concurrent users (existing load harness in `tests/performance`) | At most 300 ms (constitution V). |
| 7.3 | Save of a maximum-size profile (about 2 MB) through Static Web Apps | Succeeds (no platform body limit hit); duration recorded. See plan Complexity Tracking. |
| 7.4 | End-to-end deployment of the reference workload with a three-value, one-script profile | Within 15 minutes (constitution V), no regression above 10% against the recorded baseline. |

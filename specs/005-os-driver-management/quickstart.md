# Quickstart: Validating OS Driver Management

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Contracts**: [contracts/](contracts/)

## 1. Automated gates (every change)

| Command | Proves |
|---|---|
| `dotnet build CloudImaging.sln -c Release` | Zero warnings and errors. |
| `dotnet test CloudImaging.sln -c Release --collect:"XPlat Code Coverage"` | Core: cabinet reader (MSZIP, LZX) over the real fixtures, HP and Dell parsers, exclusion, classification, lineage, policy resolver, fingerprint and roll back, schedule slots across DST, shared Sync now and all-models check limit, clean-up references, assignment pin and skip reasons. Gateway and Operator API: contract tests for every new function. Client: descriptor handling, PCI match, download verification, extractor switches and success rules, DISM invocation, outcomes, critical failure, hand-off to Prepare setup. Coverage at least 80% on new code. |
| `dotnet format CloudImaging.sln --verify-no-changes --include <changed files>` | Style. |
| `npm run test:run` (portal client and server) | Every `/api/drivers` route (guards, actor from token, 409/429 relay), role matrix, Drivers page tabs, model page, onboarding dialog, timeline card, OS image release field. |
| `npm run lint`, `npx tsc --noEmit -p tsconfig.json` (client and server) | Lint and types. |
| Text scan of changed files for the em dash character and `\b(FR|SC)-\d{3}\b|\bT\d{3}\b|\bUS\d\b` outside comments | No banned punctuation or identifiers. |

## 2. Fixtures (once)

Capture into `tests/fixtures/drivers/`: `platformList.cab`, `8c26_64_11.0.24h2.cab`,
`8cbe_arm64_11.0.24h2.cab`, `CatalogIndexPC.cab`,
`Latitude_0CB2.cab`, `Latitude_0CB4.cab`, `Latitude_0C85.cab` (Dell ARM64). Tests read only these files.

## 3. Cloud prerequisites (staging resource group)

1. Deploy with the new `drivers` container. Confirm the Core identity can read and write it.
2. Downloads from vendor hosts: from the staging Core, download one HP SoftPaq and one Dell
   package from the catalogs into `drivers/files/...` and compare the streamed SHA-256 with the
   catalog. Expected: both commit with matching hashes. If Core cannot reach the hosts, record it:
   the tenant needs egress (NAT gateway or firewall rule) for the vendor hosts.
3. Read all seven fixtures' live counterparts with `CabinetReader` in a staging run. Expected:
   parse without error, Function RSS under 256 MB.
4. Run `tests/validation/staging-smoke-test.ps1 -IncludeDrivers` against the staging resource group.
   Expected: one HP model onboarded and completed, driver set 1 built with every file Verified, a
   simulated session assigned with set 1 pinned, every Gateway link downloads with its SHA-256,
   and after Remove model the next clean-up leaves no file of that model. The script exits non-zero
   on any failure.

## 4. Portal flow (Operator)

1. Sync tab: turn HP on and press Sync in the Sync now row. Expected: run Running then Completed;
   HP's model list is refreshed (the Onboard model dialog lists its models); History shows one row;
   a second press within 60 minutes is refused and the button's tooltip shows "Available again at
   <time>". Models tab: Check for updates is disabled until the same time (shared limit). Call
   `POST /api/drivers/sync/runs` and `POST /api/drivers/models/check` directly within the window:
   both 429 with `availableAt`. After the hour, Check for updates on the Models tab runs without
   reading HP's model list (only component catalogs are requested).
2. Onboard EliteBook 840 G11 (`8C26`) for Windows 11, version 24H2. Expected: step 2 table shows
   counts per action and size; Onboard opens the model page with the Draft banner, the match key
   badge next to the name, and Check for updates and Roll back driver set disabled; the Models tab
   shows Draft; nothing in `drivers/files/` yet and no `driver-work` messages; a device of the
   model gets no drivers. Change one action to Skip, then Complete onboarding. Expected: the
   Downloads tab shows the set within seconds, files appear, each verified, and the model gets
   driver set 1 without the skipped component.
3. Rules: add the suggested rule "HP Services Scan for Commercial". Expected: component Source shows
   the rule, Hits counts it, a new set is built without downloads.
4. Override one component, then press its undo icon. Expected: Source Override, then back to the rule.
5. Locked component: dropdown disabled with the lock tooltip.
6. Turn on "Wait for approval", simulate a new component (fixture with one extra lineage).
   Expected: amber New badge, banner, excluded from the set; Approve includes it with its action.
7. Roll back driver set (its tooltip names both sets), then Sync now with an unchanged catalog.
   Expected: set stays rolled back.
8. Remove model. Expected: impact overlay with freed space; after confirm, new assignments get no
   set; files go after the next run's clean-up unless shared.
9. Technician: all tabs visible without edit controls; write calls return 403.
10. Downloads and Storage: right after completing onboarding, the Downloads tab shows the set in progress with
    "n of m components" and the tab count; when ready it moves to Finished in the last 7 days.
    Storage: expand the model; each release block names its current and roll-back set, and each
    component shows its sets, Shared with and Status.
11. Change three overrides within a minute. Expected: the banner says "3 changes are waiting" with
    a build time 2 minutes after the last change; one set is built; Build now builds at once.
    A new version of a skipped component (fixture) builds no set.
12. Roll back component (the version history row 2 entries back, no longer stored). Expected: the
    file is downloaded again and verified, the next set has the older version of that component and
    the newest of the others; Source shows Rolled back. A fixture with a newer vendor version ends
    the roll back at the next sync. Point the recorded URL at a missing file: 409 with the reason,
    nothing changes. With the default of one previous version, the row 1 entry back shows Stored and
    rolls back with no download; set Storage > Previous versions to Keep none and after the next
    clean-up it shows Downloads on roll back. Open Roll back component with a recorded URL pointing
    at a missing file: "Checking HP", then that version shows "No longer available from HP",
    cannot be chosen, and the version history shows the same status.
13. Add custom component: a signed driver ZIP replacing Intel Wireless LAN Driver on two models.
    Expected: SHA-256 and INF count shown, Source Custom, the vendor component Replaced by custom and
    locked at Skip; a ZIP without INF is refused. A fixture with a new vendor version shows the
    banner naming the vendor; Dismiss is recorded; Remove custom component restores the vendor
    component in the next set.
14. Check for updates on the model page (the button row under the name) with a fixture that has a
    newer audio driver and a newer version of a skipped component. Expected: only that model's
    vendor catalog is read, the audio driver downloads at once and a new set is built, the skipped
    component downloads nothing, the History card shows "<model>: 1 updated", a second press is
    refused for 15 minutes, and the 60-minute limit of the Models tab button does not move. Switch
    the release in the component toolbar: tiles and table change to that release.
15. Schedule: set Every day at a time 3 minutes ahead. Expected: exactly one `Scheduled` run starts
    at that time with no polling timer in Core (only `driver-work` messages), and restarting Core
    before the slot still starts one run, not two. Then set Selected days, today's weekday + 1, every
    2 weeks. Expected: the line reads "Next sync: <tomorrow> <time>, then every 2 weeks on <day>.",
    and after that run `nextRunAt` is two weeks later, not one.
16. Upload a 6 GB custom package. Expected: refused before upload with the 5 GB limit.
17. Fresh install, Dell sync off: open Onboard model and pick Dell. Expected: "Getting Dell's model
    list" for a few seconds, then the list with "list from <time>" in the footer and the "Sync is
    off for Dell" line; no History row. Reopen: no fetch. Block Dell's catalog host and set the stored
    list's `FetchedAt` 8 days back: the list shows with "Can't reach catalog at
    https://downloads.dell.com" and onboarding works. Delete the stored list too: the error with Try
    again, Next disabled. Unblock, then Sync now with a fixture that adds models: the History row
    says "Dell: N new models available".

## 5. Device flow (HP reference device, x64 WinPE and WinRE boot images)

1. Set the OS image release to Windows 11, version 24H2. Assign a matching device.
   Expected: session pins the model and set; later syncs do not change it.
2. Image the device. Expected: Install drivers step after Apply Windows image; status lines
   "Downloading drivers (n of m)" and "Adding drivers (n of m)"; extraction under
   `W:\CloudImaging\Drivers`, nothing on X:; the timeline card lists outcomes.
3. First boot: every Offline component is in the driver store (`pnputil /enum-drivers`); companion
   apps of Offline + Specialize components are installed for all users;
   `C:\Windows\Logs\CloudImaging\SetupRunner.log` lists each installer's exit code, shows it ran as
   SYSTEM before any profile script, and nothing about specialize reaches the portal;
   `C:\CloudImaging\Drivers` and `C:\Windows\Setup\CloudImaging` are gone, also on a device where
   one installer failed (the next installer still ran and the device did not restart during
   specialize).
4. A device of an unonboarded model: step Completed with warning "No onboarded model matches this
   device.", deployment succeeds.
5. Mark "Driver - Network" critical and corrupt one network component's blob. Expected: step
   Failed naming it, session fails before Configure boot, Retry works after fixing.
6. As a Technician, couple an onboarded HP device and one whose system ID is not onboarded, and
   select the OS image. Expected: the Driver set column preselects the matched model; the other
   row shows "No match, choose a set" with a tooltip naming its system ID. Pick the G11 model for
   it and No drivers for the matched device; the undo button returns a row to its match. The
   Start imaging overlay lists both choices as "(chosen manually)". After start, each session
   pins what was chosen; the timeline shows "Chosen manually by <upn>" and, for No drivers, the
   step note without a warning.
7. Remove the chosen model in another tab before pressing Start imaging. Expected: 409 naming the
   device, nothing assigned, the column reloads.
8. Image a device of a model with the custom component and a rolled-back component. Expected: the
   custom driver is in the driver store (`pnputil /enum-drivers`) and the replaced vendor driver is
   not; the timeline lists each driver with its version, marking Custom and Rolled back.
9. Image a second device of the same model with the same USB drive. Expected: no driver file is
   downloaded (Core storage logs show no reads for those blobs), the status line says "Copying
   drivers from the USB drive", `CACHE:\CloudImagingCache\drivers\` holds one folder per file with
   `metadata.json`, and the timeline says "31 from the USB drive". Corrupt one cached file: that
   component is downloaded again and its cache entry replaced.
10. Prepare a new USB drive in Media Builder with one model's driver set selected. Expected: the
    size and the space left for OS images are shown; preparation downloads the files once into the
    local Media Builder cache and copies them to the CACHE partition; the manifest lists the set;
    the first deployment of that model downloads no driver file. Select more sets than fit: Prepare
    is disabled with the reason. Image from an ISO (no CACHE partition): drivers download as before.

## 6. Dell (Latitude 5450, SKU 0CB4, without WWAN)

Onboard, sync, image. Expected: WWAN and NVIDIA components reported "Skipped: no matching
hardware" and never downloaded; 32-bit packages only Specialize; Dell installers during
specialize logged.

## 6a. ARM64 (Dell Latitude 7455 and HP EliteBook Ultra G1q, ARM64 boot image and OS image)

1. Onboard both. Expected: model rows show ARM64; on the HP model no component offers Offline or
   Offline + Specialize, and components default to Specialize.
2. Assign an x64 OS image to an ARM64 device. Expected: step skipped with "The OS image's
   architecture does not match this model."
3. Image the Dell with an ARM64 image. Expected: native ARM64 packages extract and inject in ARM64
   WinPE like x64.
4. Image the HP. Expected: components are staged unextracted and run as `<sp>.exe /s` during
   specialize; drivers present and Wi-Fi available at OOBE; SetupRunner.log lists exit codes.

## 7. Performance and budgets

The baseline for the constitution's 10% regression gate is this table measured on `main` before the
merge (Main baseline column), next to the feature branch's results (Feature branch column).

| Measure | Target | Main baseline | Feature branch |
|---|---|---|---|
| Onboard a model and review its per-action preview (stopwatch, from Onboard model to Onboard) | Under 3 minutes |
| Correct an unmatched device in the Coupled Devices table | Under 30 seconds |
| Driver set column preview for 20 coupled devices, p95 | Under 300 ms |
| Install drivers on the reference HP and Dell models, 500 Mbit/s | Under 10 minutes |
| Install drivers with every file on the USB drive (USB 3) | Under 5 minutes |
| Client working set during the step | Under 512 MB |
| Media Builder working set while preloading driver sets | Under 512 MB |
| Client and Media Builder startup to main window | Under 5 seconds |
| Imaging Core first HTTP response after a cold start (with the new queue trigger) | Under 3 seconds |
| Function RSS during a first sync of 3 models | Under 256 MB |
| Every `GET /api/drivers/*` route, p95 at 50 users (preview and model list from cache) | Under 300 ms |
| Onboarding preview and model list from cache, p95 | Under 300 ms (a cache miss shows the loading line) |
| End-to-end deployment without drivers (constitution reference workload) | Under 15 minutes |
| End-to-end deployment with drivers | Recorded |

## 8. Items to validate on hardware (research R23 and R24)

HP extractor in our WinPE and WinRE images; HP installers during specialize as SYSTEM; no second
install after Offline + Specialize; INFs installing companion software themselves; Dell
extraction and installers (exit code 2); docks only matched when connected; `dism.exe` present in
WinRE-based boot images. Record each result in this file's table when run.

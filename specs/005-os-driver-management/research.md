# Research: OS Driver Management

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-24

Source material: `todo/os-image-driver-injection.md` (HP spike 2026-10-21, Dell and Lenovo spikes
2026-10-22), the spec clarifications of 2026-10-22 to 2026-10-24, and the code as of branch
`feat/005-os-driver-mgmt` (spec 003 setup profiles and spec 004 operator role in place).

Every Technical Context unknown is resolved below. Each entry: Decision, Rationale, Alternatives.

---

## R1. Where the sync runs and how it survives long downloads

**Decision**: The sync runs in Imaging Core, driven by one Storage queue, `driver-work`, on Core's
own storage account, and one queue-triggered function, `DriverWorkProcessor`. There is no polling
timer. Every unit of work is a message: `ScheduledSync`, `FetchVendorCatalog`, `ResolveModel`,
`DownloadFile`, `BuildSet` and `FinalizeRun`. The event that needs the work enqueues it: completing
onboarding, Sync now, Check for updates on the Models tab or on one model, a policy change, a component roll back, a custom
component and Build now. The schedule is a delayed message:

- `ScheduledSync` carries the slot it is for and is enqueued with a visibility delay until that
  slot (re-enqueued with the remaining delay when the slot is more than 7 days away, the queue's
  limit). The message that claims the slot with the `LastScheduledSlotUtc` ETag starts the run and
  enqueues the message for the next slot; any other copy is dropped. Saving the schedule and every
  Core start enqueue one message for the next slot, so a lost message never stops the schedule and
  copies never multiply.
- `BuildSet` for a policy change is enqueued with a 2-minute delay. When it arrives and
  `BuildAfter` has moved later (more edits), it re-enqueues itself for the remainder; Build now
  enqueues it without delay.
- A run counts its outstanding messages on the run row (ETag); the message that brings the count
  to zero enqueues `FinalizeRun`. A `FinalizeRun` delayed 2 hours is enqueued at run start and
  ends a run still `Running` then as `Failed`.
- The host retries a failing message up to 3 times, then moves it to `driver-work-poison`, whose
  trigger records the item as failed with the reason so the run still finishes.

Concurrency comes from the queue: `host.json` sets `batchSize` 4 and `newBatchThreshold` 2, and
`maxPollingInterval` 10 seconds so new work starts within seconds. The host renews a message's
visibility while the function runs, so an 800 MB download is one message.

**Rationale**: Only the daily (customizable) catalog check recurs (spec clarification of
2026-10-25). Core is a Linux Elastic Premium (EP1) Function App with `functionTimeout` 01:00:00 and an
identity-based `AzureWebJobsStorage` connection to its storage account, where its identity
already holds Storage Queue Data Contributor, so a queue trigger needs no infrastructure change and
existing environments get it with the code upgrade. Core creates both queues at start
(`CreateIfNotExists`). Queue triggers also drive Elastic Premium scale-out for large first
downloads, which a singleton timer never does. Core owns state transitions (constitution, Project
Architecture), and the schedule changes without a restart. The only new dependency is the
first-party `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` package.

**Alternatives considered**: A table-backed work list advanced by a timer every minute with an
8-minute claim budget (the previous decision; polls all day for work that happens a few times a
week, and adds up to a minute before downloads start); Durable Functions (new extension and
storage provider for one feature); a Container Apps Job (new compute, identity and network
surface); one long timer run (fails at the 1-hour limit on a first sync and restarts from
nothing).

## R2. How Core reaches vendor hosts

**Decision**: Imaging Core downloads every vendor file itself, catalogs included, with
`HttpClient` (`ResponseHeadersRead`), and streams the body into a block blob in the `drivers`
container (4 MB blocks) while computing the SHA-256 in the same pass (R5). The URL must pass the
vendor host allowlist first: HP `hpia.hpcloud.hp.com`, `ftp.hp.com`, `ftp.ext.hp.com`; Dell
`downloads.dell.com`, `dl.dell.com`. Any other host, an `http` URL, a URL with credentials, or a
redirect to a host outside the allowlist is refused and recorded as a failed item. Core creates the
`drivers` container at startup (`CreateIfNotExistsAsync`; its identity already has Storage Blob
Data Contributor), so existing environments need no infrastructure deployment.

**Rationale**: Core's inbound access is private, but its outbound calls already reach the
internet: it calls `graph.microsoft.com` for Autopilot import, corporate identifiers and pre-flight
authorization. With VNet integration and route-all, App Service sends that traffic into the VNet
and it leaves from the app's own outbound addresses unless a NAT gateway or firewall is added; the
retirement of default outbound access does not apply to delegated App Service subnets (checked
against Microsoft Learn, 2026-10-25). One streaming pass downloads, stores and verifies, with
constant memory.

**Alternatives considered**: Blob server-side copy from the vendor URL (no Function egress, but a
second full read to verify and untested redirect behaviour; kept as the fallback for tenants that
block Core's egress); the portal backend (presentation tier, duplicate work on scale-out, would
need write access to Core storage); a dedicated Function App (new deployment). A NAT gateway for a
fixed outbound IP is optional infrastructure outside this feature; with one, App Service requires
storage traffic to use private or service endpoints. Tenants that restrict egress must allow the
vendor hosts above (documented in `docs/drivers.md`).

## R3. Reading vendor catalogs (.cab) on Linux

**Decision**: A managed `CabinetReader` in Imaging Core reads single-folder cabinets with MSZIP
and LZX compression, implemented from Microsoft's published specifications ([MS-MCI] for MSZIP,
[MS-PATCH] LZX section for LZX). Unit tests use the real catalogs captured as fixtures under
`tests/fixtures/drivers/`.

**Rationale**: Measured on 2026-10-24 by reading the cabinet headers: HP's model catalog
(`HPClientDriverPackCatalog.cab`) and Dell's index (`CatalogIndexPC.cab`) and per-SKU catalogs
(`Latitude_0CB4.cab`) use MSZIP; HP's per-platform reference file (`8c26_64_11.0.24h2.cab`) uses
LZX. All are one folder with one file and a 20-byte signature reserve. Plain XML variants do not
exist (404). .NET has no cabinet support, and MSZIP blocks share the 32 KB history window across
blocks, which `DeflateStream` cannot preset, so an inflater with a carried window is needed
anyway. A clean-room implementation keeps the MIT license free of copyleft code.

**Alternatives considered**: `libmspack` (native LGPL library to ship and load on Linux);
WixToolset.Dtf (wraps Windows `cabinet.dll`, does not run on Linux); a Windows Function App only
for catalogs (second Core host); parsing catalogs on the device (the portal needs them first).

## R4. Parsing catalog XML within the memory budget

**Decision**: `XmlReader` streaming over the decompressed stream, projecting only the fields the
data model needs. Dell catalogs are UTF-16 with namespaces (`openmanage/cm/dm`); the reader
detects the encoding from the BOM. Decompressed catalogs are processed one at a time.

**Rationale**: Function instances must stay below 256 MB RSS (constitution V). A DOM for Dell's
index and a full-history per-SKU catalog would hold tens of MB per document.

**Alternatives considered**: `XDocument` (simpler, but memory grows with catalog size);
`XmlSerializer` over full schemas (couples us to vendor schema changes we do not use).

## R5. Verifying large files in the cloud

**Decision**: The download stream feeds an `IncrementalHash` (SHA-256) while blocks are staged;
the block list is committed only when the length and hash match the vendor's published values over
the full content. A mismatch discards the uncommitted blocks and records a failed item, so an
unverified file is never visible. One file per `DownloadFile` message; the host keeps the message
invisible while the download runs, well within the 1-hour function timeout for an 800 MB file, and
a message that fails or times out is retried from the start (at most 3 times, R1).

**Rationale**: Answers the spec's risk 8. Streaming keeps memory constant. Blob content MD5 cannot
replace the vendor's SHA-256.

**Alternatives considered**: trusting the copy (no integrity guarantee); verifying on the device
only (devices would still verify, but the spec requires a verified store).

## R6. Admin-defined schedule with IANA time zones

**Decision**: Stored settings: mode (`Daily`, `SelectedDays`, `Manual`), weekdays, repeat (1 to 4
weeks, `SelectedDays` only) with the week it counts from, local time
`HH:mm`, IANA zone. The schedule is one delayed `ScheduledSync` message (R1) carrying `slotUtc`,
the next slot computed in the zone with `TimeZoneInfo` (IANA IDs work on Linux through tzdata and
on Windows through ICU in .NET 10). When it arrives, the handler starts a run only if it can write
`slotUtc` to `LastScheduledSlotUtc` with the ETag, so duplicate copies of the message cannot both
start it, and a message whose slot no longer matches the stored schedule is dropped. A message
that arrives more than 6 hours after its slot (for example after an outage) starts no run and only
enqueues the next slot. A local time skipped by a DST change runs at the next valid
minute; a repeated hour runs once. Every save sets the anchor to the Monday of the week holding
the next selected day, so the first run after a change is always the next selected day and
every N weeks follows from it. The same calculation returns `nextRunAt`, which the portal shows
with the repeat so the on weeks are never guesswork.

**Rationale**: Settings apply on change with no restart (FR-032). The ETag on the settings row
gives "at most once per slot" (FR-033) without a lock service.

**Alternatives considered**: CRON per tenant in app settings (needs restart); storing UTC only
(a DST change would move the local run time).

## R7. One run at a time and the manual run rate limit

**Decision**: A run is created by inserting `DriverSyncRuns` row `run/active` (insert fails if it
exists), then the run's own row. Every trigger (`Scheduled`, `Manual`, `Check`, `ModelCheck`)
needs that lock, so only one run is ever active: Sync now, Check for updates on the Models tab and
Check for updates on a model page are refused with
409 while `run/active` exists and with
429 within 60 minutes of the last `Scheduled`, `Manual` or `Check` run start (one limit shared by
the first two; a model page check has its own 15-minute limit per model); all responses carry
`availableAt` and a `Retry-After` header. The portal disables the button until `availableAt`. Only
`Scheduled` and `Manual` runs (syncs) refresh the vendors' model lists; `Check` and `ModelCheck`
runs read component catalogs only.

**Rationale**: The API enforces both rules (FR-034, SC-005). Insert-if-absent on a fixed row key
is atomic in Table Storage.

**Alternatives considered**: blob lease (another primitive for the same job); a client-side
cooldown only (spec forbids).

## R8. The Windows release of an OS image

**Decision**: `OsImage` gains `WindowsRelease`, a code from a fixed list shown as "Windows 11,
version 24H2" (codes `Windows11_23H2`, `Windows11_24H2`, `Windows11_25H2`, `Windows10_22H2`).
Operators set it when uploading or editing an image; nothing is read from the WIM. HP maps it to
the reference file name (`11.0.24h2`), Dell to the operating system its catalogs list ("Windows
11"). The list is extended in code when Microsoft ships a release.

**Rationale**: "22H2" exists for both Windows 10 and Windows 11, so a release alone is ambiguous.
The clarification requires an administrator-entered value with no fallback.

**Alternatives considered**: free text (typos silently match nothing); reading the WIM's build
(rejected in the clarification).

The image's existing `Architecture` (x64 or ARM64) must equal the matched model's architecture
(R24); otherwise the step is skipped with a warning.

## R9. Matching a device and pinning its set

**Decision**: Imaging Core matches and pins at assignment, in `AssignSessionFunction` and
`BulkAssignmentService`, next to the setup profile pin. HP matches `Hardware.MotherboardModel`
(`Win32_BaseBoard.Product`, already collected) against the model's system ID list; Dell matches
the new `Hardware.SystemSku` (`Win32_ComputerSystem.SystemSKUNumber`). The session records the
model, release, set version and, when nothing matches, a skip reason. A model chosen when imaging
starts replaces the match (R25). The Device Gateway relays
the pinned set (R10). The Client adds `SystemSku` to `DeviceHardwareMetadata` and to the copy in
`DeviceGatewayApiClient.SignPayload`.

**Rationale**: Core owns state transitions and SAS issuance; the setup profile pin already lives
here, so both pins happen in one place. The device's signature binds serial number, timestamp and
nonce, not the hardware fields, which is the trust level manufacturer and model already have; a
device that lies about its model only receives another model's drivers.

**Alternatives considered**: matching in the Device Gateway (the spec's wording, but the Gateway
holds no catalog state and must not write sessions); matching when the Client asks (a sync between
assignment and the step could change the set).

## R10. Device access to component files

**Decision**: The descriptor (`GET /api/v1/sessions/{id}/driver-set`) returns one user-delegation
read SAS per component, valid for 60 minutes. A component whose URL expires before it finishes is
refreshed alone (`POST .../driver-set/components/{index}/link`). The descriptor is fetched once;
refreshes are rare, so the session stays far inside 10 calls per 30 seconds.

**Rationale**: Matches the clarification (one link per component) and the existing setup profile
descriptor pattern (15-minute SAS, Core issues, Gateway relays). 60 minutes covers an 800 MB file
on a slow link.

**Alternatives considered**: one container-scoped SAS (exposes every customer's driver file);
a session-scoped proxy download through the Gateway (bandwidth through Functions).

## R11. Extracting and injecting on the device

**Decision**: Files go to `<W>:\CloudImaging\Drivers\<componentKey>\` on the applied volume
(the path spec 003's `SpecializeInstaller` already expects), never to X:. Each file is downloaded
with the existing retry pattern and verified (streamed SHA-256) before anything runs from it.
Extraction runs through `RestrictedProcessRunner` (restart guard, process-tree kill, 10-minute
timeout): HP `sp<id>.exe /s /e /f "<folder>"`, retried with the legacy `-pdf -e -s -f"<folder>"`;
Dell 64-bit `<package>.exe /s /drivers="<folder>"`. Success is judged by the files produced (at
least one `.inf` for Offline actions; the installer's executable for Specialize actions), not by
exit code. Injection runs `dism.exe /Image:<W>:\ /Add-Driver /Driver:"<folder>" /Recurse` once per
component, without `/ForceUnsigned`, capturing output like `ImageApplyService`.

**Rationale**: Confines extraction to the component's folder (zip-slip is the vendor tool's job,
but the folder is fresh and deleted afterwards), keeps WinPE's RAM disk free, and isolates a bad
INF to its own component.

**Alternatives considered**: one DISM call for all folders (one bad INF fails all); 7-Zip in
WinPE (not every package is an archive, and HP's tool is authoritative); cloud-side extraction
(rejected in the todo).

## R12. Dell hardware match on the device

**Decision**: Before downloading, the Client enumerates `Win32_PnPEntity.HardwareID` values once
(WinPE-WMI is in every Media Builder boot image) and parses `PCI\VEN_xxxx&DEV_xxxx&SUBSYS_ssssvvvv`.
A Dell component with published PCI IDs is processed only if some device matches vendor and
device ID and, when the catalog lists them, subsystem vendor and device. Components without
hardware IDs are always processed; HP components are always processed.

**Rationale**: Dell publishes `PCIInfo` per component; skipping absent mobile broadband (642 MB)
and NVIDIA (970 MB) is the main size saving.

**Alternatives considered**: sending the PCI inventory to the cloud for matching (larger payload,
no benefit, and the set must stay pinned).

## R13. Handing Specialize work to Prepare setup

**Decision**: Install drivers returns one `SpecializeInstaller` per component with action
`OfflineAndSpecialize` or `Specialize`: files are every file of the staged folder with the
SHA-256 the Client computed after extraction (Dell 32-bit: the original package, already
verified); the command comes from the vendor data (HP `SilentInstall` split into executable and
arguments, working directory the staged folder; Dell `<package>.exe /s`); timeout 20 minutes;
success exit codes 0 and 3010; cleanup path the component folder. Prepare setup and the setup
runner already order driver installers before profile scripts, verify files, apply timeouts and
continue after failures.

**Rationale**: Reuses spec 003's contract unchanged; the runner re-verifies every staged file
(FR-056).

**Alternatives considered**: staging only the original package and re-extracting during
specialize (runs the vendor extractor twice; HP's `SilentInstall` expects the extracted folder).

## R14. Critical categories and the step result

**Decision**: Critical categories are stored per vendor and category name, none by default. The
descriptor marks each component `critical`. The Client finishes every component, then reports the
step `Failed` (error detail listing the failed critical components) if any critical component
failed, otherwise `Completed` with a warning count. A `Failed` step stops the pipeline before
Prepare setup and Configure boot through the existing failure and retry path.

**Rationale**: FR-053: "fail after the step finishes", so all outcomes are still reported.

**Alternatives considered**: stopping at the first critical failure (loses the other outcomes).

## R15. Step and item reporting

**Decision**: `ImagingStepName` gains `InstallDrivers` (appended; optional, progress weight 0
like `PrepareSetup`). Items: new `StepItemKind.DriverComponent`, new `StepItemOutcome.Skipped`
(hardware not present), and an optional `DriverAction` on `ImagingStepItem`. Outcomes: Offline
injected = `Succeeded`; Offline + Specialize injected and staged = `Succeeded` with action shown;
Specialize staged = `Queued`; no matching hardware = `Skipped`; failure = `Failed` with reason.
A step skipped for the whole device (no model, release not onboarded, no release on the image,
arm64 image) is reported `Completed` with warning count 1 and the new optional `StatusNote` on the
step ("No onboarded model matches this device."). Items fit the existing 1500-item cap.

**Rationale**: Reuses the item table, final-report call and portal card pattern from spec 003;
additive enum values keep stored data valid.

**Alternatives considered**: a `Skipped` step status (every consumer of `ImagingStepStatus`
would need a new branch); free-text reasons only (the portal cannot show counts).

## R16. Storage layout and clean-up

**Decision**: New private container `drivers` on the Core storage account, created by Core at
startup (no Bicep change):
`catalogs/<vendor>/<sha-of-url>.cab` (fetched catalogs, overwritten), `files/<vendor>/<componentId>/<version>/<original file name>`
(immutable, one per vendor component version), `sets/<modelId>/<release>/<version:000000>.json`
(resolved set documents). Clean-up runs in `FinalizeRun` of every `Scheduled`, `Manual` and
`Check` run (not `ModelCheck`, which reads one model): list `files/`, keep every file
referenced by a current, roll-back or pending set, a component roll back, a custom component or
the set pinned on any session in the `active`
partition, and the files of the previous versions each component keeps (`PreviousVersionsToKeep`,
default 1, from the version history), delete the rest. No lifecycle rule; no manual action.

**Rationale**: Content-addressing by vendor version gives sharing by reference (FR-041) and
exact "Freed if removed" numbers (files referenced by only one model).

**Alternatives considered**: per-model copies (duplicates shared components); lifecycle rules
(cannot see references).

## R17. Resolved sets, fingerprints and roll back

**Decision**: A set document lists items (lineage key, component ID, version, action, file blob,
SHA-256, size, critical flag, vendor commands). Fingerprint: SHA-256 over sorted
`lineage|version|action` lines. A `DriverSetPointer` row per model and release holds
`CurrentVersion`, `PreviousVersion` and `RolledBackFromFingerprint`, updated with an ETag. A new
build whose fingerprint equals the current one or `RolledBackFromFingerprint` creates no set.
Roll back swaps current and previous and records the fingerprint rolled back from. Only current and
previous set documents are kept, plus any pinned by a running session until it ends.

**Rationale**: FR-028 to FR-031 and the edge case "the next sync produces the same fingerprint as
the set rolled back from".

**Alternatives considered**: per-component roll back with automatic move forward (proposed in the
todo; adopted alongside the set roll back in R27).

## R18. Policy resolution

**Decision**: A pure function `DriverPolicyResolver.Resolve(component, rules, override)` returns
the action and its source: Locked if allowed actions are only Skip; else model override; else
vendor-scoped rules; else all-vendor rules; else the vendor default. Within a layer, ID beats name
beats category; same layer and kind with different actions: the most recently changed rule wins.
A rule or override whose action is not in the component's allowed actions is ignored for that
component. Name patterns are case-insensitive with `*` wildcards only (no regex). Rule hit counts
are computed over the current sets.

**Rationale**: Deterministic, unit-testable, and identical for the portal preview, sync and
policy edits.

**Alternatives considered**: regex patterns (ReDoS risk and harder for admins).

## R19. Policy edits, approvals and new versions between syncs

**Decision**: A rule, override, approval, release or removal change marks the affected
(model, release) pairs dirty and enqueues a delayed `BuildSet` (R1). It re-resolves dirty pairs
from the stored component data (no catalog read), enqueues downloads for files not yet stored, and
switches the set when every file is verified (FR-024). These jobs are not sync runs and do not
count for the all-models check limit; they are recorded in the change log. Check for updates on a model
is a run scoped to that model: it reads that model's vendor catalog only, records a run in the
history, and has its own 15-minute limit per model instead of the 60-minute all-models limit.

**Rationale**: Edits take effect without waiting for the next sync and without touching vendor
catalogs.

**Alternatives considered**: applying edits at the next sync only (stale for up to a week).

## R20. Portal and access control

**Decision**: New page `pages/DriversPage.tsx` with tab markup like `DeploymentConfigPage`, the
`Switch`, `Select`, `SearchInput`, `Badge`, `Table`, `Tooltip`, `ConfirmImpactDialog` and
`SectionCard`/`SettingRow` components, and a model page `pages/DriverModelPage.tsx`. Capabilities
`canViewDrivers` (Technician) and `canManageDrivers` (Operator). The portal server router
`/api/drivers` guards reads with `requireTechnician` and writes with `requireOperator`, forwards
the acting user from the token, and the Operator API proxies to Core (PortalAccess). Changes are
recorded in a `DriverChangeLog` table (who, when, what).

**Rationale**: Same layering and role model as setup profiles (spec 003) and the operator role
(spec 004).

**Alternatives considered**: a Tabs primitive in `components/ui` (only worth it when a third page
needs it); enforcing roles in the Operator API (it only sees the portal's service principal).

## R21. Onboarding preview latency

**Decision**: The preview downloads each selected model's component catalog directly (R2), cached
in `catalogs/` for 24 hours, then resolves it in memory. Expected 1 to 3 seconds for an uncached
model; the dialog shows progress per model. Constitution V (1.5.0) exempts a call that waits on a
third-party service on a cache miss when the UI shows progress; a preview served from the cache
must still meet 300 ms p95, and so must the model list fetch (R33) when its list is current.

**Rationale**: The spec requires the preview before anything downloads, and per-model component
catalogs for every vendor model cannot be kept current.

**Alternatives considered**: syncing every vendor model's component catalog daily (hundreds of
requests for models nobody uses).

## R22. Performance budgets

**Decision**: Measured in quickstart section 7: Install drivers duration on reference HP and Dell
models over 500 Mbit/s (SC-011, under 10 minutes), Client working set during extraction (under
512 MB; downloads stream to disk), Function RSS during a sync (under 256 MB), Drivers API p95 for
list and model page reads and for cached previews (under 300 ms; R21). The constitution's
15-minute end-to-end budget covers the base OS image (1.5.0); the driver payload's own budget is
Install drivers under 10 minutes, and the full deployment with drivers is still recorded.

## R23. Items still to validate on hardware

Kept from the spec's risk list, each with a quickstart check: HP extractor in our WinPE and WinRE
boot images; HP installers during specialize as SYSTEM (companion apps for all users); Offline then
installer not causing a second install or restart request; INFs that install companion software
themselves; Dell 64-bit extraction in WinPE and Dell installers during specialize (including whether
Dell returns exit code 2 for "restart required", which would need adding to its success codes);
dock drivers only matching when docked; `dism.exe` present in WinRE-based boot images; ARM64:
x64 SoftPaqs not running in ARM64 WinPE and running under emulation during specialize (R24), Dell
ARM64 extraction in ARM64 WinPE.

## R24. ARM64 devices

**Decision**: ARM64 is supported for both vendors. Each onboarded model has an architecture (x64
or ARM64) taken from the catalog, and a device gets a set only when the OS image's architecture
matches. The Client runs the same step in ARM64 WinPE.

- **Dell**: Snapdragon models (Latitude 7455 `0C85`, Latitude 5455 `0C83`, XPS 13 9345 `0C69`)
  have their own per-SKU catalogs whose components list `osArch="ARM64"`; their packages are native
  ARM64 executables (PE machine `AA64`, measured 2026-10-24) with `packageType` `LW64`. Same
  classification and extraction as x64.
- **HP**: the driver pack catalog lists x64 platforms only (4133 entries), so HP's model list comes
  from HP's platform list (`https://hpia.hpcloud.hp.com/ref/platformList.cab`), which names each
  platform's system ID, products and per-release `OSArchitecture` (5 ARM64 platforms on
  2026-10-24, for example `8cbe` EliteBook Ultra G1q). ARM64 reference files are
  `<id>_arm64_11.0.<release>.cab`. Their SoftPaq executables, including the 443 MB Qualcomm Driver
  Pack, are x64 (PE machine `8664`), and ARM64 WinPE has no x64 emulation, so HP's extractor cannot
  run before Windows starts. On HP ARM64 models Offline and Offline + Specialize are therefore not
  allowed: components whose x64 default would be Offline or Offline + Specialize default to
  Specialize, and Specialize runs the SoftPaq itself (`"<sp>.exe" /s`, which extracts and runs
  `SilentInstall`) during the specialize pass, where Windows 11 on ARM emulates x64. Drivers are
  then installed before OOBE, so Wi-Fi is available for OOBE.

**Rationale**: ARM64 is increasingly common; Dell needs no special handling, and HP's constraint is
an executable format limit, not a catalog one.

**Alternatives considered**: leaving HP ARM64 out (loses half the ARM64 fleet); unpacking HP
SoftPaqs in the cloud (the format is the vendor's, and the todo rejected cloud-side extraction).

## R25. Choosing the driver set when imaging starts

**Decision**: The Coupled Devices table on the Devices page gets a Driver set column (design
`docs/design/driver-set-selection-options.html`, option A). When the OS image changes, the page
calls a new preview endpoint, `POST /api/drivers/assignment-preview { osImageId, sessionIds }`,
which runs the same `DriverSetResolver` match Core uses at assignment without writing anything
and returns, per session, the matched model or the skip reason and the device's match key, plus
one option list (models with a current set for the image's release and architecture, with
`maxDownloadBytes`). Start imaging sends `driverChoices: [{ sessionId, kind: "Model" | "None",
modelId? }]` only for rows that differ from the match. Core validates every choice before
assigning any session (same all-or-nothing rule as the architecture check in
`BulkAssignmentService`) and pins the chosen model's current set at that moment, with
`DriverChosenManually` and `DriverChosenBy`. The portal server takes the chooser from the token
(`decidingUser`) and discards any browser-sent actor. The preview and the choice use the Start
imaging guard (Technician and above), not the Drivers write guard.

**Rationale**: Matching is right for onboarded models, but a wrong or missing match is otherwise
only visible after imaging. A per-deployment choice is not a policy change, so Technicians may
make it. Choosing a model rather than a set version keeps the list short and always gives the
newest verified drivers; roll back stays an Operator action on the model page. Re-resolving at
assignment means a sync between preview and start cannot pin a stale set.

**Alternatives considered**: a model-name fallback in matching (names differ between catalogs
and devices, and a wrong guess installs another model's drivers silently); a read-only column
with a dialog per change (two clicks per device); per-model choices only in the Start imaging
overlay (cannot give one of two identical devices no drivers, and the problem shows only after
pressing Start).

## R26. When a new driver set is built

**Decision**: The set and its fingerprint hold only included components (no Skip items), with
custom components and rolled-back versions already applied, and the fingerprint covers
`lineage|version|action|origin`. Critical categories are not in the set; Core copies the model
vendor's critical categories to the session at assignment and marks components critical when it
serves the descriptor. Policy changes set `BuildAfter` to 2 minutes after the change and enqueue a
`BuildSet` message delayed until then, which re-enqueues itself while later edits move
`BuildAfter` (R1), so a burst of edits produces one set. Build now
clears the wait. Syncs build at once.

**Rationale**: A new version of a skipped component would otherwise build a set with nothing to
download and push the useful roll-back set out. Critical categories in the set would only reach
devices with the next unrelated change. Only one previous set is kept, so five quick overrides must
not create five sets.

**Alternatives considered**: critical flag in the fingerprint (every category change rebuilds every
set of the vendor); a Save button for policy changes (the Drivers page applies changes on the spot
everywhere else).

## R27. Component version history and roll back

**Decision**: `DriverComponentVersions` keeps one row per version a component has had on a model
and release, with the sets that used it, dates, what changed it and the vendor URL and SHA-256,
for as long as the model is onboarded (rows are small; files are not kept). Roll back component
stores a `DriverRollBacks` row (version, until a newer vendor version or undone); policy resolution
applies it before the fingerprint. A version whose file was cleaned up is downloaded again from the
recorded URL through the same allowlist and verification; the roll back is refused if that fails.
Dell's per-SKU catalogs keep every release, and HP normally keeps old SoftPaqs on its FTP hosts,
but neither is guaranteed. So clean-up keeps the previous version of every component by default
(0 to 3 on the Storage tab): the usual roll back, "the last update broke it", is instant and needs
no vendor. Opening Roll back component checks every version that is not stored with a HEAD request
to its recorded URL (allowlisted hosts, 10 seconds each, in parallel) and records the result, so a
version the vendor removed is shown as no longer available instead of failing after confirm.

**Rationale**: A model onboarded for two years has dozens of sets, while each component changes at
its own pace. Set roll back only goes back one set and undoes every other update; a bad component
found late needs to be undone on its own. Defaulting to "until a newer version" matches how vendor
fixes arrive.

**Alternatives considered**: keeping more previous sets (storage grows with every update, and still
undoes unrelated components); a set history view (a rarely changed component appears once among 50
entries); keeping every version (storage grows without bound for versions nobody returns to);
checking vendor availability on every sync (hundreds of requests for versions nobody rolls back to).

## R28. Custom components

**Decision**: Operators upload a package with the same staged upload as OS images (the browser
writes blocks straight to a staging blob through a SAS URL; only JSON passes through the portal
server), and Core commits it into `custom/<customId>/<version>/` in the `drivers` container,
computing the SHA-256 in one streaming pass and, for Offline actions, checking the ZIP central
directory for at least one `.inf` entry (no unpacking in the cloud). Packages are limited to 5 GB,
which covers a full chipset or graphics package with room to spare. A custom component targets one or more models (all releases) and may
replace one vendor lineage per model, which then resolves to Skip with source `ReplacedByCustom`.
It stays until removed. The Client unpacks custom ZIPs itself (`System.IO.Compression`, every
entry's full path checked to stay inside the component folder) and injects with DISM without
`/ForceUnsigned`, so unsigned drivers fail on the device like any other. Sync only reports that the
replaced lineage has a new vendor version; it never compares that version with the custom one.

**Rationale**: Intel and others ship fixes before HP and Dell repackage them. Comparing versions is
not reliable: the custom package carries the chip vendor's driver version, HP's catalog the SoftPaq
version and Dell its own `A16`-style version, and reading the INF `DriverVer` would need unpacking
in the cloud.

**Alternatives considered**: automatic retirement when the vendor's version is equal or newer
(fires too early or never); a separate Function for uploads (the OS image upload path already
handles multi-gigabyte chunked uploads with SHA-256).

## R29. Download progress and stored content in the portal

**Decision**: `DriverSetBuilds` records each set build with its trigger, component counts and
bytes; the Downloads tab reads builds with `ReadyAt` null (in progress, counts from `DriverFiles`
status) and those ready in the last 7 days. The Storage tab's model rows expand into per-release
blocks built from the current and roll-back set documents plus `DriverFiles` (size, status,
downloaded time) and the set documents of other models (shared with). Progress counts components,
not bytes, because a file's status is only known per file.

**Rationale**: Downloads after onboarding and policy changes are not sync runs, so Sync History
cannot show them; without a progress view a new model looks finished before it has a set.

**Alternatives considered**: a byte-level progress bar (needs Core to record bytes per block); a
Downloads card on the Storage tab (mixes what is kept with what is happening now).

## R30. Driver files on the USB drive

**Decision**: Driver and custom component files use the USB drive's existing NTFS CACHE partition
(spec 001), under `CloudImagingCache\drivers\<sha256>\` next to `images\`, keyed by SHA-256 like OS
images. A new `DriverFileCache` in the Client (beside `ImageCacheService`) looks a file up before
download, copies a hit into the component folder on the Windows volume, re-hashes the copy there
before anything runs, deletes a cache entry whose copy fails the check, and writes an entry only
after a verified download (metadata last; incomplete entries deleted at startup of the step).
The cache is always read: a SHA-256 match is used, anything else is downloaded, and when the cache
is full the download is not cached. The CACHE volume is found the same way as for images (`Win32_Volume` label `CACHE`).

Media Builder preloads selected driver sets: the non-elevated process lists sets and fetches one
read SAS per file through two new Operator API functions on the Media Builder allowlist (Core
returns links for files of current sets only), downloads and verifies them into its local cache
folder (`%LOCALAPPDATA%\Cloud Imaging Media Builder\Cache\drivers\<sha256>\`, reused across drives),
and the elevated worker copies them into the CACHE partition after formatting, using
`PreparationResult.CacheDriveLetter`. The manifest gains `DriverSets` for information only.

**Rationale**: Driver payloads of 2 to 3 GB per model are downloaded on every deployment otherwise,
which hurts branch offices most; the OS image already avoids this. Keying by SHA-256 makes preloaded
and Client-cached files interchangeable and survives new sets: unchanged components keep their hash.
Copying to the Windows volume keeps extraction off the USB drive and lets the SHA-256 check run on
the exact bytes that are executed.

**Alternatives considered**: extracting from the USB drive in place (writes and runs vendor tools
on removable media, and the check would not cover the copied bytes); preloading by set (a newer set
would make the whole preload useless); a separate driver partition (changes the USB layout every
existing drive has).

## R31. Onboarding as a draft

**Decision**: Onboard creates each model with `State = Draft` and resolves its components from
the catalog data the preview already fetched, without building a set or enqueuing downloads. The
portal opens the first model's page, where the Operator changes actions with the usual override
controls. Overrides, approvals and release changes on a draft are stored without enqueuing a
`BuildSet`. Complete onboarding (`POST models/{id}/complete`) sets `State = Active`, turns the
vendor's sync on, and enqueues `ResolveModel` for each release, which downloads the included files
and builds the first set. Match keys are reserved at Onboard so two drafts cannot claim the same
system ID; assignment and the assignment preview ignore drafts. A scheduled run refreshes a
draft's components like any model but enqueues no download for it.

**Rationale**: The onboarding dialog's per-action counts cannot show which components the
Operator wants; deciding that per component belongs on the model page, and downloading gigabytes
for components that are about to be skipped wastes time and storage.

**Alternatives considered**: a third dialog step with the full component table (duplicates the
model page inside a dialog); downloading at once and letting skips clean up later (downloads and
then deletes files the Operator never wanted).

## R32. Time zone list for the sync schedule

**Decision**: The product stores the list in [contracts/time-zones.json](contracts/time-zones.json):
one entry per Windows time zone (142), in the Windows order (standard UTC offset, then name), each
with the Windows ID, the Windows display name as Windows shows it in en-US ("(UTC+01:00) Amsterdam,
Berlin, Bern, Rome, Stockholm, Vienna", "(UTC) Coordinated Universal Time"), its standard offset,
the official IANA name the schedule uses (`ianaId`), and `covers`: every IANA name that maps to it,
current names and their older aliases, so the browser's zone can be matched to an entry. The IANA
names come from the Unicode CLDR `windowsZones.xml` mapping (territory `001` is the primary name),
with aliases resolved to current names through the tzdb `backward` file (tzdb 2026e, retrieved
2026-10-25). Imaging Core embeds the file, offers only entries whose `ianaId` its host's
`TimeZoneInfo` resolves, returns them with the sync settings, and accepts only an `ianaId` from the
list. The portal shows the display names and preselects the entry whose `covers` includes the
browser's zone (`Europe/Stockholm` selects the Amsterdam to Vienna entry, stored as
`Europe/Berlin`, which has the same rules). The file is regenerated when Windows or tzdb change a
zone.

**Rationale**: Administrators know the Windows picker, and its labels name the cities people look
for, while the scheduler on Linux needs IANA names. One stored list keeps the display and the
validation the same. All 142 `ianaId` values are current tzdb names (none deprecated) and every one
resolves in .NET on Windows and Linux; two Windows entries without a CLDR mapping
("Mid-Atlantic Standard Time", "Kamchatka Standard Time", both obsolete) are left out. 461 IANA
names map to an entry, including aliases browsers still report such as `Asia/Calcutta` and
`Europe/Stockholm`.

**Alternatives considered**: IANA names as labels from tzdb `zone.tab` (419 entries such as
"Europe/Stockholm (UTC+02:00)"; precise, but unfamiliar next to the Windows picker and five names
do not resolve in .NET on Windows); Windows time zone IDs as the stored value (Linux schedules on
IANA names); the browser's `Intl.supportedValuesOf("timeZone")` (differs per browser and cannot be
validated server-side); display names read from the server at run time (on Linux .NET returns
generic names such as "Central European Time", not the Windows city lists).

## R33. Vendor model list for onboarding

**Decision**: The vendor model list (HP platform list, Dell catalog index) is stored per vendor with
`FetchedAt`. Syncs refresh it for vendors that are on. `GET catalog/models` fetches it on the call
when it is missing or more than 7 days old, for any supported vendor whatever its Sync switch; a
failed fetch falls back to the stored list with a warning, or returns 503 naming the catalog URL
when none is stored. Fetching is not a run: no History row, no limits. A sync records how many
models it added per vendor, and the History card shows "<vendor>: N new models available".

**Rationale**: A fresh install, or a vendor turned off, otherwise cannot onboard anything until
someone runs a sync. The list is one small file per vendor and lists no drivers, so reading it from
the dialog costs nothing and does not depend on the Sync switch. Vendors add models a few times a
year, so a week-old list is good enough; Sync now refreshes it at once when needed.

**Alternatives considered**: requiring a sync with the vendor on before onboarding (the current
dead end); a 24-hour freshness window (more vendor calls for no practical gain); a refresh button in
the dialog (Sync now already does it); blocking onboarding when the catalog is unreachable (an older
list is still correct for nearly every model).

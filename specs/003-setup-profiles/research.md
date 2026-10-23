# Research: Setup Profiles

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-23

All decisions below are grounded in the code as of this date. The spec has no open clarifications;
this document resolves the design questions the spec leaves to implementation. Spec documents may
reference requirement identifiers; nothing proposed here as user-facing text does.

---

## Current state (what the code does today)

| Area | Where | What it does today | Consequence for this feature |
|---|---|---|---|
| Imaging steps | `CloudImaging.Contracts/Enums/ImagingEnums.cs` | `ImagingStepName` has five values (FormatDisk, DownloadImage, ApplyImage, ConfigureBoot, ApplyRecoveryImage), serialized as strings. `ImagingStepStatus` is Pending, InProgress, Completed, Failed. | A new value `PrepareSetup` is additive on the wire. There is no warning status. |
| Step reducer | `ImagingCoreApi/Functions/ReportProgressFunction.cs` | A session completes when `allSteps.Count == OverallProgressCalculator.PipelineStepCount` (5) and all are Completed. | An optional step breaks this rule both ways (6 reported steps never equal 5; a skipped step is never reported). The rule must change. |
| Progress weights | `ImagingCoreApi/Services/OverallProgressCalculator.cs` | Fixed shares 8/47/25/5/15 mirroring the Client's bands (0-8, 8-55, 55-80, 80-85, 85-100). | A new step either takes a share (shifts every band) or has weight 0. |
| Step storage | `ImagingCoreApi/Repositories/ImagingStepRepository.cs` | Table `ImagingSteps`, PK sessionId, RK step name; Status, timestamps, ErrorDetail, StepProgress. | No per-item outcomes exist. |
| Client pipeline | `Client/ViewModels/ImagingWorkflowViewModel.cs` | Linear Format, Download, Apply, Configure boot, Recovery. Each step is reported InProgress then Completed or Failed via `ImagingProgressReporter`. Failure navigates to Results; Retry starts a new session. | Prepare setup slots between Apply image and Configure boot (after Install drivers once spec 005 ships). |
| Progress budget | `DeviceGatewayApi/Middleware/RateLimitingMiddleware.cs`, `Client/Services/ImagingProgressReporter.cs` | 10 calls per 30 s per session token, shared by every authenticated endpoint; percent updates throttled to one per 10 s. | Per-item outcomes must ride on one final report, not one call per item. |
| Offline registry | `Client/Services/OfflineDriveLetterService.cs` | Loads the applied image's SYSTEM hive with `RegLoadKey` (SeBackup/SeRestore privileges enabled), edits `MountedDevices` through `Microsoft.Win32.Registry`, unloads with a GC retry. | Proven pattern in this product's WinPE. Reuse it for SOFTWARE, SYSTEM and the default user hive. |
| Device location | `Contracts/Models/DeviceSession.cs` | `LocationId` and denormalized `LocationName` come from the USB preparation manifest through registration. | Profile resolution keys on `LocationId`; no new device identity. |
| Locations | `Contracts/Models/Location.cs`, `ImagingCoreApi/Functions/LocationFunctions.cs` | Name, Region (legacy), CountryCode. Delete removes the row, then removes the id from every Wi-Fi network (`WifiNetworkRepository.RemoveLocationAsync`). No actor is passed. | Delete gets one more clean-up and one refusal rule, and needs the deleting user for history. |
| Assignment | `ImagingCoreApi/Functions/AssignSessionFunction.cs`, `Services/BulkAssignmentService.cs` | Both move SessionAssigned to SessionStarted and set the OS image SAS URL. The portal's Start imaging uses bulk assign. | Both paths must pin the profile the same way. |
| Device status | `DeviceGatewayApi/Functions/GetSessionStatusFunction.cs` | Polled status relays state, step, SAS URL, hash, architecture, partitioning scheme, pre-flight checks. | Polled every few seconds; not a place for a 2 MB payload. |
| Boot image | `MediaBuilder/Services/BootImageGenerationService.cs` | Injects WinPE-WMI then WinPE-NetFx (with en-us language cabs) into the mounted WIM for both the ADK WinPE and the Windows RE base; fails with a clear message if a cab is missing. | WinPE-Scripting and WinPE-PowerShell slot in right after NetFx. |
| Portal roles | `server/src/middleware/roleGuard.ts`, `client/src/lib/portalRoles.ts` | Spec 004 is implemented in the working tree of `feat/004-operator-role` (not yet merged): Operator exists, `requireOperator` names Operator and Administrator, the client derives capability flags from expanded roles, and `tests/cloud-imaging-portal/server/role-route-matrix.test.ts` asserts every route against every role. | New routes reuse the guards and get matrix rows. This feature must be built on top of the merged spec 004 work. |
| Portal patterns | `client/src/pages/WifiNetworksPage.tsx`, `client/src/components/WifiNetworkDialog.tsx` | `LocationsCell` (outline badges, "+N more" with tooltip) and a file-private `LocationPicker` (search, Select all, Clear, "N of M selected"). Ordering uses move up/down buttons and a `PUT .../order` call. | Extract both into shared components; do not fork them. |
| Shared rule fixtures | `tests/fixtures/autopilot-group-tag-cases.json` | One JSON case file consumed by the portal client (Vitest) and Imaging Core (xUnit) so two implementations of one rule set cannot drift. | The answer file rules use the same mechanism. |
| Body size | `server/src/app.ts` | `express.json({ limit: '1mb' })` globally; error handler forwards only `type`, `title`, `status`, truncated `detail`. | A full profile version can reach about 2 MB; validation problems need a structured list. |
| Blob containers | `src/deploy/bicep/modules/storage.bicep` | Containers are declared in Bicep (os-images, boot-images, recovery-images, branding, app-packages, session-logs). Tables are created at Core startup (`Program.cs`, `EnsureTableExistsAsync`). | A new container goes in Bicep; new tables go in `Program.cs`. |

---

## R1. Storage model for profiles, versions, targeting, retention and audit

**Decision**: Imaging Core API owns all state, split into four tables and one blob container in the
existing Core storage account.

| Store | Keys | Holds |
|---|---|---|
| Table `SetupProfiles` | PK `profile`, RK profileId | Name, current version number, counts of the current version (registry values, scripts, answer file present), failure handling of the current version, created and last modified time and author, tombstone (`DeletedAt`, `DeletedBy`). Optimistic concurrency through the entity ETag. |
| Table `SetupProfileTargets` | PK `target`, RK locationId or the literal `all-devices` | ProfileId. One row per targeted location, one row for the All devices profile. **This table is the single source of truth for targeting.** |
| Table `SetupProfileVersions` | PK profileId, RK version as `D6` (`000008`) | Author, time, content SHA-256, content size, blob name, generated change summary (JSON), `RestoredFrom` version when applicable. |
| Blob container `setup-profiles` | `{profileId}/{version:D6}.json` | The immutable version document (registry values, scripts, answer file, failure handling). Written once with `If-None-Match: *`. |
| Table `SetupProfileHistory` | PK profileId, RK `{inverted ticks:D19}-{guid:N}` | One row per profile-level event: created, renamed, targeting changed (locations added or removed, All devices on or off), location removed because it was deleted, version saved, version restored, duplicated, deleted. Who (UPN and object id) and when. |

- **Uniqueness**: a location can belong to one profile and only one profile can target All
  devices because the RK of `SetupProfileTargets` is the location id (or `all-devices`). Adding a
  target is an insert; a conflict (409 from Table storage) is the "already targeted" signal and
  names the owning profile. A targeting change is one entity group transaction in partition
  `target` (adds and deletes together, atomic). Above 100 operations (the transaction limit) the
  change is applied in chunks with compensation on failure.
- **Retention setting**: new field `SetupProfileVersionRetention` on `PortalConfiguration` (default
  25, valid 5 to 100), stored by the existing `PortalConfigurationRepository`, edited on the
  Configuration page Miscellaneous tab through the existing Administrator-only `PUT /api/portal-config`.
- **Pruning**: at each save (new version, restore), Core lists the profile's versions, keeps the
  newest N (N = retention) and any older version pinned by a session in the `active` partition of
  `DeviceSessions`, and deletes the rest (blob first, then row). Lowering the setting therefore
  never removes anything until that profile's next save (spec edge case).
- **Deletion**: Delete tombstones the profile row, deletes its target rows (one transaction) and
  writes a history row. Version blobs stay while any active session pins one; the existing
  five-minute `SessionLifecycleTimerFunction` gets a sweep that deletes unpinned versions of
  tombstoned profiles. Tombstone and history rows are kept so the audit trail survives deletion.
- **Audit (FR-043)**: the history table is the durable audit record for every profile change; each
  write also emits a structured `ILogger` event (UPN, object id, profile id, action) that lands in
  Application Insights. Retention changes are recorded on the configuration row
  (`SetupProfileVersionRetentionChangedAt`, `...ChangedBy`) and as the same structured event,
  because the configuration row has no history table.

**Rationale**: A version document can reach about 2 MB (20 scripts of 64 KB, a 256 KB answer
file, 200 registry values, JSON escaping), above the 1 MB Table entity limit and far above the
64 KB property limit, so content goes to blob storage. Version documents are immutable, which makes
pinning a pointer (profile id plus version) instead of a copy. Keeping targeting out of the profile
row and in an index keyed by location gives the uniqueness rule an atomic, race-free enforcement
point that Table storage supports natively. All tables follow the existing repository pattern
(`WifiNetworkRepository`, `DeviceSessionRepository`).

**Alternatives considered**:
- Content in Table storage split across properties: 1 MB entity limit is too low and splitting is fragile.
- Copying the version content onto the session at assignment: duplicates up to 2 MB per device and complicates session purge.
- Targeting stored as a JSON list on the profile row (as Wi-Fi networks do): two operators saving overlapping targeting at the same time can both succeed; the index makes the conflict impossible.
- A generic audit table for the whole product: no such table exists today and the spec only needs profile changes; a per-profile partition is enough and is what the History dialog reads.

---

## R2. Pinning at assignment

**Decision**: A new Core service `SetupProfileResolver` resolves the profile for a session at
assignment: point read `target/{session.LocationId}`, else point read `target/all-devices`, else
none. It reads the profile row and pins `SetupProfileId`, `SetupProfileVersion` and the
denormalized `SetupProfileName` on the `DeviceSession` in the same `with { ... }` transition that
sets `SessionStarted`. Both `AssignSessionFunction` and `BulkAssignmentService` call it (bulk
assign resolves once per distinct location in the batch).

**Rationale**: Imaging Core owns state transitions and Start imaging is the bulk assign call, so
pinning there satisfies "Start imaging MUST pin the current version". The Device Gateway never
writes session state; spec 005's "Device Gateway pins" language maps to the same Core assignment
transition. A deleted location has no target row, so a session from such media falls through to
All devices, which is the spec's edge case. Pinning reads two rows and one profile row, adding a
few milliseconds to assignment.

**Alternatives considered**: Resolving when the Client asks for the profile. Rejected: edits made
between Start imaging and the Prepare setup step would change what the device gets (FR-031).

---

## R3. How the pinned version reaches the Client

**Decision**: A new device endpoint, called once per session:

- Device Gateway `GET /api/v1/sessions/{sessionId}/setup-profile` (device session token, rate
  limited like every session call) relays to Core `GET /api/internal/sessions/{sessionId}/setup-profile`.
- Core returns 204 when nothing is pinned. Otherwise it returns a descriptor: profile id, name,
  version, failure handling, content size, content SHA-256 (recorded at save time) and a read-only
  SAS URL for exactly that version blob, valid for 15 minutes and issued at request time with the
  existing `BlobSasUrlGenerator`.
- The Client downloads the blob with the same HTTP path it uses for images, verifies size and
  SHA-256 before parsing, and keeps the parsed document in memory (at most about 2 MB).
- The Client fetches it **right after the assignment wait and before Format disk**. A device whose
  pinned profile cannot be fetched or verified after three attempts fails before its disk is wiped.

The polled status response does not change. Size limits are enforced at save time by Core
(scripts 64 KB of UTF-8 each, answer file 256 KB of UTF-8, 200 registry values, 20 scripts), so the
Client never has to reject an oversized document it was given.

**Rationale**: The status call is polled every few seconds and shares the 10-calls-per-30-seconds
budget, so a large payload there would cost bandwidth on every poll. A one-off descriptor plus a
SAS download mirrors the existing OS image pattern (SAS URL plus `Sha256Hash`), keeps Function
responses small, and makes Core the SAS issuer as the constitution requires. Issuing the SAS on
request avoids the expiry problem a SAS minted at assignment would have on slow downloads.
Fetching before Format disk turns a missing or tampered profile into a failure with the disk
untouched, and lets the on-device step list show Prepare setup from the start. Integrity is
end-to-end: the hash is computed by Core when the version is written and re-checked by the Client.

**Compatibility**: A new Client talking to an older backend gets 404 from the Gateway route and
treats it as "no profile" (an older backend has no profiles). An older Client on a newer backend
never calls the endpoint, so a pinned profile is not applied; the portal timeline shows the step
as not run (R15) and the release notes tell customers to update boot images.

**Alternatives considered**:
- Inline content in the Gateway response: works, but needs a canonical hashing scheme for JSON inside JSON and pushes up to 2 MB through three Function hops on every fetch.
- SAS URL issued at assignment and carried in the status poll (like the OS image): needs SAS refresh logic for a second blob and grows every status response.
- Encrypted envelope like Wi-Fi: protects secrets, but profiles must not contain secrets (FR-016, FR-020) and TLS plus mTLS already protects transport.

---

## R4. Step placement, step name, progress, completion and per-item outcomes

**Decision**:

1. `ImagingStepName.PrepareSetup` is added. Order in the Client: Format disk, Download image,
   Apply image, (Install drivers, spec 005), **Prepare setup**, Configure boot, Apply recovery image.
2. **Weight 0** in `OverallProgressCalculator`. The overall percentage holds at 80 during the step;
   the step's own bar shows items processed out of total. Shares of existing steps do not change.
3. **Completion rule** in `ReportProgressFunction` becomes `OverallProgressCalculator.IsPipelineComplete(steps)`:
   every required step (the existing five) is Completed and no reported step is InProgress or
   Failed. `PipelineStepCount` is removed. Optional steps (PrepareSetup now, InstallDrivers with
   spec 005) count only when reported. `CurrentOrLastStepName` walks the full ordered list,
   including optional steps.
4. **Skipped means not reported.** A device with no profile and no driver Specialize work never
   reports PrepareSetup, so the timeline shows five steps exactly as today.
5. **Warnings without a new status.** `ImagingStep` gains `WarningCount` (null for steps that do
   not report items) and `ItemCounts` (counts by item kind and outcome). A step with status
   Completed and `WarningCount > 0` is "Completed with warnings". All existing `Status == Completed`
   checks keep working.
6. **Per-item outcomes** travel on the step's final report only (Completed or Failed) as an
   `items` array (at most 500 entries; text fields capped and truncated by Core, not rejected).
   Core writes them to a new table `ImagingStepItems` (PK sessionId, RK `{StepName}-{index:D4}`,
   batch transactions of 100) and computes `ItemCounts`. The portal reads items on demand through
   a new session route. Items are deleted with the session when it is purged.

Budget for the step: 1 descriptor call (before Format disk), 1 InProgress report, percent updates
under the existing 10-second throttle, 1 final report. That is within the shared 10-per-30-seconds
budget with the heartbeat.

**Rationale**: Weight 0 keeps the portal and the device in agreement without re-banding every
step, and the step's budget is 30 seconds plus the profile's own scripts. A warning count instead
of a `CompletedWithWarnings` status leaves every existing reducer, calculator, history record and
report untouched, and an older Core simply ignores the unknown fields. Spec 005's Install drivers
step reuses the same fields and table (FR-053, FR-054, FR-059 there).

**Alternatives considered**:
- A `CompletedWithWarnings` status value: touches every `Completed` comparison in Core, the Client and the portal, and an older Core rejects the unknown value with 400.
- Re-banding the progress weights: changes the percentages of every in-flight deployment during an upgrade and of every historical session.
- One progress report per item: up to 220 calls; would exhaust the rate limit and starve the heartbeat.
- Storing items as JSON on the step row: 220 items exceed the 32 K character property limit.

---

## R5. Offline registry writes from WinPE

**Decision**: Extract the hive load/unload interop from `OfflineDriveLetterService` into an
`OfflineRegistryHive` helper (IDisposable: enables SeBackup and SeRestore, `RegLoadKey` under
`HKLM\CI_OFFLINE_<NAME>`, `RegUnLoadKey` with the existing GC retry) and use it from both
`OfflineDriveLetterService` and a new `OfflineRegistryWriter`:

| Hive in the profile | File on the applied volume | Mounted as |
|---|---|---|
| HKLM SOFTWARE | `Windows\System32\config\SOFTWARE` | `HKLM\CI_OFFLINE_SOFTWARE` |
| HKLM SYSTEM | `Windows\System32\config\SYSTEM` | `HKLM\CI_OFFLINE_SYSTEM` |
| Default user | `Users\Default\NTUSER.DAT` | `HKLM\CI_OFFLINE_DEFAULTUSER` |

- Each hive is loaded only when the profile has values for it, all values for it are written,
  every handle is closed, and it is unloaded before In WinPE scripts run (scripts may load hives
  themselves).
- A key under HKLM SYSTEM that starts with `CurrentControlSet\` is rewritten to
  `ControlSet00N\` where N is `Select\Current` of the offline hive. An offline SYSTEM hive has no
  `CurrentControlSet` link; writing to a literal `CurrentControlSet` key would create a dead key.
- Value types map to `RegistryValueKind` String, ExpandString, MultiString, DWord, QWord. DWORD
  and QWORD data are validated at save (decimal or `0x` hexadecimal, unsigned range).
- A hive that cannot be loaded fails every value for it with the reason, for example "The default
  user profile was not found in the applied image." Other hives continue.
- No `WOW6432Node` redirection happens offline; a value meant for 32-bit software must name that
  key explicitly (help text in the registry dialog).
- The same SYSTEM hive load also reads `Setup\Status\SysprepStatus\GeneralizationState` (R7) and
  clears `Setup\UnattendFile` when Prepare setup will write an answer file (R7).

**Rationale**: `RegLoadKey` from the SYSTEM-context WinPE process is already proven in this product,
needs no extra binaries, and is unit-testable behind a small interface. offreg.dll is not part of
WinPE or the ADK runtime and would have to be redistributed. `reg.exe load` works but returns
strings to parse and gives poorer error reporting.

**Alternatives considered**: offreg.dll (redistribution, extra native dependency per architecture);
`reg.exe` (string parsing, one process per value, slower against the 30-second budget).

**To validate** (spec risk 1): loading `Users\Default\NTUSER.DAT` from Windows 11 23H2, 24H2 and
25H2 images in this product's WinPE and Windows RE boot images ([quickstart.md](quickstart.md) section 6).

---

## R6. In WinPE scripts

**Decision**:

- **Runtime**: Windows PowerShell 5.1 from the boot image
  (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script>`).
  `BootImageCapabilities.SupportsPowerShell()` checks that file, like `SupportsWifi()` checks the
  WLAN files. Without it every In WinPE script fails with "This boot image has no PowerShell, so
  the script cannot run. Generate a new boot image with Media Builder and update the USB media."
- **Identity**: the Client already runs as SYSTEM in WinPE; children inherit it.
- **Files**: each script is written to `X:\Windows\Temp\CloudImaging\Scripts\NN.ps1` as UTF-8
  **with BOM** (Windows PowerShell 5.1 reads BOM-less files as ANSI) and deleted afterwards.
- **Environment**: `CI_WINDOWS_DRIVE` is the applied Windows volume as `W:` (no trailing slash).
  The working directory is the script folder.
- **Timeout and tree kill**: each script runs in a Windows job object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; at the timeout the job is terminated, killing the whole
  process tree, and the item is recorded as failed with "Stopped after N minutes."
- **No restarts**: the process is created with `CreateRestrictedToken` (deleting
  `SeShutdownPrivilege` and `SeRemoteShutdownPrivilege`) and `CreateProcessAsUser` suspended,
  assigned to the job, then resumed. The job also sets `JOB_OBJECT_UILIMIT_EXITWINDOWS`. Every
  standard restart path (`Restart-Computer`, `shutdown.exe`, `wpeutil reboot`, `ExitWindowsEx`,
  `InitiateSystemShutdownEx`, `NtShutdownSystem`, WMI `Win32Shutdown`) needs that privilege, so the
  call fails inside the script and the imaging step continues. A restricted copy of the caller's
  own token does not need SeAssignPrimaryTokenPrivilege. If the restricted token or the job cannot
  be created, the script is not started and fails with "Not run because restarts could not be
  blocked on this device." (FR-015 is a MUST; running unguarded is not an option.)
- **Outcome**: exit code 0 is success. Reported per script: exit code, duration, last non-empty
  output line (stdout and stderr merged, truncated to 200 characters). Full output goes to the
  Client's rolling log (last 200 lines per script).
- **Off the UI thread**: all of this runs inside the pipeline's background task; the UI only
  receives `StatusMessage` and step percent updates (constitution IV).

**Rationale**: PowerShell is what operators already write for these tasks (spec assumption). A
restricted token is the only mechanism that blocks every restart path rather than one API, and it
leaves every other SYSTEM privilege (firmware variables, raw disk access) that vendor tools such as
HP BCU need.

**Limits stated honestly**: the guard is a safety net against scripts and tools that restart by
habit (`-Restart` switches, vendor tools that reboot after a firmware change). It is not a
security boundary: scripts run as SYSTEM by design and a deliberately hostile script could still
reach another SYSTEM process. Who may write scripts is controlled by roles (R12).

**Alternatives considered**:
- `JOB_OBJECT_UILIMIT_EXITWINDOWS` alone: blocks only `ExitWindowsEx`.
- Watching for and aborting shutdowns (`AbortSystemShutdown`): cannot stop immediate shutdowns.
- Running scripts through the setup runner only: WinPE results would not reach the timeline (FR-013).

**To validate**: `CreateRestrictedToken` and `CreateProcessAsUser` from the Client in WinPE and
Windows RE boot images on x64 and ARM64, and that `Restart-Computer` inside a script fails without
affecting the Client ([quickstart.md](quickstart.md) section 6).

---

## R7. The device's answer file

**Decision**: A pure `UnattendComposer` in the Client builds the file; `PrepareSetupService`
writes it.

1. Start from the profile's answer file, or `<unattend xmlns="urn:schemas-microsoft-com:unattend"/>`.
   Parse with `XmlReader` (`DtdProcessing.Prohibit`, `XmlResolver = null`).
2. When the device has Windows setup work (driver installers from spec 005 or During Windows setup
   scripts): find or create `settings[@pass='specialize']`, then the
   `Microsoft-Windows-Deployment` component whose `processorArchitecture` is the device's
   (`MachineArchitecturePlatform.AdkArchitectureName`: `amd64` or `arm64`), creating it with
   `publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"` and the `wcm` and
   `xsi` namespace declarations, then `RunSynchronous`. Existing `RunSynchronousCommand` elements
   are sorted by `Order` (stable, document order on ties) and renumbered from 2. The setup runner
   command is inserted with `Order` 1, `wcm:action="add"`, a `Description` and the `Path`:
   `%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%WINDIR%\Setup\CloudImaging\Invoke-CloudImagingSetup.ps1"`
   (about 150 characters, under the 259-character limit). Components for other architectures are
   left alone; Windows ignores them on this device.
3. Serialize as UTF-8 with an XML declaration to `<W>:\Windows\Panther\unattend.xml`, creating the
   folder. Read it back and assert exactly one runner command (SC-007).
4. Make sure Windows setup reads this file: delete `Windows\Panther\Unattend\unattend.xml` and
   `Windows\Panther\Unattend\autounattend.xml` if the image has them, and clear
   `HKLM\SYSTEM\Setup\UnattendFile` in the offline SYSTEM hive. Both take precedence over
   `Windows\Panther\unattend.xml` in Windows setup's implicit search order. The `System32\Sysprep`
   and `%SYSTEMDRIVE%` locations come later and are left alone.
5. When Prepare setup runs, it always writes the file (an empty `unattend` element when there is
   neither a profile answer file nor Windows setup work), so the device gets exactly one answer
   file and any file the image contained is overwritten without a warning (FR-021).
6. **Generalization check**: Windows setup only runs the specialize pass on a generalized image.
   When there is Windows setup work and the offline `GeneralizationState` is not 7, the answer file
   item fails with "The OS image is not generalized, so Windows setup will not run the setup runner.
   Capture the image after Sysprep /generalize." Microsoft's install.wim is always generalized.

**Rationale**: FR-022 fixes the merge rules; the decisions above are the details Windows setup
needs: architecture-matched component, unique `Order`, and a file Windows actually reads. Without
step 4 an image carrying its own cached answer file or registry pointer would silently win.

**Alternatives considered**: Appending without renumbering (duplicate `Order` values make setup
fail); a separate `SetupComplete.cmd` (runs after OOBE starts, too late for Autopilot prerequisites
and not the specialize pass the spec requires).

---

## R8. The setup runner

**Decision**: `Invoke-CloudImagingSetup.ps1`, a Windows PowerShell 5.1 script in
`src/CloudImaging.Client/SetupRunner/`, copied to the Client publish output and therefore into
every boot image Media Builder generates (it copies the whole Client folder into `X:\CloudImaging`).
It reaches devices through the existing boot image self-update.

- **Staging** (by the Client in WinPE): runner and `manifest.json` go to
  `<W>:\Windows\Setup\CloudImaging\`, scripts to `...\Scripts\NN.ps1` (UTF-8 with BOM). Driver
  installer payloads (spec 005) are already staged by Install drivers; Prepare setup only lists them.
- **Manifest**: driver installers first (in resolved order), then During Windows setup scripts in
  profile order. Each item: order, kind, name, files (path relative to the system drive and
  SHA-256 of each), command (executable, arguments, working directory), timeout, success exit codes
  (`[0]` for scripts; spec 005 supplies `[0, 3010]`), paths to delete. Full schema in
  [contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md).
- **Run** (RunSynchronous in specialize, SYSTEM): for each item, verify every file's SHA-256; on a
  mismatch log and skip the item. Otherwise start it with a restricted token (no shutdown
  privileges) in a kill-on-close job object, created through a small `Add-Type` C# block (the
  applied OS has .NET Framework 4.8 and its compiler), wait up to the timeout, terminate the tree
  on timeout, log, continue. If the guard cannot be set up, no item runs and each is logged as
  failed with that reason.
- **Log**: `%WINDIR%\Logs\CloudImaging\SetupRunner.log`, ASCII, one line per item (UTC time,
  position, kind, name, outcome, exit code, duration) plus start and end lines. Never deleted by
  the runner. Nothing is sent to the portal (FR-014, spec 005 FR-057).
- **Cleanup**: in `End`/`finally`, delete every item's paths, the scripts folder, the manifest and
  `Windows\Setup\CloudImaging` itself (Windows PowerShell has already read the script into memory,
  so deleting it is safe). Cleanup runs after failures too.
- **Exit**: always 0, so Windows setup never treats the runner as a failed command.
- **Style**: follows the repository's PowerShell rules: comment-based help, Begin/Process/End,
  `[PSCustomObject]`, generic `List[System.Object]`, `catch [System.Exception]` with
  `Write-Warning` (plus the log line), no `Write-Host`, no backtick continuation, parameters with
  `HelpMessage` (`-ManifestPath`, `-LogPath`, both defaulted, so tests can point them at a temp folder).

**Rationale**: Full Windows always has Windows PowerShell 5.1 (spec assumption) and the runner must
not depend on anything in the OS image. Shipping it with the Client versions it with the code that
writes its manifest, so manifest and runner can never disagree. The restricted token reuses the
WinPE design (R6) for FR-015 in Windows setup.

**Alternatives considered**:
- A .NET 10 helper executable: the applied OS has no .NET 10 runtime, and a self-contained copy adds about 70 MB per device.
- A .NET Framework 4.8 helper: a second target framework, which the constitution only allows with a documented constraint, for logic that fits in one script.
- Running items directly as separate RunSynchronous entries: the spec requires exactly one Cloud Imaging entry, and verification, timeouts and logging would be lost.

**To validate**: `Add-Type` and the restricted process launch during specialize on 23H2, 24H2 and
25H2; Windows Installer packages that request a restart through the msiserver service (spec 005
must pass no-restart switches; the runner logs exit code 1641 as a failure) ([quickstart.md](quickstart.md) section 6).

---

## R9. Answer file validation: one rule set, two implementations

**Decision**: The rules in [contracts/answer-file-rules.md](contracts/answer-file-rules.md) are
implemented twice, once per runtime, and pinned together by one case file:

- **Server (authoritative)**: `ImagingCoreApi/Services/AnswerFileValidator.cs`, run by Core on
  every save, restore and duplicate. `XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`
  and `IXmlLineInfo` for line numbers. A violation returns 400 with a problem body carrying an
  `errors` array (`code`, `line`, `message`).
- **Client (live)**: `client/src/lib/answerFileValidation.ts`, run on every edit (debounced) in the
  answer file dialog, listing every problem with its line number and disabling Done and Save.
  Parsing uses `saxes`, a strict non-validating XML 1.0 parser that reports line and column and
  rejects malformed input, loaded with the dialog's code-split chunk.
- **Pinned together**: `tests/fixtures/answer-file-cases.json` (input XML, expected `code` and
  `line` per problem, expected message text) is read by `tests/cloud-imaging-portal/client/answer-file-validation.test.ts`
  and by `tests/CloudImaging.ImagingCoreApi.Tests/Services/AnswerFileValidatorTests.cs`, exactly as
  `autopilot-group-tag-cases.json` pins the group tag rules today.
- **Portal server**: passes the request through and forwards the `errors` array (bounded to 50
  entries of at most 300 characters each) in its error handler, so a direct API call to the portal
  backend gets the same list (US3 AS3).

**Rationale**: Portal and Core are different runtimes, so the constitution's "single source of
truth per runtime boundary" allows one implementation each; a shared fixture is the existing,
tested way to keep two implementations identical. Core rather than the portal server is
authoritative so the Operator API path is covered too.

**Alternatives considered**: Validating only on the server and calling it from the dialog (a round
trip per keystroke, and the dialog would be useless when the backend is slow); a hand-written XML
tokenizer in TypeScript (well-formedness edge cases are easy to get wrong); the browser's
`DOMParser` (no line numbers for valid elements, browser-specific error text).

---

## R10. Failure handling semantics

**Decision**:

| Situation | Complete with warnings (default) | Fail the deployment |
|---|---|---|
| Registry value cannot be written | Item Failed, step continues | Item Failed, step stops, remaining items Not run, step Failed, session Failed |
| In WinPE script fails, times out or has no PowerShell | Item Failed, next script runs | Same as above |
| Answer file cannot be written, or image not generalized with Windows setup work | Item Failed | Same as above |
| Pinned profile cannot be fetched or verified (before Format disk) | Not applicable: the descriptor carries the setting, so an unreachable profile always fails the session before the disk is touched | Same |
| During Windows setup results | Never change the result | Never change the result |
| Device without a profile (driver Specialize work only) | Always this column; the answer file failure names the driver installers that will not run | n/a |

A step with failed items under Complete with warnings is reported Completed with `WarningCount`
equal to the failed item count. Under Fail the deployment the step is reported Failed with
`errorDetail` naming the first failed item and its reason, before Configure boot runs, so the
device is not bootable into a half-prepared Windows (FR-028). Retry is unchanged: the technician
starts a new session, which pins the then-current version.

**Rationale**: Stopping at the first failure when the deployment is going to fail anyway avoids
further SYSTEM-level side effects (firmware changes) that the retry would repeat. The spec's
"next script runs" scenario describes the default setting.

---

## R11. Media Builder: PowerShell in every boot image

**Decision**: After `InjectNetFxOptionalComponentAsync`, inject `WinPE-Scripting` and then
`WinPE-PowerShell` (base cab plus `en-us` language cab each), using the same method shape and
missing-cab message as WMI and NetFx. This applies to both bases (ADK Windows PE and Windows RE)
and both architectures. Progress text: "Adding WinPE PowerShell support". The boot image manifest
records `powerShellIncluded` for diagnostics.

**Impact**: WinPE-PowerShell and its prerequisite add an estimated 20 to 40 MB to `boot.wim`
(to be measured per architecture and recorded in the release notes). The constitution's Client
start-up budget (main window within 5 seconds of process launch) is unaffected: PowerShell is not
loaded at start-up. WinPE boot time grows by the time it takes to read the extra megabytes from USB
into the RAM disk (well under a second on USB 3). RAM disk use grows by the same amount, well
within the 2 GB minimum. Boot images built before this release keep working; their In WinPE
scripts fail with the clear reason above.

**Rationale**: Microsoft documents WinPE-WMI, WinPE-NetFx and WinPE-Scripting as the prerequisites
of WinPE-PowerShell, in that order; the first two are already injected.

**Alternatives considered**: Making PowerShell an opt-in checkbox (a profile's In WinPE scripts
would then fail per device, depending on how the USB media was built); adding WinPE-StorageWMI and
WinPE-DismCmdlets as well (useful, but grows the image further; can be added later on demand).

**To validate** (spec risk 3): ARM64 ADK availability of both cabs; measured size and WinPE boot
time before and after on the reference hardware ([quickstart.md](quickstart.md) section 5).

---

## R12. Roles

**Decision**: Reuse spec 004 unchanged.

- Portal backend: add `requireTechnician = requireRole('CloudImaging.Technician', 'CloudImaging.Operator', 'CloudImaging.Administrator')`
  next to `requireOperator`, so every read guard names its accepted roles in the 403 detail.
  Reads use `requireTechnician`; writes (create, save, duplicate, restore, delete) use
  `requireOperator`; the retention value travels with `PUT /api/portal-config`, which is already
  Administrator-only. Reader, AutopilotApprover and users without a role are refused by both guards.
- Portal client: two new capability flags in `portalRoles.ts`: `canViewSetupProfiles`
  (Technician, so Operator and Administrator through implication) and `canManageSetupProfiles`
  (Operator). The navigation item uses the existing `'imaging'` access level.
- Operator API: no middleware change. New functions are not on the Media Builder allowlist, so
  they need `CloudImaging.PortalAccess`, which only the Portal backend holds.
- Every new route gets a row in `role-route-matrix.test.ts`.
- Until spec 004 ships in a deployment, Administrator alone holds the write permissions (spec
  dependency); the code is the same.

---

## R13. Location deletion

**Decision**: `LocationFunctions.DeleteLocation` in Core:

1. Reads `target/{locationId}`. If a profile targets the location and it is that profile's only
   target row, return 409 with "<location> is the only location of setup profile "<profile>". Add
   another location to the profile or delete the profile, then try again." Nothing is deleted.
2. Otherwise deletes the location, removes it from Wi-Fi networks (unchanged), deletes the target
   row, and writes a history row on that profile: "<location> removed because the location was
   deleted", with the deleting user.

The deleting user reaches Core the way the existing Autopilot group tag delete does it: the portal
backend reads it from the token (`decidingUser`) and forwards `?changedBy=<upn>` through the
Operator API. The Locations page names the affected profile in its delete confirmation overlay
(it loads the profile list) and shows the 409 detail as an error toast.

**Rationale**: Core already performs the Wi-Fi clean-up in the same function; the profile clean-up
belongs next to it. A point read keeps the check cheap.

---

## R14. Portal composition

**Decision**:

- **Pages**: `SetupProfilesPage` (list, under Imaging after Recovery Images, and after Drivers
  once spec 005 adds it) and `SetupProfileEditorPage` (routes `/setup-profiles/new`,
  `/setup-profiles/:profileId`, read-only `/setup-profiles/:profileId/versions/:version`). An
  editor page rather than a dialog, because a profile holds three tables, a targeting section and
  a save bar, and the History dialog opens from it.
- **Extracted shared components** (behaviour unchanged for Wi-Fi): `components/LocationPicker.tsx`
  from `WifiNetworkDialog.tsx`, gaining an optional `disabledReason(locationId)` used to disable
  locations another profile targets with the profile named in the tooltip;
  `components/TargetingBadges.tsx` from `WifiNetworksPage.tsx`'s `LocationsCell`, with the
  all-label as a prop ("All locations" for Wi-Fi, "All devices" for profiles).
- **Ordering**: move up/down buttons as on the Wi-Fi page, but order is part of the draft and is
  saved with the version (no separate order call).
- **Editors**: a `CodeTextArea` primitive (monospace textarea with a line number gutter) in
  `components/ui/` for scripts and the answer file. No code editor dependency.
- **Confirmations**: delete, restore and discard use `ConfirmImpactDialog`, which gains an optional
  `details` slot so Start imaging can list profile lines and Delete can list the locations that
  lose the profile.
- **Unsaved changes**: leaving the editor with a dirty draft is blocked with the same overlay.
- **Save bar**: "Save as version N" when content changed; "Save changes" when only the name or
  targeting changed (no new version); Discard always. A 409 from a concurrent save shows who saved
  and asks to reload.
- **Names**: unique per tenant (case-insensitive) so the Start imaging overlay and History are
  unambiguous; Duplicate proposes "<name> (copy)", then "(copy 2)", and so on.
- **Duplicate**: opens `/setup-profiles/new` prefilled with the source's current content and no
  targeting; nothing is stored until Save, which sends `duplicatedFrom` with the create request.
  A stored profile therefore always targets something (FR-002 and FR-036 hold together).
- **Rename**: name is not versioned (like targeting) and appears in the History Targeting tab as
  a profile change.

**Rationale**: FR-004 and FR-006 require the Wi-Fi picker and badges; extracting them is the only
way to keep the two surfaces identical.

---

## R15. Timeline

**Decision**: `lib/imagingProgress.ts` `PIPELINE_STEPS` gains an `optional` flag and the
PrepareSetup entry (label "Prepare setup") between Apply image and Configure boot (spec 005 inserts
Install drivers before it). `SessionProgressDetails` builds its rail from the required steps plus
optional steps that were reported or are expected, and uses a column count from the list instead
of the fixed `grid-cols-5`. Expected means the session has a pinned profile. A terminal session
with a pinned profile and no PrepareSetup report shows the step as "Not run" with the tooltip
"This device's boot image is older than this feature, so the profile was not applied. Update the
boot image." Completed with `warningCount > 0` shows the amber marker and "Completed with warnings".
The Prepare setup card names the profile and version, shows the summary from `itemCounts`, and on
"Show items" loads the item list (failures first, full reason in a tooltip). `progressStepLabel`
covers the new step for the Devices table and the failure report.

**Rationale**: FR-040 forbids a fixed step count; deriving "expected" from the pin keeps old
sessions at five steps and makes a skipped profile on an old Client visible.

---

## R16. Release and compatibility

- **Streams**: Portal, Operator API, Imaging Core and Device Gateway ship in the backend stream;
  the Client (with the setup runner) in the client stream; the Media Builder in its own stream.
  Each needs a notes file under `.github/release-notes/<stream>/` (spec 004 convention).
- **Order**: backend first (new container, tables, routes), then Media Builder (PowerShell), then
  a Client release and regenerated boot images. Profiles can be created as soon as the backend is
  upgraded; devices apply them once they boot a new Client.
- **Bicep**: one new container `setup-profiles` in `storage.bicep` (no lifecycle rule; it must not
  match the `session-logs/` prefix). Applied by the documented template spec upgrade.
- **Mixed versions**: covered in R3 (old backend, new Client) and R15 (new backend, old Client).
- **No data migration**: existing sessions and history have no pin and render as today.

---

## R17. Tests (test-first)

- **Core (xUnit)**: `SetupProfileValidatorTests`, `AnswerFileValidatorTests` (shared fixture),
  `SetupProfileServiceTests` (versioning, restore, duplicate, retention with pinned versions,
  change summaries, concurrency 409), `SetupProfileResolverTests`, `OverallProgressCalculatorTests`
  (weight 0, `IsPipelineComplete` with and without PrepareSetup, failed optional step),
  `ReportProgressPayloadTests` (items, truncation), integration tests for target uniqueness under
  concurrent saves, assignment pinning (single and bulk), location deletion rule, item storage
  and purge.
- **Device Gateway**: `SetupProfileContractTests` (200 shape, 204, token mismatch 403, upstream 503),
  `ReportProgressContractTests` (items relayed unchanged).
- **Operator API**: `SetupProfileContractTests`, authorization (PortalAccess only).
- **Client**: `UnattendComposerTests` (empty start, existing pass and component, other
  architecture untouched, renumbering, exactly one runner entry), `OfflineRegistryPathTests`
  (CurrentControlSet mapping, data parsing), `PrepareSetupServiceTests` (phase order, failure
  handling both ways, no PowerShell, hive load failure, generalization check, driver hand-off),
  `SetupRunnerManifestBuilderTests`, `ImagingWorkflowViewModelTests` (step placement, skip, fetch
  failure before format), `ProgressViewModel` dynamic steps, and `SetupRunnerScriptTests`, which
  run the real runner under `powershell.exe` against a temp manifest (verification mismatch,
  timeout, continue after failure, cleanup after failure, log format). The restart guard is
  verified on a device (quickstart 6.8).
- **Media Builder**: `BootImageGenerationTests` (component order WMI, NetFx, Scripting, PowerShell;
  missing cab message).
- **Portal server (Vitest + Supertest)**: `setup-profiles-routes.test.ts` (pass-through, actor
  from token never from body, 4 MB body limit on this router only, validation `errors` forwarded),
  `role-route-matrix.test.ts` rows for the 10 new routes, locations delete forwards `changedBy`.
- **Portal client (Vitest)**: `answer-file-validation.test.ts` (shared fixture),
  `setup-profiles.test.ts` (overlay grouping, disabled reasons, save bar label, draft diff),
  `portal-roles.test.ts` (new flags), `session-progress-details.test.tsx` (dynamic rail, warning
  marker, Not run, old five-step sessions unchanged).
- **Manual** (WinPE, specialize, hardware): [quickstart.md](quickstart.md) sections 5 and 6.

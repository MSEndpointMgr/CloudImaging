# Data Model: OS Driver Management

**Feature**: [spec.md](spec.md) | **Research**: [research.md](research.md) | **Date**: 2026-10-24

Storage: Azure Table Storage and the new private Blob container `drivers` on the Imaging Core
storage account, created by Core at startup (no infrastructure deployment). Contracts live in `src/CloudImaging.Contracts` (shared by Core, Gateway, Operator
API and Client); the portal mirrors them in `client/src/lib/drivers.ts`. JSON is camelCase with
string enums (`JsonSerializerDefaults.Web` plus `JsonStringEnumConverter`).

---

## 1. Enums (Contracts, `Enums/DriverEnums.cs` unless noted)

| Enum | Values | Notes |
|---|---|---|
| `DriverVendor` | `Hp`, `Dell`, `Lenovo` | Lenovo is listed but unsupported: never onboardable, sync switch disabled. |
| `DriverAction` | `Offline`, `OfflineAndSpecialize`, `Specialize`, `Skip` | UI labels: Offline, Offline + Specialize, Specialize, Skip. |
| `DriverActionSource` | `Locked`, `ReplacedByCustom`, `Custom`, `RolledBack`, `Override`, `Rule`, `Default` | Precedence order in the Source column. |
| `DriverComponentOrigin` | `Vendor`, `Custom`, `RolledBack` | On set items and step items; what kind of component the device got. |
| `DriverRollBackUntil` | `NewerVersion`, `Undone` | `NewerVersion`: ends when a sync finds a version newer than the one that was current when the roll back started. |
| `DriverRuleScope` | `AllVendors`, `Hp`, `Dell` | |
| `DriverRuleMatch` | `Category`, `NamePattern`, `ComponentId` | Specificity: ComponentId > NamePattern > Category. |
| `DriverComponentState` | `Active`, `WaitingForApproval`, `Withdrawn` | See section 6. |
| `WindowsRelease` | `Windows10_22H2`, `Windows11_23H2`, `Windows11_24H2`, `Windows11_25H2` | Label "Windows 11, version 24H2". Extended in code. |
| `DriverSyncSchedule` | `Daily`, `SelectedDays`, `Manual` | |
| `DriverSyncTrigger` | `Scheduled`, `Manual`, `Check`, `ModelCheck` | `Scheduled` and `Manual` (Sync now) are syncs: they refresh the vendors' model lists and check every onboarded model. `Check`: Check for updates on the Models tab, every onboarded model, components only. `ModelCheck`: Check for updates on one model. |
| `DriverSyncResult` | `Running`, `Completed`, `PartlyFailed`, `Failed` | |
| `DriverSyncItemOutcome` | `Updated`, `New`, `NewWaitingForApproval`, `Withdrawn`, `Removed`, `Failed` | |
| `DriverSkipReason` | `NoMatchingModel`, `NoWindowsRelease`, `ReleaseNotOnboarded`, `NoDriverSet`, `ArchitectureMismatch`, `NoneChosen` | Pinned on the session when no set applies. `NoneChosen`: No drivers was chosen when imaging started; the step note carries no warning. |
| `DriverChoiceKind` | `Model`, `None` | Request-only: a manual choice per session when imaging starts. Absent means use the match. |
| `ImagingStepName` (existing) | adds `InstallDrivers` | Appended; optional; progress weight 0. Order: ApplyImage, InstallDrivers, PrepareSetup, ConfigureBoot. |
| `StepItemKind` (existing) | adds `DriverComponent` | |
| `StepItemOutcome` (existing) | adds `Skipped` | Component not processed because the device lacks its hardware. |

## 2. Contract changes to existing types

| Type | Change |
|---|---|
| `OsImage` | `WindowsRelease? WindowsRelease`. Set on upload or edit by Operator or Administrator; null means no driver work. |
| `DeviceHardwareMetadata` | `string? SystemSku` (`Win32_ComputerSystem.SystemSKUNumber`, trimmed, at most 64 chars). Copied in `DeviceGatewayApiClient.SignPayload`. |
| `DeviceSession` | `Guid? DriverModelId`, `string? DriverModelName`, `WindowsRelease? DriverRelease`, `int? DriverSetVersion`, `DriverSkipReason? DriverSkipReason`, `string? DriverMatchKey` (the device's HP system ID or Dell SKU used for matching, for the skip note), `bool DriverChosenManually`, `string? DriverChosenBy` (UPN from the token, at most 256 chars), `string? DriverCriticalCategories` (JSON array of the pinned model's vendor categories that were critical at assignment). Written once at assignment. |
| `ImagingStepItem` | `DriverAction? DriverAction` (null for non-driver items), `string? ComponentVersion`, `DriverComponentOrigin? DriverOrigin`, `bool? FromUsbCache` (true when the file came from the USB drive). |
| `UsbPreparationManifest` (Media Builder, `BOOT:\cloudimaging-manifest.json`) | `List<PreloadedDriverSet>? DriverSets`: `ModelId`, `ModelName`, `Release`, `SetVersion`, `Components`, `Bytes`. Informational only; nothing is pinned from it. |
| Step progress report and `ImagingStep` | `string? StatusNote` (at most 300 chars), shown under the step, used for a skipped step. |

## 3. Tables

All tables are created at Core startup (`EnsureTableExistsAsync`). Keys are case-sensitive; vendor
and match keys are stored upper-case.

### 3.1 `DriverVendorSettings` (PK `vendor`, RK vendor name)

| Field | Type | Rules |
|---|---|---|
| `SyncEnabled` | bool | Default false. Lenovo always false and not writable. Onboarding turns it on. |
| `ChangedAt`, `ChangedBy` | DateTimeOffset?, string? | From the token (portal server). |

### 3.2 `DriverSyncSettings` (PK `settings`, RK `default`)

| Field | Type | Rules |
|---|---|---|
| `Schedule` | `DriverSyncSchedule` | Default `Daily`. |
| `Weekdays` | int (bit mask, Monday = 1) | Required non-zero for `SelectedDays`. |
| `RepeatWeeks` | int 1 to 4 | Default 1. Used by `SelectedDays` only. |
| `AnchorWeek` | string `yyyy-MM-dd` | The Monday, in `TimeZone`, of the week holding the first slot after the last save; rewritten on every save. A `SelectedDays` slot is due only in weeks whose distance from it is a multiple of `RepeatWeeks`. |
| `LocalTime` | string `HH:mm` | Default `02:00`. |
| `TimeZone` | string | The `ianaId` of an entry in the stored list ([contracts/time-zones.json](contracts/time-zones.json): 142 Windows time zones, each with its Windows display name and IANA name) that `TimeZoneInfo` resolves on the host. Default: the entry whose `covers` includes the browser's zone, otherwise `Etc/UTC` ("(UTC) Coordinated Universal Time"). |
| `WaitForApprovalOfNewComponents` | bool | Default false. |
| `PreviousVersionsToKeep` | int 0 to 3 | Default 1. Clean-up keeps the files of this many versions per model, release and vendor lineage that came directly before the current one (3.14, newest first). |
| `LastScheduledSlotUtc` | DateTimeOffset? | Written with ETag when a scheduled run starts (at most once per slot). |
| `LastRunStartedAt` | DateTimeOffset? | `Scheduled`, `Manual` and `Check` runs; drives the 60-minute limit shared by Sync now and Check for updates on the Models tab, and the Models tab's "Last checked" time. |
| `ChangedAt`, `ChangedBy` | DateTimeOffset?, string? | From the token (portal server). |

### 3.3 `DriverCatalogModels` (PK vendor, RK catalog model key)

Vendor model entries, refreshed by each run for enabled vendors (FR-005).

| Field | Type | Notes |
|---|---|---|
| `Name` | string | HP `SystemName`; Dell model name. |
| `MatchKeys` | string (JSON array) | HP system ID (`8C26`); Dell system SKUs (`0CB2`, `0CB4`). |
| `Architecture` | `MachineArchitecture` (existing x64, ARM64) | HP from the platform list `OSArchitecture`; Dell from the catalog's `osArch`. |
| `Releases` | string (JSON array of `WindowsRelease`) | HP from the platform list `OS` entries for that architecture; Dell all Windows 11 releases (Dell has no release granularity). |
| `SkuCatalogs` | string (JSON) | Dell only: per SKU `{ sku, path, sha256 }`. |
| `SeenAt` | DateTimeOffset | Entries not seen in a vendor's latest model list (from a sync or a dialog fetch) are deleted unless onboarded. |

Per vendor, row `list/<vendor>` holds `FetchedAt` (last successful model list read, by a sync or the
Onboard model dialog; older than 7 days means the dialog fetches again), `CatalogUrl`, `ModelCount`,
`LastAttemptAt` and `LastError` (shown by the dialog when the catalog cannot be reached).

RK: HP `<systemId>_<architecture>` (for example `8C26_X64`, `8CBE_ARM64`); Dell the model name
normalised (upper-case, spaces to `_`).

### 3.4 `DriverModels` (PK `model`, RK modelId) and `DriverMatchKeys` (PK vendor, RK match key)

| Field | Type | Rules |
|---|---|---|
| `Vendor` | `DriverVendor` | Hp or Dell. |
| `Architecture` | x64 or ARM64 | Copied from the catalog model; must equal the OS image's architecture at assignment. |
| `DisplayName` | string | From the catalog; 1 to 100 chars. |
| `CatalogModelKey` | string | Link to `DriverCatalogModels`. |
| `MatchKeys` | JSON array | At least one. Each key reserved by inserting `DriverMatchKeys(vendor, key) -> modelId`; insert conflict means another onboarded model owns it (FR-007). |
| `Releases` | JSON array of `WindowsRelease` | At least one; only releases the catalog lists. |
| `State` | `Draft` or `Active` | `Draft` from Onboard until Complete onboarding: matches no device, is not offered at imaging start, downloads nothing. |
| `OnboardedAt`, `OnboardedBy` | DateTimeOffset, string | From the token (portal server). |
| `CompletedAt`, `CompletedBy` | DateTimeOffset?, string? | Set by Complete onboarding. |
| `LastCheckedAt` | DateTimeOffset? | Start of the model's last Check for updates; drives its 15-minute limit. |

Removal deletes the row, its match keys, overrides, component rows, set pointers and set documents
(pinned ones stay until their sessions end), keeps rules and run history.

### 3.5 `DriverComponents` (PK `<modelId>_<release>`, RK lineage key)

The latest known version of each component lineage for one model and release.

| Field | Type | Notes |
|---|---|---|
| `ComponentId` | string | HP SoftPaq ID (`sp150123`); Dell package ID (`CP0VC`). |
| `Name`, `Category` | string | Category: HP `Driver - Network`; Dell component type plus category (`Network`). |
| `Version`, `ReleasedAt` | string, DateTimeOffset | Dell version is `dellVersion` (A16). |
| `PreviousVersion` | string? | |
| `Size`, `Sha256`, `Url`, `FileName` | long, string, string, string | Url must pass the vendor host allowlist. |
| `AllowedActions` | JSON array of `DriverAction` | From classification; `[Skip]` means Locked. |
| `DefaultAction` | `DriverAction` | |
| `VendorData` | string (JSON) | HP: `dpbCompliant`, `ssmCompliant`, `silentInstall`, `contentTypes`, `releaseType`. Dell: `packageType` (LW64, LWXP), `componentType`, `criticality`, `pciIds`, `bundlesApp`. Never sent to devices except the install command. |
| `DescriptionUrl`, `Description` | string?, string? | Description at most 2000 chars. |
| `State` | `DriverComponentState` | Section 6. |
| `AddedAt`, `UpdatedAt` | DateTimeOffset | `UpdatedAt` drives the "updated by today's sync" filter. |
| `VerifiedVersion`, `VerifiedFile` | string?, string? | Last version whose file passed verification (FR-031). |

Lineage key: HP the oldest ID in the `Supersedes` chain seen for this lineage (stable as new
SoftPaqs supersede old ones); Dell `dell-<componentID>` from `SupportedDCHDevices/Device`, falling
back to `name-<normalised name>`.

### 3.6 `DriverOverrides` (PK `<modelId>_<release>`, RK lineage key)

`Action` (`DriverAction`), `ChangedAt`, `ChangedBy`. Applies only if the action is allowed.

### 3.7 `DriverRules` (PK `rule`, RK ruleId)

| Field | Type | Rules |
|---|---|---|
| `Name` | string | 1 to 100 chars, unique ignoring case. |
| `Scope` | `DriverRuleScope` | |
| `Match` | `DriverRuleMatch` | |
| `Value` | string | Category: exact category name; NamePattern: case-insensitive, `*` wildcard only, 1 to 200 chars; ComponentId: exact ID. |
| `Action` | `DriverAction` | |
| `ChangedAt`, `ChangedBy` | DateTimeOffset, string | Most recent change wins a same-layer, same-kind conflict. |

Suggested rules (FR-023) are a constant list in Core (`HP Services Scan for Commercial`,
`Seamless Firmware Update Service`, `HP App`, each NamePattern, Skip, scope Hp), returned by
`GET rules/suggestions` minus rules that already exist; never inserted without a request.

### 3.8 `DriverCriticalCategories` (PK vendor, RK category)

`ChangedAt`, `ChangedBy`. No rows by default.

### 3.9 `DriverSetPointers` (PK `<modelId>_<release>`, RK `pointer`)

| Field | Type | Rules |
|---|---|---|
| `CurrentVersion` | int? | Set when the first set is built. |
| `PreviousVersion` | int? | Roll-back target. |
| `CurrentFingerprint` | string | |
| `RolledBackFromFingerprint` | string? | A build with this fingerprint creates no set. Cleared when a set with a different fingerprint becomes current. |
| `LastVersion` | int | Monotonic, for numbering. |
| `PendingVersion` | int? | Built set waiting for its files (FR-024, FR-031). |
| `Dirty` | bool | Needs re-resolution (policy change). |
| `BuildAfter` | DateTimeOffset? | Set to now + 2 minutes on every policy change, with a `BuildSet` message delayed until then; the message builds the pair only once it has passed. Build now clears it. |
| `PendingChanges` | int | Changes since the last build, for the "3 changes are waiting" banner. |
| `BuiltAt` | DateTimeOffset? | Current set's build time. |

All writes use the row's ETag.

### 3.10 `DriverFiles` (PK vendor, RK `<componentId>_<version>`)

`BlobName`, `Size`, `Sha256`, `Status` (`Downloading`, `Verified`, `Failed`), `VerifiedAt`,
`FailureReason`. A file is usable only when `Verified`.

### 3.11 `DriverSyncRuns` (PK `run`, RK inverted start ticks; plus RK `active` lock row)

| Field | Type | Notes |
|---|---|---|
| `RunId` | Guid | |
| `Trigger` | `DriverSyncTrigger` | |
| `ModelId` | Guid? | Set for `ModelCheck`: the run reads only this model's vendor catalog. |
| `Outstanding` | int | Queue messages of the run not yet done (ETag); the message that brings it to zero enqueues `FinalizeRun`. |
| `StartedAt`, `FinishedAt` | DateTimeOffset, DateTimeOffset? | |
| `Result` | `DriverSyncResult` | |
| `StartedBy` | string? | Manual runs. |
| `Counts` | JSON | Per outcome, for the one-line summary. |
| `Vendors` | JSON | Per vendor: processed or failed with reason, and for syncs `newModels` (models added to the vendor's list by this run). |

`DriverSyncRunItems` (PK runId, RK index): `ModelId`, `ModelName`, `LineageKey`, `ComponentName`,
`OldVersion`, `NewVersion`, `Outcome`, `Reason`. Runs and items older than 90 days are deleted at
the end of every run.

### 3.12 Queue `driver-work` (Core storage account, not a table)

Every unit of background work is one JSON message (research R1), processed by
`DriverWorkProcessor`; there is no polling timer.

| `kind` | Other fields | Enqueued by | Delay |
|---|---|---|---|
| `ScheduledSync` | `slotUtc` | Saving the schedule, Core start, the run that claimed the previous slot | Until `slotUtc` (at most 7 days, then re-enqueued) |
| `FetchVendorCatalog` | `runId`, `vendor`, `modelId?` | Run start (one per enabled vendor, or the model's vendor for `ModelCheck`); refreshes the vendor's model list only in `Scheduled` and `Manual` runs, then enqueues `ResolveModel` per onboarded model | None |
| `ResolveModel` | `runId?`, `modelId`, `release` | Catalog fetch, Complete onboarding | None |
| `DownloadFile` | `runId?`, `vendor`, `fileKey`, `buildId?` | Resolve or build that needs a file not yet stored, component roll back to a version that is not stored | None |
| `BuildSet` | `modelId`, `release` | Policy change (dirty pair), Build now, custom component change | 2 minutes, or none for Build now |
| `FinalizeRun` | `runId` | Last message of a run; run start (2-hour watchdog) | None, or 2 hours |

`FinalizeRun` sets the result, releases the `active` lock and, for `Scheduled`, `Manual` and
`Check` runs, runs clean-up (research R16); a `ModelCheck` run does not clean up.

The host retries a message at most 3 times; then it moves to `driver-work-poison`, whose trigger
records the file or run item as failed with the reason and decrements the run's `Outstanding`.
Messages are idempotent: a `DownloadFile` for a file already `Verified` and a `BuildSet` for a pair
that is no longer dirty do nothing.

### 3.13 `DriverChangeLog` (PK `log`, RK inverted ticks)

`At`, `By` (UPN), `ObjectId`, `Area` (`Model`, `Release`, `Rule`, `Override`, `Approval`,
`RollBack`, `Sync`, `Vendor`, `CriticalCategory`, `Component`, `Storage`), `Subject`, `Change` (one line).
Every write in FR-075 adds one row (FR-077); a change of Previous versions is `Storage` with
subject "Previous versions" and change "1 to 2".

### 3.14 `DriverComponentVersions` (PK `<modelId>_<release>`, RK `<lineageKey>_<inverted first-use ticks>`)

One row per version a component has had on a model and release (FR-089). Kept until the model or
release is removed; never cleaned up with files.

| Field | Type | Notes |
|---|---|---|
| `LineageKey`, `ComponentId`, `Version` | string | Custom components use lineage `custom-<customId>`. |
| `FirstSet`, `LastSet` | int, int? | `LastSet` null while the version is in the current set. |
| `From`, `To` | DateTimeOffset, DateTimeOffset? | |
| `ChangedBy` | string | `Sync`, `Onboarding by <upn>`, `Roll back by <upn>`, `Custom component by <upn>`. |
| `Url`, `Sha256`, `Size`, `FileName` | string?, string, long, string | Vendor location kept so a roll back can download a removed file again. Null `Url` for custom components. |
| `Origin` | `DriverComponentOrigin` | |
| `Available`, `AvailabilityCheckedAt` | bool?, DateTimeOffset? | Whether the vendor still hosted `Url` at the last check (Roll back component opening); null until checked. Not used for stored files. |

### 3.15 `DriverRollBacks` (PK `<modelId>_<release>`, RK lineage key)

`Version`, `Until` (`DriverRollBackUntil`), `SkippedVersion` (the version current when the roll
back started; `NewerVersion` ends when a sync finds a newer one), `ChangedAt`, `ChangedBy`. Applies
after policy resolution and before the fingerprint; the held version's file is protected from
clean-up while the row exists.

### 3.16 `DriverCustomComponents` (PK `custom`, RK customId) and `DriverCustomTargets` (PK customId, RK modelId)

| Field | Type | Rules |
|---|---|---|
| `Name`, `Version`, `Category` | string | Name 1 to 100 chars; version 1 to 50 chars. |
| `Action` | `DriverAction` | Not Skip. |
| `BlobName`, `Sha256`, `Size`, `FileName` | string, string, long, string | Computed on upload; at most 5 GB. |
| `InfCount` | int | Offline actions: at least 1 (counted from the ZIP's central directory on upload). |
| `InstallCommand` | string? | Specialize actions: executable and arguments relative to the package. |
| `UploadedAt`, `UploadedBy`, `ChangedAt`, `ChangedBy` | DateTimeOffset, string, DateTimeOffset?, string? | From the token (portal server). |

`DriverCustomTargets` rows: `ReplacesLineageKey?` (one replacement per vendor lineage per model,
enforced by a `DriverReplacements` row `(modelId, lineageKey) -> customId`), `NoticeVersion` (the
vendor version a banner was last shown for) and `DismissedAt`, `DismissedBy`.

### 3.17 `DriverSetBuilds` (PK `<modelId>_<release>`, RK version)

`Trigger` (`Onboarding`, `Sync`, `Policy`, `RollBack`, `Custom`), `BuiltAt`, `ReadyAt?`,
`Components`, `NewComponents`, `ReusedComponents`, `SizeBytes`, `DownloadedBytes`, `FailedComponents`.
Feeds the Downloads tab (in progress: `ReadyAt` null; finished: `ReadyAt` in the last 7 days) and
the "Driver sets" ranges of the version history. Rows older than 90 days are deleted with the run
history.

## 4. Blobs (container `drivers`)

| Path | Content | Lifetime |
|---|---|---|
| `catalogs/<vendor>/<sha256 of URL>.cab` | Fetched vendor catalog, metadata `fetchedAt`, `sourceUrl` | Overwritten; preview cache 24 hours. |
| `files/<vendor>/<componentId>/<version>/<file name>` | Original vendor file, metadata `sha256`, `verified` | Until clean-up finds no reference, kept previous versions (`PreviousVersionsToKeep`) included. |
| `custom/<customId>/<version>/<file name>` | Uploaded custom package, metadata `sha256` | While the custom component or its version history refers to it; never downloaded again. |
| `sets/<modelId>/<release>/<version:000000>.json` | Resolved set document (5.1) | Current and previous, plus pinned ones until their sessions end. |

### 4.1 USB drive cache (CACHE partition, NTFS)

Next to the Client's OS image cache (`CloudImagingCache\images\<sha256>\`):

| Path | Content |
|---|---|
| `CACHE:\CloudImagingCache\drivers\<sha256>\<file name>` | A verified vendor file or custom package, the original bytes. |
| `CACHE:\CloudImagingCache\drivers\<sha256>\metadata.json` | `{ "Sha256Hash", "FileName", "SizeBytes", "CachedAt", "LastUsedAt" }` (PascalCase, like `CacheEntryMetadata`). Written last, after the file is verified; an entry without metadata is incomplete and deleted. |

Written by the Client after a verified download, and by Media Builder when preloading. Keyed by
SHA-256 only, so one file serves every model, set and release that uses it. The 30-day idle purge
and the 500 MB free-space floor of the image cache apply to driver entries too.

## 5. Documents

### 5.1 Resolved set (`sets/...json`)

```json
{
  "schemaVersion": 1,
  "modelId": "guid", "release": "Windows11_24H2", "version": 7,
  "fingerprint": "64 hex", "builtAt": "2026-10-22T02:41:00Z",
  "items": [
    {
      "lineageKey": "sp140001", "componentId": "sp150123", "name": "Intel Wireless LAN Driver",
      "category": "Driver - Network", "version": "23.80.0.4", "action": "Offline",
      "file": "files/Hp/sp150123/23.80.0.4/sp150123.exe", "sizeBytes": 113000000,
      "sha256": "64 hex", "origin": "Vendor",
      "install": { "extract": "Hp", "command": null },
      "pciIds": []
    }
  ]
}
```

`install.extract`: `Hp` (SoftPaq extract switches), `Dell64` (DUP `/drivers`), `Zip` (custom
component ZIP, unpacked by the Client with path checks), `None` (Dell 32-bit Specialize and custom
installers). `install.command` (Specialize actions only): executable and arguments relative to
the staged folder. Items are in resolved order: Offline actions first, then by category and name.
The set holds only included components (no Skip items); the fingerprint is SHA-256 over sorted
`lineage|componentId|version|action|origin|sha256` lines of those items (the hash catches a custom
package replaced under the same version). Whether an item is critical is not stored in
the set: it comes from `DeviceSession.DriverCriticalCategories` when the descriptor is served.

### 5.2 Device descriptor (Gateway response, see contracts/device-gateway.md)

Per component: index, name, version, origin, action, size, SHA-256, `downloadUrl` (60-minute SAS),
`downloadUrlExpiresAt`, extract kind, install command, critical flag (from the session's pinned
critical categories), PCI IDs.

## 6. State transitions

**Onboarded model**: Onboard -> `Draft` (match keys reserved, components resolved, nothing
downloaded, no set) -> Complete onboarding -> `Active` (vendor sync turned on, `ResolveModel` per
release enqueued, first set built when its files are verified). Remove is allowed in both states.

**Component (per model and release)**

```text
(not known) --sync, continues a known lineage--> Active (new version, keeps action)
(not known) --sync, new lineage, approval on--> WaitingForApproval --Approve--> Active
(not known) --sync, new lineage, approval off--> Active
Active --vendor stops publishing--> Withdrawn --vendor publishes the lineage again--> Active
Withdrawn --administrator removes--> (deleted)
```

WaitingForApproval components are excluded from the resolved set and do not affect a run result.

**Sync run**: `Running` -> `Completed` (no failed item) | `PartlyFailed` (some items or vendors
failed) | `Failed` (nothing processed). An instance crash returns the message to the queue, where
it is retried (at most 3 times, then recorded as failed); the 2-hour `FinalizeRun` ends a run still
`Running` as `Failed`.

**Set pointer**: policy change -> `Dirty`, `BuildAfter` = now + 2 minutes, `PendingChanges` + 1 ->
(`BuildAfter` passed, or Build now) build -> fingerprint unchanged or equal to
`RolledBackFromFingerprint`: no change | otherwise `PendingVersion` -> all files `Verified` ->
current becomes previous, pending becomes current. Roll back driver set: swap current and previous,
set `RolledBackFromFingerprint`. Syncs build at once (no wait).

**Component roll back**: created -> held version used by every build -> (`Until = NewerVersion`
and a sync finds a version newer than `SkippedVersion`) or undone -> deleted, the component returns
to its resolved version at the next build. Creating it fails without change when the held version's
file is not stored and cannot be downloaded and verified again.

**Custom component**: added -> in every build of its target models (replacing its vendor lineage
where set) -> removed -> its target models build without it; the replaced vendor component returns
to its own action. A new version of the replaced lineage sets `NoticeVersion` and shows the banner
until dismissed.

**Session pin**: written once at assignment; never updated. A manual choice replaces the match
before resolution (model chosen: its current set for the image's release; No drivers:
`NoneChosen`) and sets `DriverChosenManually` and `DriverChosenBy`. The model vendor's critical
categories are copied to `DriverCriticalCategories`. Files of the pinned set are protected from
clean-up while the session is in the `active` partition.

## 7. Validation summary

| Rule | Where |
|---|---|
| Match key owned by one model | `DriverMatchKeys` insert (409 names the owning model). |
| Release must be listed by the catalog model | Core on onboard and add release. |
| Rule value and pattern syntax, unique name | Core (400 with field errors). |
| Override and rule actions must be allowed for the component | Ignored at resolution; Core refuses an override with a disallowed action (400). |
| Schedule fields | Core: weekdays for SelectedDays, `RepeatWeeks` 1 to 4, `HH:mm`, IANA zone resolvable. |
| Previous versions | Core: `PreviousVersionsToKeep` 0 to 3. |
| Vendor URL allowlist | Core before any copy. |
| Windows release value | Contracts enum; Core 400 on unknown. |
| Manual driver choice | Core at assignment: model exists, same architecture as the image, current set for the image's release; otherwise 409 for the whole request, naming the sessions. At most one choice per session ID; a choice for a session not in the request is 400. |
| Component roll back | Core: the version is in the component's history and is not the current one; 409 when the file cannot be downloaded or verified again (reason relayed). |
| Custom component | Core on upload: at most 5 GB; ZIP with at least one `.inf` entry for Offline actions; installer and command for Specialize; name, version and category set; target models onboarded; one replacement per vendor lineage per model (409 names the other custom component). |

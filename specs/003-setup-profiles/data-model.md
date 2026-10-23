# Data Model: Setup Profiles

**Feature**: [spec.md](spec.md) | **Research**: [research.md](research.md) | **Date**: 2026-10-23

Field names are the JSON (camelCase) names; C# properties are PascalCase. Enums travel as strings
(`JsonStringEnumConverter`), as every enum in `CloudImaging.Contracts` does. Shared .NET types live
in `CloudImaging.Contracts` (namespace `CloudImaging.Contracts.Models.SetupProfiles` and `CloudImaging.Contracts.Enums`);
portal TypeScript types mirror them in `client/src/lib/setupProfiles.ts`.

---

## 1. Enumerations

| Enum | Values | Notes |
|---|---|---|
| `ImagingStepName` (existing) | adds `PrepareSetup` | Additive. Spec 005 adds `InstallDrivers`. |
| `SetupRegistryHive` | `Software`, `System`, `DefaultUser` | UI labels: HKLM SOFTWARE, HKLM SYSTEM, Default user. |
| `SetupRegistryValueType` | `String`, `ExpandString`, `MultiString`, `DWord`, `QWord` | UI labels: String, Expandable string, Multi-string, DWORD, QWORD. |
| `SetupScriptPhase` | `WinPE`, `WindowsSetup` | UI labels: In WinPE, During Windows setup. |
| `SetupFailureHandling` | `CompleteWithWarnings`, `FailDeployment` | Default `CompleteWithWarnings`. UI labels: Complete with warnings, Fail the deployment. |
| `StepItemKind` | `RegistryValue`, `WinPEScript`, `WindowsSetupScript`, `DriverInstaller`, `AnswerFile` | `DriverInstaller` items appear only when spec 005 hands Specialize work to Prepare setup. |
| `StepItemOutcome` | `Succeeded`, `Failed`, `Queued`, `NotRun` | `Queued`: staged for Windows setup. `NotRun`: skipped because Fail the deployment stopped the step. Spec 005 may add values for its own step. |
| `SetupProfileHistoryKind` | `Created`, `Renamed`, `TargetingChanged`, `LocationDeleted`, `VersionSaved`, `VersionRestored`, `Duplicated`, `Deleted` | Rows of kinds `Created`, `Renamed`, `TargetingChanged`, `LocationDeleted` and `Deleted` are shown in the History Targeting tab; version kinds are shown through the Versions tab. |

---

## 2. Setup Profile (table `SetupProfiles`, PK `profile`, RK profileId)

| Field | Type | Rules |
|---|---|---|
| `profileId` | Guid | Server-assigned. |
| `name` | string | 1 to 100 characters after trim; unique among non-deleted profiles, case-insensitive. |
| `currentVersion` | int | 1 or higher. Points at a row in `SetupProfileVersions`. |
| `failureHandling` | `SetupFailureHandling` | Copy of the current version's value, for the list page. |
| `registryValueCount`, `scriptCount` | int | Copies from the current version, for the list page. |
| `hasAnswerFile` | bool | Copy from the current version. |
| `createdAt`, `createdBy` | DateTimeOffset, string | UPN. |
| `lastModifiedAt`, `lastModifiedBy` | DateTimeOffset, string | Any change: content, name or targeting. |
| `deletedAt`, `deletedBy` | DateTimeOffset?, string? | Tombstone. Deleted profiles are hidden from every list and cannot be targeted, pinned or edited. |
| ETag | (Table) | Optimistic concurrency for every write. |

**Targeting** is not stored here. It is read from `SetupProfileTargets` and returned as:

| Field | Type | Rules |
|---|---|---|
| `targeting.allDevices` | bool | True when the `all-devices` row points at this profile. |
| `targeting.locationIds` | Guid[] | Empty when `allDevices` is true; at least one otherwise (FR-002). |

**State**: Active (normal), Deleted (tombstone). No other states; a profile always has a current
version and always targets something.

---

## 3. Targeting index (table `SetupProfileTargets`, PK `target`)

| RK | Field | Meaning |
|---|---|---|
| locationId (`D` format) | `profileId` | The profile that targets this location. At most one row per location (FR-003). |
| `all-devices` | `profileId` | The All devices profile. At most one (FR-003). |

Rules:
- A save that adds a target which already belongs to another profile fails with 409 naming that
  profile; nothing is changed.
- A targeting change is applied as one entity group transaction (adds and deletes), chunked with
  compensation above 100 operations.
- Switching a profile from Selected locations to All devices deletes its location rows and adds
  `all-devices` in the same transaction, and the reverse.
- Resolution for a session (R2): row for `session.locationId`, else `all-devices`, else none.

---

## 4. Profile Version

### 4.1 Version index (table `SetupProfileVersions`, PK profileId, RK version `D6`)

| Field | Type | Rules |
|---|---|---|
| `version` | int | Sequential per profile, starting at 1, never reused. |
| `createdAt`, `createdBy` | DateTimeOffset, string | Author of the save or restore. |
| `blobName` | string | `{profileId}/{version:D6}.json` in container `setup-profiles`. |
| `contentSha256` | string | Lowercase hex SHA-256 of the blob bytes, computed before upload. |
| `contentSizeBytes` | long | Blob length. |
| `restoredFrom` | int? | Set when this version was created by Restore. |
| `duplicatedFrom` | object? | `{ profileId, profileName, version }` on version 1 of a duplicated profile. |
| `changeSummary` | object | See 4.3. Computed by Core against the previous version. |

Derived at read time (not stored): `isCurrent`, `inUseCount` (sessions in the `active` partition of
`DeviceSessions` whose `setupProfileId` and `setupProfileVersion` match).

### 4.2 Version document (blob, immutable)

```json
{
  "schemaVersion": 1,
  "profileId": "8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10",
  "version": 8,
  "failureHandling": "CompleteWithWarnings",
  "registryValues": [
    {
      "id": "6a1e...", "name": "Stop automatic device encryption until Intune sets the cipher",
      "hive": "System", "key": "CurrentControlSet\\Control\\BitLocker",
      "valueName": "PreventDeviceEncryption", "type": "DWord", "data": "1"
    }
  ],
  "scripts": [
    {
      "id": "c2d4...", "name": "Enable Wake on LAN in HP firmware", "runs": "WinPE",
      "order": 1, "timeoutMinutes": 5, "content": "...", "sha256": "...",
      "lastModifiedAt": "2026-10-23T09:12:00Z", "lastModifiedBy": "ops@contoso.com"
    }
  ],
  "answerFile": {
    "content": "<?xml version=\"1.0\" encoding=\"utf-8\"?>...",
    "passes": ["specialize", "oobeSystem"], "sizeBytes": 2311, "sha256": "...",
    "lastModifiedAt": "2026-10-23T09:12:00Z", "lastModifiedBy": "ops@contoso.com"
  }
}
```

**Registry Value** rules (FR-008):

| Field | Rules |
|---|---|
| `id` | Guid, stable across versions (drives change summaries). |
| `name` | 1 to 100 characters. States why the value is set. |
| `hive` | `SetupRegistryHive`. |
| `key` | 1 to 512 characters; relative to the hive root (no `HKLM\`, `HKEY_`, leading or trailing backslash, empty component); each component at most 255 characters. Under `System`, a leading `CurrentControlSet\` is allowed and mapped offline (research R5). |
| `valueName` | 0 to 255 characters; empty means the key's default value. |
| `type` | `SetupRegistryValueType`. |
| `data` | String and ExpandString: at most 4,096 characters. MultiString: lines separated by `\n`, no empty line, at most 4,096 characters total. DWord: decimal `0` to `4294967295` or `0x` hex up to 8 digits. QWord: decimal `0` to `18446744073709551615` or `0x` hex up to 16 digits. |
| Profile-level | At most 200 values; `(hive, key, valueName)` unique, case-insensitive. |

**Script** rules (FR-011):

| Field | Rules |
|---|---|
| `id` | Guid, stable across versions. |
| `name` | 1 to 100 characters; unique within the profile. |
| `runs` | `SetupScriptPhase`. |
| `order` | 1-based within its phase, contiguous. Moving a script changes `order` only. |
| `timeoutMinutes` | 1 to 30; default 5. |
| `content` | Non-empty; at most 65,536 bytes of UTF-8. |
| `sha256` | Lowercase hex of the UTF-8 content (without BOM), computed by Core. |
| `lastModifiedAt`, `lastModifiedBy` | Carried over unchanged when the script did not change; set to the save's time and author otherwise. |
| Profile-level | At most 20 scripts. |

**Answer File** rules (FR-018 to FR-020): `content` at most 262,144 bytes of UTF-8 and valid
under [contracts/answer-file-rules.md](contracts/answer-file-rules.md). `passes` is computed by
Core (distinct `settings/@pass` values in document order). `answerFile` is null when the profile has none.

**Version document** limits: the largest valid document is about 2 MB, which sets the 4 MB body
limit on the portal router (JSON escaping headroom).

### 4.3 Change summary (stored on the version row)

```json
{
  "registryValues": { "added": 1, "removed": 0, "changed": 2 },
  "scripts": { "added": 0, "removed": 1, "changed": 1, "reordered": true },
  "answerFile": "Changed",
  "failureHandling": { "from": "CompleteWithWarnings", "to": "FailDeployment" },
  "names": {
    "registryValues": ["Turn on automatic time zone"],
    "scripts": ["Wired 802.1X for the office LAN"]
  },
  "total": 5
}
```

- `answerFile`: `NoChange`, `Added`, `Removed`, `Changed`.
- `failureHandling`: null when unchanged.
- `names`: up to 10 names per section of the items that were added, removed or changed, for the
  expanded row's labelled fields.
- `total`: the "Changes" count column (added + removed + changed across sections, plus 1 for a
  reorder, an answer file change and a failure handling change each).
- Version 1: everything counts as added. A restore: computed against the version it replaced.

---

## 5. Profile history (table `SetupProfileHistory`, PK profileId, RK `{inverted ticks:D19}-{guid:N}`)

| Field | Type | Notes |
|---|---|---|
| `kind` | `SetupProfileHistoryKind` | |
| `at` | DateTimeOffset | |
| `by`, `byObjectId` | string, string? | UPN and Entra object id from the portal token. |
| `change` | string | Ready-to-show sentence, for example "Added Gothenburg Office", "Removed Stockholm HQ because the location was deleted", "Targeting changed from Selected locations to All devices", "Renamed from Stockholm to Stockholm and Gothenburg". |
| `addedLocationIds`, `removedLocationIds` | Guid[] | For `TargetingChanged` and `LocationDeleted`. |
| `version` | int? | For version kinds. |

Rows are never pruned by version retention and survive profile deletion (audit). Inverted ticks
make a partition query return newest first.

---

## 6. Retention setting (existing `PortalConfiguration`)

| Field | Type | Rules |
|---|---|---|
| `setupProfileVersionRetention` | int | Default 25. 5 to 100. Administrator-only through `PUT /api/portal-config`. |
| `setupProfileVersionRetentionChangedAt`, `...ChangedBy` | DateTimeOffset?, string? | Set by Core when the value changes; `changedBy` comes from the portal token. |

Applied at each profile's next save: keep the newest `setupProfileVersionRetention` versions plus
any version with `inUseCount > 0`; delete the rest (blob, then row).

---

## 7. Session Setup Assignment (fields added to `DeviceSession`)

| Field | Type | Set when | Notes |
|---|---|---|---|
| `setupProfileId` | Guid? | Assignment (SessionAssigned to SessionStarted) | Null when no profile resolved. |
| `setupProfileVersion` | int? | Assignment | |
| `setupProfileName` | string? | Assignment | Denormalized so the timeline still names a deleted profile. |

Stored as columns on the `DeviceSessions` entity. Never changed after assignment. The portal
session summary exposes `setupProfile: { profileId, name, version } | null`. The device status
response does not change.

---

## 8. Step outcomes

### 8.1 `ImagingStep` (existing contract and `ImagingSteps` row) gains

| Field | Type | Notes |
|---|---|---|
| `warningCount` | int? | Null for steps that do not report items. Completed plus `warningCount > 0` is "Completed with warnings". |
| `itemCounts` | `{ kind, outcome, count }[]` | Computed by Core from the reported items; stored as JSON on the step row (small). |
| `itemsTruncated` | bool | True when the Client reported more than 500 items and Core kept the first 500. |

### 8.2 Step item (table `ImagingStepItems`, PK sessionId, RK `{StepName}-{index:D4}`)

| Field | Type | Limit (Core truncates, never rejects) |
|---|---|---|
| `index` | int | Position reported by the Client (failures are not reordered on the device; the portal sorts). |
| `kind` | `StepItemKind` | |
| `name` | string | 100 characters. Registry value name, script name, "Answer file", or the driver component name. |
| `target` | string? | 300 characters. Registry: full path with value name, for example `HKLM\SOFTWARE\Policies\Microsoft\FVE\EncryptionMethodWithXtsOs`. |
| `outcome` | `StepItemOutcome` | |
| `reason` | string? | 500 characters. Required when `outcome` is `Failed` or `NotRun`. |
| `exitCode` | int? | Scripts. |
| `durationMs` | long? | Scripts. |
| `lastOutputLine` | string? | 200 characters. In WinPE scripts. |

At most 500 items per report; a longer list is cut at 500 and the step gets `itemsTruncated: true`.
A new final report for the same step replaces its rows (delete then insert). Rows are deleted when
the session is purged.

---

## 9. Device-side entities (not persisted in Azure)

| Entity | Where | Defined in |
|---|---|---|
| Setup profile descriptor | Device Gateway response | [contracts/device-gateway.md](contracts/device-gateway.md) |
| Prepared setup work | Client memory: parsed version document plus driver Specialize items handed over by Install drivers (spec 005) | [contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md) |
| Setup Manifest | `<W>:\Windows\Setup\CloudImaging\manifest.json` | [contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md) |
| Device answer file | `<W>:\Windows\Panther\unattend.xml` | [contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md) |
| Setup runner log | `%WINDIR%\Logs\CloudImaging\SetupRunner.log` | [contracts/setup-runner-and-unattend.md](contracts/setup-runner-and-unattend.md) |

---

## 10. Relationships

```text
Location (catalog) 0..1 <--- SetupProfileTargets row ---> 1 Setup Profile
Setup Profile 1 ---> 1..N Profile Version (index row + immutable blob)
Setup Profile 1 ---> 0..N History rows (kept after deletion)
DeviceSession 0..1 ---> 1 Profile Version (pin: profileId + version, set at assignment)
DeviceSession 1 ---> 0..N ImagingStep ---> 0..N ImagingStepItem
PortalConfiguration 1 ---> retention count applied to every profile at its next save
```

# Contract: Operator API and Imaging Core API

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) | **Callers**: [portal-server-routes.md](portal-server-routes.md), [device-gateway.md](device-gateway.md)

The Operator API is a thin proxy (pattern of `OperatorApi/Functions/SetupProfileFunctions.cs`):
validates route parameters, forwards the body unchanged to Core through
`OperatorApi/Services/ImagingCoreClient.cs`, relays status and body. All rules live in Core. New
functions require `CloudImaging.PortalAccess` (not on the Media Builder allowlist) and get entries
in `OperatorApi/OpenApi/OpenApiConfig.cs`. Each Operator route `/api/drivers/...` maps to Core
`/api/internal/drivers/...` with the same method and remaining path.

Core functions live in `ImagingCoreApi/Functions/DriverFunctions.cs` (portal-facing),
`DriverSyncFunctions.cs` (sync settings, runs, the `driver-work` queue triggers) and `SessionDriverSetFunction.cs` (device).
The two Media Builder endpoints for preloading driver sets on a USB drive are in
[media-builder-driver-sets.md](media-builder-driver-sets.md) section 1.

## 1. Shapes

```jsonc
// Model row (GET models)
{ "modelId": "guid", "vendor": "Hp", "displayName": "HP EliteBook 840 G11",
  "matchKeys": ["8C26"], "releases": ["Windows11_24H2"], "state": "Active",
  "included": 31, "skipped": 9, "maxDownloadBytes": 2410000000,
  "status": { "waitingForApproval": 1, "failedItems": 0, "syncOff": false,
              "downloading": { "done": 12, "total": 33 } } }

// Component (GET models/{id}?release=)
{ "lineageKey": "sp140001", "componentId": "sp150123", "name": "...", "category": "Driver - Network",
  "version": "23.80.0.4", "lastUpdated": "2026-10-20", "action": "Offline",
  "allowedActions": ["Offline", "Skip"], "source": "Rule", "ruleName": "Skip HP Services Scan",
  "state": "Active", "updatedByLastSync": true,
  "rollBack": null,          // { version, until, skippedVersion, changedBy, changedAt } when rolled back
  "custom": null,            // { customId, replacesName, uploadedBy } for a custom component
  "replacedBy": null,        // { customId, name } when a custom component replaces this one
  "details": { /* vendor-specific labelled fields, see portal-client-surfaces.md */ },
  "descriptionUrl": "https://..." }
```

Model page response also carries `state` (`Draft` or `Active`), `checkAvailableAt` (when Check for
updates is available again, or null), `checkBlockedReason` (`Draft`, `SyncOff`, `Running` or null), `setVersion`, `builtAt` (shown on the Storage tab, not in the
header), `hasPreviousSet`, `pendingVersion`, `pending` (`{ changes, buildAt }` while edits wait),
`preparing` (`{ setVersion, done, total }`), `notices` (custom component banners: `customId`,
`name`, vendor component name, new `componentId`, `version`, `releasedAt`), tiles (`offline`,
`offlineAndSpecialize`, `specialize`, `skip` counts, `downloadPerDeviceBytes`).

## 2. Endpoints (Core, `internal/drivers/...`)

| Function | Route | Rules and errors |
|---|---|---|
| `GetDriverVendors` | `GET vendors` | Vendors with sync flag, onboarded model count, match key type, `supported`. |
| `SetDriverVendor` | `PUT vendors/{vendor}` | 400 for Lenovo; records change log. |
| `GetDriverCatalogModels` | `GET catalog/models?vendor=&search=` | Returns `{ fetchedAt, catalogUrl, stale, warning?, models[] }`. When the vendor's stored list is missing or older than 7 days, fetches it first, whatever the vendor's sync switch (not a run, no limit). If that fetch fails and an older list is stored: 200 with `stale: true` and `warning` "Showing the model list from 2026-10-14 02:00. Can't reach catalog at https://hpia.hpcloud.hp.com.". If it fails and no list is stored: 503 `{ detail: "Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again.", catalogUrl }`. |
| `PreviewDriverModels` | `POST models/preview` | At most 20 models. Downloads component catalogs (24-hour cache) and resolves with current rules. 502 per model that could not be fetched, listed in `errors`. |
| `OnboardDriverModels` | `POST models` | Reserves match keys (409 naming the owning model) and creates each model as `Draft` with its components resolved from the preview's catalog data. Nothing is downloaded and no set is built. Returns `{ modelIds }` in request order; the portal opens the first. |
| `CompleteDriverModelOnboarding` | `POST models/{id}/complete` | 409 when the model is not `Draft`. Sets `Active`, turns the vendor's sync on, enqueues `ResolveModel` per release (downloads start at once). |
| `CheckDriverModelUpdates` | `POST models/{id}/check` | 202 with `{ runId }`: a `ModelCheck` run that reads only this model's vendor catalog. 409 `{ detail, availableAt }` while any run is active (one run at a time); 409 `{ detail }` for a draft ("Complete onboarding first."); 409 `{ detail }` when the model's vendor has Sync off ("Sync is off for HP."); 429 `{ detail, availableAt }` within 15 minutes of the model's last check; `Retry-After` header. Does not count for the 60-minute all-models limit. |
| `GetDriverModels` | `GET models` | |
| `GetDriverModel` | `GET models/{id}?release=` | 404 unknown model or release. |
| `AddDriverModelRelease` | `POST models/{id}/releases` | 400 when the catalog model does not list it. |
| `RemoveDriverModelRelease` | `DELETE models/{id}/releases/{release}?changedBy=` | 409 when it is the last release ("Remove the model instead."). |
| `GetDriverRemovalImpact` | `GET models/{id}/removal-impact?release=` | `freedBytes` (files no other model references), `sets`, `overrides`, `runningSessions`. |
| `RemoveDriverModel` | `DELETE models/{id}?changedBy=` | Immediate for new assignments. |
| `SetDriverOverride` / `ClearDriverOverride` | `PUT`/`DELETE models/{id}/releases/{release}/overrides/{key}` | 400 when the action is not allowed; marks dirty. |
| `ApproveDriverComponents` | `POST .../approvals` | Only `WaitingForApproval` keys; marks dirty. |
| `RemoveWithdrawnDriverComponent` | `DELETE .../components/{key}` | 409 unless `Withdrawn`. |
| `RollBackDriverSet` | `POST .../rollback` | 409 without a previous set. |
| `GetDriverRules`, `CreateDriverRule`, `UpdateDriverRule`, `DeleteDriverRule` | `rules`, `rules/{id}` | 400 field errors (name, value, pattern); 409 duplicate name; every change marks all pairs of the rule's scope dirty. |
| `GetDriverRuleSuggestions` | `GET rules/suggestions` | |
| `GetDriverCategories`, `SetDriverCriticalCategories` | `categories`, `critical-categories/{vendor}` | |
| `GetDriverSyncSettings`, `SetDriverSyncSettings` | `sync/settings` | 400 field errors ("Choose a time zone from the list.", "Choose at least one day.", "Choose a repeat from the list." for `repeatWeeks` outside 1 to 4). Both return `nextRunAt` (UTC, null for `Manual`) computed from the stored schedule, and `syncNowAvailableAt` (UTC, null when available now): the later of the end of the active run, if any, and 60 minutes after the last `Scheduled`, `Manual` or `Check` start. The get also returns `timeZones`: `[{ ianaId, displayName, covers }]` from the stored list in its Windows order, only entries the host resolves; the set accepts only an `ianaId` from it. |
| `StartDriverSyncRun` | `POST sync/runs` | Sync now: a `Manual` run that refreshes the model lists and checks every onboarded model. 202; 409 `{ detail, availableAt }` while running; 429 `{ detail, availableAt }` within 60 minutes of the last `Scheduled`, `Manual` or `Check` start; `Retry-After` header. |
| `CheckAllDriverModelUpdates` | `POST models/check` | Check for updates on the Models tab: a `Check` run that reads the component catalog of every onboarded model of every vendor that is on, without refreshing the model lists. Same 409 and 429 rules as `StartDriverSyncRun` (one shared limit). Drafts are refreshed without downloads. |
| `GetDriverSyncRuns`, `GetDriverSyncRunItems` | `sync/runs`, `sync/runs/{id}/items` | 90 days. |
| `GetDriverStorage` | `GET storage` | Total bytes counted once per file, per vendor, and per model `sizeBytes` (each file once) and `sets` (`{ current, rollBack }` counts); `previousVersionsToKeep` and `previousVersionsBytes` (bytes of the files kept only because of this setting, each file once; a file that any other reference also keeps is not counted). |
| `SetDriverStorageSettings` | `PUT storage/settings` `{ previousVersionsToKeep }` | 200 with the `GetDriverStorage` body; 400 "Choose a number from the list." outside 0 to 3. Downloads nothing; a lower number frees files at the next clean-up (the end of the next scheduled sync, Sync now or Models tab check). Writes a `Storage` change log row. |
| `GetDriverStorageModel` | `GET storage/models/{id}` | Per release: current and roll-back set (version, `builtAt`), component count and bytes, and per stored component: `lineageKey`, `componentId`, `name`, `version`, `sizeBytes`, `sets` (versions using it, `next`, or `previous` for a kept previous version), `sharedWithModels`, `status` (`Stored`, `Downloading`, `Failed` with `reason`), `downloadedAt`. |
| `GetDriverDownloads` | `GET downloads` | `inProgress`: per set being prepared, model, release, set version, `components`, `componentsDone`, `bytes`, `bytesDone`, `status` (`Downloading`, `Waiting`, `Failed` with `failed` count and reasons), `startedAt`. `finished`: builds ready in the last 7 days with `newComponents`, `reusedComponents`, `sizeBytes`, `readyAt`. |
| `BuildDriverSetNow` | `POST models/{id}/releases/{release}/build` | Clears the 2-minute wait for a dirty pair; 409 when nothing is waiting. |
| `GetDriverComponentVersions` | `GET models/{id}/releases/{release}/components/{key}/versions[?check=true]` | Version history rows (data-model 3.14) with `status`: `Stored`, `DownloadsOnRollBack`, or `Unavailable` (`Available` false at the last check), and `sizeBytes`. With `check=true` (Roll back component opening) Core first sends a HEAD to the recorded URL of every version that is not stored, through the host allowlist, 10 seconds each and in parallel, and records `Available` and `AvailabilityCheckedAt`; a timeout counts as unavailable. |
| `SetDriverComponentRollBack` | `PUT .../components/{key}/rollback` `{ version, until }` | 400 when the version is not in the history or is the current one; queues a download when not stored; 409 `{ detail }` when it cannot be downloaded or verified again (the row is not written). Marks the pair dirty. |
| `ClearDriverComponentRollBack` | `DELETE .../components/{key}/rollback?changedBy=` | 404 when not rolled back. Marks the pair dirty. |
| `StartCustomComponentUpload` | `POST custom-components/uploads/start` `{ fileName, sizeBytes }` | 400 over 5 GB or not `.zip`, `.exe`, `.msi`. Returns `uploadId` and a write SAS for a staging blob, like the OS image upload. `POST custom-components/uploads/{id}/complete` commits the block list, streams the blob once to compute the SHA-256 and count `.inf` entries in the ZIP central directory, and returns `{ sha256, sizeBytes, infCount }` (404 for an unknown upload); `.../abandon` deletes the staging blob (204). |
| `CreateCustomComponent` | `POST custom-components` `{ uploadId, name, version, action, category, installCommand?, targets: [{ modelId, replacesLineageKey? }] }` | 400 field errors, Offline actions without INF, Specialize without command; 409 when a lineage is already replaced on a model (names the other custom component). Marks every target pair dirty. |
| `GetCustomComponents`, `UpdateCustomComponent`, `DeleteCustomComponent` | `custom-components`, `custom-components/{id}` | Update covers name, action, category, command and targets; a new package is a new upload with a new version. Delete marks targets dirty; running deployments keep their pinned files. |
| `DismissCustomComponentNotice` | `POST custom-components/{id}/notices/{modelId}/dismiss` | Records who dismissed the banner for the current `NoticeVersion`. |
| `PreviewDriverAssignment` | `POST assignment-preview` `{ osImageId, sessionIds[] }` | Read-only, no actor. At most 200 session IDs. 404 unknown image. Response: `{ release, architecture, options: [{ modelId, vendor, displayName, maxDownloadBytes }], sessions: [{ sessionId, matchedModelId, skipReason, matchKey }] }`. `options` are the models with a current set for the image's release and architecture, ordered by vendor then name; empty `options` with `release: null` when the image has none. Sessions that are not found or not assignable are left out. |

Every write requires `actor.upn` (or `changedBy`) and writes a `DriverChangeLog` row; 400 "The
acting user is missing." otherwise. `PreviewDriverAssignment` is a read and is proxied by the
Operator API like the other driver routes.

## 3. Background work (Core, queue-triggered)

There is no polling timer. All work is a message on the `driver-work` queue (data-model 3.12,
research R1).

| Function | Trigger | Work |
|---|---|---|
| `DriverWorkProcessor` | Queue `driver-work` (`AzureWebJobsStorage`) | Runs one message by `kind`: `ScheduledSync` claims its slot with the `LastScheduledSlotUtc` ETag when no run is active, starts a `Scheduled` run and enqueues the next slot (other copies are dropped); `FetchVendorCatalog`, `ResolveModel`, `DownloadFile`, `BuildSet` (re-enqueues itself while `BuildAfter` is in the future) and `FinalizeRun`. |
| `DriverWorkPoison` | Queue `driver-work-poison` | Records the message's file or run item as failed with the reason and decrements the run's `Outstanding`. |

`host.json` `extensions.queues`: `batchSize` 4, `newBatchThreshold` 2, `maxDequeueCount` 3,
`maxPollingInterval` `00:00:10`. Core creates both queues at start and enqueues one
`ScheduledSync` for the next slot; `SetDriverSyncSettings` does the same after saving.

## 4. Session pin (inside existing assignment)

`AssignSessionFunction` and `BulkAssignmentService` call `DriverSetResolver.Resolve(session,
osImage)` after the setup profile pin and write the `DeviceSession` driver fields
(data-model section 2). Resolution: image must have a `WindowsRelease` (else `NoWindowsRelease`);
HP: any `MotherboardModel` in a model's match keys, Dell: `SystemSku` (else `NoMatchingModel`);
the image's `Architecture` must equal the model's (else `ArchitectureMismatch`); release onboarded (else
`ReleaseNotOnboarded`); current set exists (else `NoDriverSet`). The device's match key is stored
in `DriverMatchKey` either way. When a set is pinned, the model vendor's critical categories are
copied to `DriverCriticalCategories`.

**Manual choice.** `POST internal/sessions/bulk-assign` and `POST internal/sessions/{id}/assign`
accept optional `driverChoices: [{ sessionId, kind: "Model" | "None", modelId }]` (bulk) or
`driverChoice: { kind, modelId }` (single), plus `actor: { upn, objectId }`, required when a choice
is present (400 "The acting user is missing."). Before assigning any session, Core checks each
choice for an assignable session: `Model` needs an existing model with the image's architecture
and a current set for the image's release. Any invalid choice: 409 `{ detail, errors: [{ sessionId,
reason }] }` and nothing is assigned ("The chosen driver model is no longer available for 2
devices. Choose again."). A choice for a session ID not in `sessionIds`, two choices for one
session, or `Model` without `modelId`: 400. Valid choices replace the match: `Model` pins that
model's current set; `None` pins `NoneChosen`; both set `DriverChosenManually = true` and
`DriverChosenBy = actor.upn`. The existing Operator API proxies (`AssignSessionFunction`,
`BulkAssignFunction`) forward the body unchanged.

## 5. Session descriptor (Core, called by the Gateway)

`GET internal/sessions/{id}/driver-set` and
`POST internal/sessions/{id}/driver-set/components/{index}/link`: see
[device-gateway.md](device-gateway.md). Core issues user-delegation read SAS for exactly the
component's blob (vendor file or custom package), 60-minute lifetime, and sets each component's
`critical` flag from the session's `DriverCriticalCategories`.

## 6. Progress items

`ReportProgressFunction` accepts `InstallDrivers` like `PrepareSetup`: final report with items
(at most 1500), `warningCount`, `statusNote`; driver items carry `componentVersion` and
`driverOrigin`. `OverallProgressCalculator` treats `InstallDrivers` as optional with weight 0.

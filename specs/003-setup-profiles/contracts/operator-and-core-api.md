# Contract: Operator API and Imaging Core API

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) | **Callers**: [portal-server-routes.md](portal-server-routes.md), [device-gateway.md](device-gateway.md)

The Operator API is a thin proxy (pattern of `OperatorApi/Functions/WifiNetworkFunctions.cs`):
it validates route parameters, forwards the body unchanged to the Imaging Core API through
`OperatorApi/Services/ImagingCoreClient.cs`, and relays status and body. All rules live in Core.
New Operator API functions are not on the Media Builder allowlist in
`AppRoleAuthorizationMiddleware`, so they require `CloudImaging.PortalAccess` (no middleware change).
Each new function gets an OpenAPI entry in `OperatorApi/OpenApi/OpenApiConfig.cs`.

---

## 1. Setup profile endpoints

| Operator API function | Operator API route | Core function | Core route |
|---|---|---|---|
| `GetSetupProfiles` | `GET setup-profiles` | `GetSetupProfiles` | `GET internal/setup-profiles` |
| `GetSetupProfile` | `GET setup-profiles/{id}` | `GetSetupProfile` | `GET internal/setup-profiles/{id}` |
| `CreateSetupProfile` | `POST setup-profiles` | `CreateSetupProfile` | `POST internal/setup-profiles` |
| `UpdateSetupProfile` | `PUT setup-profiles/{id}` | `UpdateSetupProfile` | `PUT internal/setup-profiles/{id}` |
| `DeleteSetupProfile` | `DELETE setup-profiles/{id}?changedBy=` | `DeleteSetupProfile` | `DELETE internal/setup-profiles/{id}?changedBy=` |
| `RestoreSetupProfileVersion` | `POST setup-profiles/{id}/restore` | `RestoreSetupProfileVersion` | `POST internal/setup-profiles/{id}/restore` |
| `GetSetupProfileVersions` | `GET setup-profiles/{id}/versions` | `GetSetupProfileVersions` | `GET internal/setup-profiles/{id}/versions` |
| `GetSetupProfileVersion` | `GET setup-profiles/{id}/versions/{version}` | `GetSetupProfileVersion` | `GET internal/setup-profiles/{id}/versions/{version}` |
| `GetSetupProfileHistory` | `GET setup-profiles/{id}/history` | `GetSetupProfileHistory` | `GET internal/setup-profiles/{id}/history` |
| `GetSessionStepItems` | `GET sessions/{id}/steps/{stepName}/items` | `GetSessionStepItems` | `GET internal/sessions/{id}/steps/{stepName}/items` |

Request and response bodies are those of [portal-server-routes.md](portal-server-routes.md), with
the actor added by the portal backend:

```json
{ "actor": { "upn": "ops@contoso.com", "objectId": "guid" } }
```

Core refuses a write without `actor.upn` with 400 ("The acting user is missing.").

### Core processing of a save (`PUT internal/setup-profiles/{id}`)

Steps run in this order so a failure leaves a consistent state:

1. Load the profile row (with ETag). 404 when missing or tombstoned. 409 when `baseVersion` is not
   `currentVersion`.
2. Validate name (length, uniqueness), targeting (non-empty, locations exist in the catalog),
   and, when `content` is present, every content rule and the answer file rules
   ([answer-file-rules.md](answer-file-rules.md)). Collect every problem; return them together.
3. Compute the content diff against the current version (stable item ids). Decide whether a new
   version is needed.
4. Apply the targeting diff to `SetupProfileTargets` in one entity group transaction. A conflict
   returns 409 naming the owning profile; nothing else has changed.
5. If a new version is needed: serialize the document, compute SHA-256 and size, upload the blob
   with `If-None-Match: *`, insert the version row with the change summary.
6. Replace the profile row with the ETag from step 1 (new current version, counts, failure
   handling, name, last modified). On ETag mismatch: undo step 4, leave the orphaned blob and row
   from step 5 for the next prune, return 409.
7. Write history rows (renamed, targeting changed, version saved) and the structured audit log event.
8. Prune versions beyond retention that no active session pins (R1). Prune failures are logged and
   retried at the next save; they never fail the request.

Create runs steps 2, 4 (adds only), 5 (version 1), then inserts the profile row (on failure undo
step 4) and writes history. Restore is a save whose content is the requested version's document.
Delete tombstones the profile row (ETag), removes its target rows in one transaction, writes
history; version blobs are swept later.

### Versions read

`inUseCount` per version comes from one query on `DeviceSessions` partition `active` filtered by
`SetupProfileId eq {id}` and grouped by `SetupProfileVersion`.

---

## 2. Session endpoints (changed)

### Assignment: `POST internal/sessions/{id}/assign` and `POST internal/sessions/bulk-assign`

No request or response change. Before writing the SessionStarted transition, Core calls
`SetupProfileResolver.ResolveAsync(session.LocationId)` (bulk: once per distinct location) and
sets `SetupProfileId`, `SetupProfileVersion`, `SetupProfileName` on the session (null when none).

### Session summary: `GET internal/sessions`, `GET internal/sessions/{id}`

`SessionSummary` gains:

```json
{
  "setupProfile": { "profileId": "guid", "name": "Stockholm and Gothenburg", "version": 8 },
  "steps": [
    {
      "stepName": "PrepareSetup", "status": "Completed", "stepProgressPercent": null,
      "startedAt": "...", "completedAt": "...", "errorDetail": null,
      "warningCount": 1,
      "itemCounts": [
        { "kind": "RegistryValue", "outcome": "Succeeded", "count": 3 },
        { "kind": "WinPEScript", "outcome": "Failed", "count": 1 },
        { "kind": "WindowsSetupScript", "outcome": "Queued", "count": 1 },
        { "kind": "AnswerFile", "outcome": "Succeeded", "count": 1 }
      ],
      "itemsTruncated": false
    }
  ]
}
```

`setupProfile` is null when nothing was pinned. Step fields are null or empty for steps without items.

### Step items: `GET internal/sessions/{id}/steps/{stepName}/items`

200 with the item list ([../data-model.md](../data-model.md) section 8.2), Failed first, then
NotRun, then reported order. 404 for an unknown session. An unknown step name is 400. A known step
without items returns `[]`.

### Device descriptor: `GET internal/sessions/{id}/setup-profile`

Called only by the Device Gateway (Private Link). Contract in [device-gateway.md](device-gateway.md).
Core:
1. 404 when the session does not exist; 409 when its state is not SessionStarted or SessionInProgress.
2. 204 when `SetupProfileId` is null.
3. Reads the version row (tombstoned profiles included), confirms the blob exists, issues a
   15-minute read SAS for that blob only, returns the descriptor. A missing version row or blob is
   500 with a logged error (it means retention or the sweep broke the pin guarantee).

### Progress: `POST internal/sessions/{id}/progress`

Body extended (all new fields optional; an older Client sends none):

```json
{
  "stepName": "PrepareSetup",
  "status": "Completed",
  "stepProgressPercent": null,
  "errorDetail": null,
  "warningCount": 1,
  "items": [
    { "kind": "WinPEScript", "name": "Enable Wake on LAN in HP firmware", "target": null,
      "outcome": "Failed", "reason": "Exited with code 1.", "exitCode": 1, "durationMs": 8123,
      "lastOutputLine": "BCU returned 13: setting not found" }
  ]
}
```

Core:
- Accepts `items` only with status Completed or Failed; ignores them otherwise.
- Truncates text fields to their limits and the list to 500 (`itemsTruncated`), never rejects for length.
- Replaces the step's previous items, computes `itemCounts`, stores `warningCount` and `itemCounts` on the step row.
- Completion uses `OverallProgressCalculator.IsPipelineComplete(steps)`: every required step
  Completed and no reported step InProgress or Failed. `PrepareSetup` has weight 0.

### Session purge

`SessionLifecycleTimerFunction` deletes a purged session's `ImagingStepItems` partition with its
steps, and runs the setup profile version sweep (versions of tombstoned profiles and pruned
versions that no active session pins).

---

## 3. Locations (changed)

`DELETE internal/locations/{id}?changedBy=<upn>`:

| Case | Result |
|---|---|
| No profile targets the location | 204; unchanged behaviour (Wi-Fi clean-up included). |
| A profile targets it and has other targets | 204; target row deleted, history row `LocationDeleted` on that profile with `changedBy`. |
| It is the only target of a profile | 409 problem, `detail` names the location and the profile; nothing deleted. |

Operator API `DeleteLocation` forwards `changedBy` unchanged.

---

## 4. Portal configuration (changed)

`GET/PUT internal/portal-configuration` (via Operator API `configuration`) carries
`setupProfileVersionRetention` (int, default 25). PUT validates 5 to 100 (400 "Keep between 5 and
100 versions per profile."), and when the value changes records
`setupProfileVersionRetentionChangedAt` and `...ChangedBy` from `changedBy` and logs the audit event.

---

## 5. Contract tests

- `tests/CloudImaging.OperatorApi.Tests/Contracts/SetupProfileContractTests.cs`: each function
  forwards method, route and body; relays 400 with `errors`, 404, 409; refuses a caller with only
  `CloudImaging.MediaBuilderAccess`.
- `tests/CloudImaging.ImagingCoreApi.Tests/Functions/SetupProfileFunctionsTests.cs` and
  `Integration/SetupProfileIntegrationTests.cs`: save ordering, target uniqueness under two
  concurrent saves, retention with pinned versions, restore, duplicate source recording, delete
  with running sessions, descriptor 204/200/409, step items, location delete cases.

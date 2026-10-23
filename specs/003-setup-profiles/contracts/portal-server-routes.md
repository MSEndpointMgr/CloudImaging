# Contract: Portal Backend Routes

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) | **Upstream**: [operator-and-core-api.md](operator-and-core-api.md)

Every route passes Entra authentication, then the role guard, then (where shown)
`requireGuidParams`. A refused request returns the existing 403 problem response; its `detail`
names exactly the roles in the guard (spec 004 research R2).

**Guards**:
- `requireTechnician` (new in `middleware/roleGuard.ts`) = `requireRole('CloudImaging.Technician', 'CloudImaging.Operator', 'CloudImaging.Administrator')`
- `requireOperator` (existing) = `requireRole('CloudImaging.Operator', 'CloudImaging.Administrator')`
- `requireAdmin` (existing) = `requireRole('CloudImaging.Administrator')`
- `PortalAccess` (existing) = `requireRole('CloudImaging.PortalAccess')`, implied by Administrator, Operator and Technician

**Actor**: every write takes the acting user from the token through `decidingUser(req)`
(`routes/autopilot.ts`) and forwards it as `actor: { upn, objectId }` in the body, or as
`?changedBy=<upn>` on DELETE (the existing Autopilot group tag pattern). Any `actor`,
`createdBy`, `changedBy` or `lastModifiedBy` in the browser's body is discarded.

**Body size**: `app.ts` mounts `express.json({ limit: '4mb' })` on `/api/setup-profiles` **before**
the global 1 MB parser (body-parser skips an already-parsed body). Other routes keep 1 MB.

---

## New router `routes/setup-profiles.ts` (mounted at `/api/setup-profiles`)

| # | Method and path | Guard | Upstream (Operator API) | Success |
|---|---|---|---|---|
| 1 | `GET /api/setup-profiles` | requireTechnician | `GET /api/setup-profiles` | 200 `SetupProfileSummary[]` |
| 2 | `GET /api/setup-profiles/:profileId` | requireTechnician, requireGuidParams | `GET /api/setup-profiles/{id}` | 200 `SetupProfileDetail` |
| 3 | `POST /api/setup-profiles` | requireOperator | `POST /api/setup-profiles` | 201 `SetupProfileDetail` |
| 4 | `PUT /api/setup-profiles/:profileId` | requireOperator, requireGuidParams | `PUT /api/setup-profiles/{id}` | 200 `SetupProfileDetail` |
| 5 | `DELETE /api/setup-profiles/:profileId` | requireOperator, requireGuidParams | `DELETE /api/setup-profiles/{id}?changedBy=` | 204 |
| 6 | `POST /api/setup-profiles/:profileId/restore` | requireOperator, requireGuidParams | `POST /api/setup-profiles/{id}/restore` | 200 `SetupProfileDetail` |
| 7 | `GET /api/setup-profiles/:profileId/versions` | requireTechnician, requireGuidParams | `GET /api/setup-profiles/{id}/versions` | 200 `SetupProfileVersionSummary[]` |
| 8 | `GET /api/setup-profiles/:profileId/versions/:version` | requireTechnician, requireGuidParams | `GET /api/setup-profiles/{id}/versions/{version}` | 200 `SetupProfileVersionDetail` |
| 9 | `GET /api/setup-profiles/:profileId/history` | requireTechnician, requireGuidParams | `GET /api/setup-profiles/{id}/history` | 200 `SetupProfileHistoryEntry[]` |

`:version` must be an integer from 1 to 999999, else 400 without calling upstream (checked after
the role guard, like `requireGuidParams`).

**Duplicate** has no route of its own. The editor opens `/setup-profiles/new` prefilled with the
source's current content, a proposed name ("<name> (copy)") and no targeting, and Save calls
route 3 with `duplicatedFrom`. Nothing is stored until the operator saves with targeting set, so a
stored profile always targets something (FR-002, FR-036).

## Changed routes

| # | Method and path | Guard (unchanged) | Change |
|---|---|---|---|
| 10 | `GET /api/sessions/:sessionId/steps/:stepName/items` | PortalAccess, requireGuidParams | **New** in `routes/sessions.ts`. `stepName` must be a known step name, else 400. Upstream `GET /api/sessions/{id}/steps/{stepName}/items`. 200 `ImagingStepItem[]`. |
| 11 | `DELETE /api/locations/:locationId` | requireOperator | Forwards `?changedBy=<upn>`. A 409 from upstream (only location of a profile) is relayed with its `detail`. |
| 12 | `PUT /api/portal-config` | requireAdmin | Body may carry `setupProfileVersionRetention`; the route adds `changedBy` from the token. |
| 13 | `GET /api/sessions`, `GET /api/sessions/:sessionId` | PortalAccess | Response shape gains `setupProfile` and step `warningCount`, `itemCounts`, `itemsTruncated` (pass-through, no code change in the route). |

Routes 1 to 10 are added to `tests/cloud-imaging-portal/server/role-route-matrix.test.ts`:

| Routes | Administrator | Operator | Technician | Reader | Approver | No role |
|---|---|---|---|---|---|---|
| 1, 2, 7, 8, 9 | allowed | allowed | allowed | 403 | 403 | 403 |
| 3, 4, 5, 6 | allowed | allowed | 403 | 403 | 403 | 403 |
| 10 | allowed | allowed | allowed | 403 | 403 | 403 |

---

## Payloads

### `SetupProfileSummary` (route 1)

```json
{
  "profileId": "guid",
  "name": "Stockholm and Gothenburg",
  "targeting": { "allDevices": false, "locationIds": ["guid", "guid"] },
  "registryValueCount": 3,
  "scriptCount": 2,
  "hasAnswerFile": true,
  "failureHandling": "CompleteWithWarnings",
  "currentVersion": 8,
  "lastModifiedAt": "2026-10-23T09:12:00Z",
  "lastModifiedBy": "ops@contoso.com"
}
```

The list is enough for the Start imaging overlay (resolve each coupled device's `locationId`
against `targeting`) and for the picker's disabled reasons (which other profile owns a location
or All devices).

### `SetupProfileDetail` (routes 2, 3, 4, 6)

`SetupProfileSummary` plus `createdAt`, `createdBy` and `content`: the version document of
[../data-model.md](../data-model.md) section 4.2 for `currentVersion`.

### Create (route 3) request

```json
{
  "name": "Stockholm and Gothenburg",
  "targeting": { "allDevices": false, "locationIds": ["guid"] },
  "duplicatedFrom": null,
  "content": {
    "failureHandling": "CompleteWithWarnings",
    "registryValues": [ { "id": null, "name": "...", "hive": "Software", "key": "...", "valueName": "...", "type": "DWord", "data": "7" } ],
    "scripts": [ { "id": null, "name": "...", "runs": "WinPE", "order": 1, "timeoutMinutes": 5, "content": "..." } ],
    "answerFile": { "content": "<unattend ...>" }
  }
}
```

- `id: null` means new; Core assigns ids, hashes, `passes`, sizes and per-item modified fields.
- `duplicatedFrom: { "profileId": "guid", "version": 7 }` records the source on version 1 and in
  history; Core checks the source exists (a deleted source is still recorded by name).

### Save (route 4) request

```json
{
  "baseVersion": 7,
  "name": "Stockholm and Gothenburg",
  "targeting": { "allDevices": false, "locationIds": ["guid", "guid"] },
  "content": { "failureHandling": "...", "registryValues": [], "scripts": [], "answerFile": null }
}
```

- `content` omitted: name and targeting only; no new version.
- `content` present and different from `baseVersion`'s content: new version `baseVersion + 1`.
- `content` present and identical: no new version.
- `baseVersion` not current: 409 (see errors).

### Restore (route 6) request

```json
{ "version": 5, "baseVersion": 7 }
```

Creates version 8 with version 5's content; targeting and name unchanged.

### `SetupProfileVersionSummary` (route 7)

```json
{
  "version": 7,
  "createdAt": "2026-10-22T14:03:00Z",
  "createdBy": "ops@contoso.com",
  "changeSummary": { "...": "see data model 4.3" },
  "restoredFrom": null,
  "duplicatedFrom": null,
  "isCurrent": false,
  "inUseCount": 3
}
```

Newest first, every retained version.

### `SetupProfileVersionDetail` (route 8)

`SetupProfileVersionSummary` plus `content` (the version document). Used by View (read-only editor).

### `SetupProfileHistoryEntry` (route 9)

```json
{ "kind": "TargetingChanged", "at": "2026-10-22T14:05:00Z", "by": "ops@contoso.com", "change": "Added Gothenburg Office" }
```

Kinds `Created`, `Renamed`, `TargetingChanged`, `LocationDeleted`, `Deleted`, newest first.

### `ImagingStepItem` (route 10)

As in [../data-model.md](../data-model.md) section 8.2, sorted by Core with `Failed` first, then
`NotRun`, then the reported order.

---

## Errors

Problem responses keep the existing shape. Upstream 4xx `detail` is relayed (truncated to 500
characters); the global error handler additionally relays an upstream `errors` array, at most 50
entries, each `message` at most 300 characters, and nothing else from the upstream body.

| Status | When | Example `detail` (user-facing, ASCII) |
|---|---|---|
| 400 | Field validation | "Script names must be unique. Rename "Set BIOS password policy" and save again." |
| 400 | Answer file rules | "The answer file has 2 problems." with `errors: [{ "code": "windowsPePass", "line": 4, "message": "..." }]` |
| 400 | Targeting empty | "Choose at least one location or All devices." |
| 404 | Unknown or deleted profile, unknown version | "This setup profile no longer exists." |
| 409 | Location already targeted | "Gothenburg Office is already targeted by "Gothenburg lab". Remove it there first." |
| 409 | All devices already used | "All devices is already used by "Default profile". Change that profile first." |
| 409 | Stale `baseVersion` or concurrent name/targeting change | "This profile was changed by ops2@contoso.com at 2026-10-23 10:41. Reload to see their changes." |
| 409 | Duplicate name | "A setup profile named "Stockholm" already exists. Choose another name." |
| 409 | Location delete refused (route 11) | "Stockholm HQ is the only location of setup profile "Stockholm". Add another location to the profile or delete the profile, then try again." |
| 413 | Body above 4 MB | Existing body-parser response. Not reachable with valid content. |

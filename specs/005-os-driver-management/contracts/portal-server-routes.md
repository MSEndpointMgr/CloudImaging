# Contract: Portal Backend Routes (`/api/drivers`)

**Feature**: [../spec.md](../spec.md) | **Upstream**: [operator-and-core-api.md](operator-and-core-api.md)

New router `src/cloud-imaging-portal/server/src/routes/drivers.ts`, mounted at `/api/drivers` in
`app.ts` after Entra authentication. Every route passes the role guard, then `requireGuidParams`
where a GUID is in the path. Writes take the acting user from the token through
`decidingUser(req)` and forward it as `actor: { upn, objectId }` (body) or `?changedBy=` (DELETE);
any actor field in the browser's body is discarded. Upstream errors (`detail`, `errors`,
`availableAt`) are relayed by the existing error handler. JSON body limit: the global 1 MB.

Guards: `requireTechnician` for reads, `requireOperator` for writes (Administrator implies both).
The one exception is route 3: reading a vendor's catalog model list is part of onboarding, so it is
Operator only.

| # | Method and path | Guard | Upstream (Operator API) | Success |
|---|---|---|---|---|
| 1 | `GET /vendors` | Technician | `GET /api/drivers/vendors` | 200 vendor rows |
| 2 | `PUT /vendors/:vendor` `{ syncEnabled }` | Operator | `PUT /api/drivers/vendors/{vendor}` | 200 |
| 3 | `GET /catalog/models?vendor=&search=` | Operator | `GET /api/drivers/catalog/models` | 200 catalog models, onboarded ones flagged |
| 4 | `POST /models/preview` `{ vendor, catalogModelKeys[], releases[] }` | Operator | `POST /api/drivers/models/preview` | 200 per model counts per action and size |
| 5 | `POST /models` `{ vendor, catalogModelKeys[], releases[] }` | Operator | `POST /api/drivers/models` | 201 `{ modelIds }` of the draft models |
| 6 | `GET /models` | Technician | `GET /api/drivers/models` | 200 model rows |
| 7 | `GET /models/:modelId?release=` | Technician | `GET /api/drivers/models/{id}?release=` | 200 model page (header, tiles, components) |
| 8 | `POST /models/:modelId/releases` `{ release }` | Operator | `POST .../releases` | 200 |
| 9 | `DELETE /models/:modelId/releases/:release` | Operator | `DELETE .../releases/{release}?changedBy=` | 204 |
| 10 | `GET /models/:modelId/removal-impact?release=` | Operator | `GET .../removal-impact` | 200 impact (freed bytes, sets, overrides, running sessions) |
| 11 | `DELETE /models/:modelId` | Operator | `DELETE /api/drivers/models/{id}?changedBy=` | 204 |
| 12 | `PUT /models/:modelId/releases/:release/overrides/:lineageKey` `{ action }` | Operator | `PUT .../overrides/{key}` | 200 updated component |
| 13 | `DELETE /models/:modelId/releases/:release/overrides/:lineageKey` | Operator | `DELETE .../overrides/{key}?changedBy=` | 200 updated component |
| 14 | `POST /models/:modelId/releases/:release/approvals` `{ lineageKeys[] }` | Operator | `POST .../approvals` | 200 |
| 15 | `DELETE /models/:modelId/releases/:release/components/:lineageKey` | Operator | `DELETE .../components/{key}?changedBy=` | 204 (withdrawn components only, else 409) |
| 16 | `POST /models/:modelId/releases/:release/rollback` | Operator | `POST .../rollback` | 200 (409 without a previous set) |
| 17 | `GET /rules` | Technician | `GET /api/drivers/rules` | 200 rules with hits |
| 18 | `GET /rules/suggestions` | Operator | `GET /api/drivers/rules/suggestions` | 200 |
| 19 | `POST /rules` | Operator | `POST /api/drivers/rules` | 201 |
| 20 | `PUT /rules/:ruleId` | Operator | `PUT /api/drivers/rules/{id}` | 200 |
| 21 | `DELETE /rules/:ruleId` | Operator | `DELETE /api/drivers/rules/{id}?changedBy=` | 204 |
| 22 | `GET /categories?vendor=` | Technician | `GET /api/drivers/categories` | 200 categories seen in components, with critical flag |
| 23 | `PUT /critical-categories/:vendor` `{ categories[] }` | Operator | `PUT /api/drivers/critical-categories/{vendor}` | 200 |
| 24 | `GET /sync/settings` | Technician | `GET /api/drivers/sync/settings` | 200 settings plus `nextRunAt` and `syncNowAvailableAt` |
| 25 | `PUT /sync/settings` | Operator | `PUT /api/drivers/sync/settings` | 200 |
| 26 | `POST /sync/runs` | Operator | `POST /api/drivers/sync/runs` | 202 run; 409 running or 429 too soon, both with `availableAt` |
| 27 | `GET /sync/runs` | Technician | `GET /api/drivers/sync/runs` | 200 runs of the last 90 days |
| 28 | `GET /sync/runs/:runId/items` | Technician | `GET /api/drivers/sync/runs/{id}/items` | 200 |
| 29 | `GET /storage` | Technician | `GET /api/drivers/storage` | 200 totals, per vendor, per model |
| 30 | `POST /assignment-preview` `{ osImageId, sessionIds[] }` | Technician | `POST /api/drivers/assignment-preview` | 200 options and per-session match (operator-and-core-api section 2) |
| 31 | `GET /storage/models/:modelId` | Technician | `GET /api/drivers/storage/models/{id}` | 200 per-release blocks and stored components |
| 32 | `GET /downloads` | Technician | `GET /api/drivers/downloads` | 200 in progress and finished in the last 7 days |
| 33 | `POST /models/:modelId/releases/:release/build` | Operator | `POST .../build` | 202; 409 when nothing is waiting |
| 34 | `GET /models/:modelId/releases/:release/components/:lineageKey/versions[?check=true]` | Technician | `GET .../versions` (passes `check=true` only for Operator and Administrator) | 200 version history with status |
| 35 | `PUT /models/:modelId/releases/:release/components/:lineageKey/rollback` `{ version, until }` | Operator | `PUT .../rollback` | 200 updated component; 400; 409 with the reason when the version cannot be fetched again |
| 36 | `DELETE /models/:modelId/releases/:release/components/:lineageKey/rollback` | Operator | `DELETE .../rollback?changedBy=` | 200 updated component |
| 37 | `POST /custom-components/uploads/start` `{ fileName, sizeBytes }` | Operator | `POST /api/drivers/custom-components/uploads/start` | 200 `{ uploadId, uploadUrl }` (staging SAS); 400 over 5 GB or not `.zip`, `.exe` or `.msi` |
| 37a | `POST /custom-components/uploads/:uploadId/complete` | Operator | `POST /api/drivers/custom-components/uploads/{id}/complete` | 200 `{ sha256, sizeBytes, infCount }`; 400 when `uploadId` is not a GUID; 404 unknown upload |
| 37b | `POST /custom-components/uploads/:uploadId/abandon` | Operator | `POST /api/drivers/custom-components/uploads/{id}/abandon` | 204; 400 when `uploadId` is not a GUID |
| 38 | `POST /custom-components` | Operator | `POST /api/drivers/custom-components` | 201; 400 field errors; 409 lineage already replaced |
| 39 | `GET /custom-components` | Technician | `GET /api/drivers/custom-components` | 200 |
| 40 | `PUT /custom-components/:customId` | Operator | `PUT .../custom-components/{id}` | 200 |
| 41 | `DELETE /custom-components/:customId` | Operator | `DELETE .../custom-components/{id}?changedBy=` | 204 |
| 42 | `POST /custom-components/:customId/notices/:modelId/dismiss` | Operator | `POST .../dismiss` | 204 |
| 43 | `POST /models/:modelId/complete` | Operator | `POST /api/drivers/models/{id}/complete` | 200 model page; 409 when not a draft |
| 44 | `POST /models/:modelId/check` | Operator | `POST /api/drivers/models/{id}/check` | 202 run; 409 for a draft, with the vendor's Sync off, or while any run is active, 429 too soon; 409 running and 429 carry `availableAt` |
| 45 | `POST /models/check` | Operator | `POST /api/drivers/models/check` | 202 run; 409 running or 429 too soon, both with `availableAt` (limit shared with route 26) |
| 46 | `PUT /storage/settings` `{ previousVersionsToKeep }` | Operator | `PUT /api/drivers/storage/settings` | 200 storage totals; 400 outside 0 to 3 |

Custom component uploads follow the OS image staged upload (`routes/images.ts` `upload/*`): the
browser stages blocks directly to Blob Storage through the SAS URL from route 37, and only JSON
passes through this server. Route 37a asks Core to commit the block list, compute the SHA-256 and
count `.inf` entries; route 37b deletes the staged blob. Both use `requireGuidParams('uploadId')`.

Path parameters: `:vendor` is `Hp`, `Dell` or `Lenovo` (else 400 without an upstream call);
`:release` is a `WindowsRelease` value (else 400); `:lineageKey` matches `^[A-Za-z0-9_.-]{1,80}$`
(else 400).

Existing route changed: `PATCH /api/images/:imageId` and the OS image upload start accept
`windowsRelease` (Operator, as today); the image list returns it.

Existing routes changed: `POST /api/sessions/bulk-assign` accepts optional `driverChoices: [{
sessionId, kind, modelId }]` and `POST /api/sessions/:sessionId/assign` an optional
`driverChoice: { kind, modelId }`. The guards stay as they are (Start imaging permission). The
server validates the shape (`kind` is `Model` or `None`, `modelId` a GUID when `Model`, session
IDs GUIDs, at most one choice per session; 400 without an upstream call), forwards only
`sessionId`, `kind` and `modelId`, and adds `actor` from the token through `decidingUser(req)`
when a choice is present (401 without an identity). The 409 with `errors` is relayed unchanged.
The session list and session detail return `driverModelName`, `driverSetVersion`,
`driverSkipReason`, `driverMatchKey`, `driverChosenManually` and `driverChosenBy`.

Session timeline: the existing `GET /api/sessions/:id/steps/:stepName/items` serves
`InstallDrivers` items unchanged.

All routes join `tests/cloud-imaging-portal/server/role-route-matrix.test.ts`: reads for
Technician, Operator, Administrator; writes for Operator, Administrator; Reader, Autopilot
Approver and no role get 403.

# Contract: Portal Backend Route Authorization

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md)

Every route under `/api` except `/api/health` and `/api/config` passes Entra authentication, then
the role guard below, then (where shown) `requireGuidParams`. A refused request returns the existing
problem response, unchanged in shape:

```json
{ "type": "https://cloudimaging.io/errors/forbidden", "title": "Forbidden", "status": 403,
  "detail": "This operation requires one of the following roles: CloudImaging.Operations, CloudImaging.Administrator." }
```

The `detail` lists exactly the roles in the guard, so guards name every accepted role (research R2).

**Legend**: Adm = Administrator, Ops = Operations, Tech = Technician, Rdr = Reader, Appr = AutopilotApprover,
PA = PortalAccess (implied by Adm, Ops, Tech; never in a user token).
"Accepted" columns list the single-role users accepted, after implication.

**Change types**:
- **Edit**: guard changes in the router file to accept Operations.
- **Narrow**: guard changes in the router file to stop accepting Technician (FR-015a).
- **New**: route added by this feature.
- **Implied**: guard unchanged; Operations accepted through the implication table.
- **Message**: Operations already accepted through implication; guard edited so the 403 detail names it.
- **None**: unchanged; Operations refused (FR-012).

---

## Summary

| Change type | Routes |
|---|---|
| Edit | 31 |
| Narrow | 1 |
| New | 1 |
| Message | 2 |
| Implied | 20 |
| None (Administrator-only) | 18 |
| **Total** | **73** (72 existing plus 1 new) |

---

## Sessions (`routes/sessions.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/sessions` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/sessions/:sessionId` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/sessions/:sessionId/logs` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/sessions/:sessionId/logs/:fileName/download-url` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| POST | `/api/sessions/couple` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| POST | `/api/sessions/:sessionId/assign` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| POST | `/api/sessions/bulk-assign` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| DELETE | `/api/sessions/:sessionId` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| POST | `/api/sessions/:sessionId/preflight-override` | Adm | Adm | Adm | None |

## OS images (`routes/images.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/images` | PA, Rdr | Adm, Tech, Rdr | Adm, Ops, Tech, Rdr | Implied |
| POST | `/api/images` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/images/upload/start` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/images/upload/:uploadId/publish` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/images/upload/:uploadId/abandon` | Ops, Adm | Adm | Adm, Ops | Edit |
| PATCH | `/api/images/:imageId` | Ops, Adm | Adm | Adm, Ops | Edit |
| DELETE | `/api/images/:imageId` | Ops, Adm | Adm | Adm, Ops | Edit |

## Boot images (`routes/boot-images.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/boot-images` | PA, Rdr | Adm, Tech, Rdr | Adm, Ops, Tech, Rdr | Implied |
| POST | `/api/boot-images/upload/start` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/boot-images/upload/:token/publish` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/boot-images/:id/promote` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/boot-images/:id/demote` | Ops, Adm | Adm | Adm, Ops | Edit |
| DELETE | `/api/boot-images/:id` | Ops, Adm | Adm | Adm, Ops | Edit |

## Recovery images (`routes/recovery-images.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/recovery-images` | PA, Rdr | Adm, Tech, Rdr | Adm, Ops, Tech, Rdr | Implied |
| POST | `/api/recovery-images/upload/start` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/recovery-images/upload/:uploadId/publish` | Ops, Adm | Adm | Adm, Ops | Edit |
| DELETE | `/api/recovery-images/:id` | Ops, Adm | Adm | Adm, Ops | Edit |

## Upload jobs (`routes/upload-jobs.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/upload-jobs/:uploadId` | Ops, Adm | Adm | Adm, Ops | Edit |

## Locations and regions (`routes/locations.ts`, `routes/regions.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/locations` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| POST | `/api/locations` | Ops, Adm | Adm | Adm, Ops | Edit |
| PUT | `/api/locations/:locationId` | Ops, Adm | Adm | Adm, Ops | Edit |
| DELETE | `/api/locations/:locationId` | Ops, Adm | Adm | Adm, Ops | Edit |
| PUT | `/api/locations/:locationId/wifi-networks` | Ops, Adm | Adm | Adm, Ops | Edit |
| GET | `/api/regions` | Ops, Adm | Adm | Adm, Ops | Edit |
| PUT | `/api/regions` | Ops, Adm | Adm | Adm, Ops | Edit |

## Wi-Fi networks (`routes/wifi-networks.ts`)

No response returns a password, for any role (FR-011b). The password-bearing Operator API route
remains `MediaBuilderAccess` only and is not proxied by the Portal.

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/wifi-networks` | Ops, Adm | Adm | Adm, Ops | Edit |
| POST | `/api/wifi-networks` | Ops, Adm | Adm | Adm, Ops | Edit |
| PUT | `/api/wifi-networks/order` | Ops, Adm | Adm | Adm, Ops | Edit |
| PUT | `/api/wifi-networks/:networkId` | Ops, Adm | Adm | Adm, Ops | Edit |
| DELETE | `/api/wifi-networks/:networkId` | Ops, Adm | Adm | Adm, Ops | Edit |

## Autopilot (`routes/autopilot.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/autopilot/registrations` | Appr, Adm, Ops, Tech | Appr, Adm, Tech | Appr, Adm, Ops, Tech | Message |
| GET | `/api/autopilot/registrations/:requestId` | Appr, Adm, Ops, Tech | Appr, Adm, Tech | Appr, Adm, Ops, Tech | Message |
| GET | `/api/autopilot/history` | Appr, Adm, Ops, Rdr | Appr, Adm, Rdr | Appr, Adm, Ops, Rdr | Edit |
| POST | `/api/autopilot/registrations/:requestId/approve` | Appr, Adm, Ops | Appr, Adm | Appr, Adm, Ops | Edit |
| POST | `/api/autopilot/registrations/:requestId/reject` | Appr, Adm, Ops | Appr, Adm | Appr, Adm, Ops | Edit |
| POST | `/api/autopilot/registrations/:requestId/retry` | Appr, Adm, Ops | Appr, Adm | Appr, Adm, Ops | Edit |
| GET | `/api/autopilot/group-tags` | Appr, Adm, Ops | Appr, Adm | Appr, Adm, Ops | Edit |
| POST | `/api/autopilot/group-tags` | Adm | Adm | Adm | None |
| PUT | `/api/autopilot/group-tags/:tagId` | Adm | Adm | Adm | None |
| DELETE | `/api/autopilot/group-tags/:tagId` | Adm | Adm | Adm | None |

The deciding user's identity is still taken from the token (`decidingUser`), so an Operations
decision is recorded the same way as an Approver's (FR-017).

## Reports data (`routes/session-history.ts`)

Reports read `GET /api/session-history`, the three catalog lists (Implied rows above) and
`GET /api/autopilot/history` (Edit row above, never accepted Technician).

The catalog lists keep `PA, Rdr`: the Technician OS Images, Boot Images and Recovery Images pages
and the Dashboard stats read them, so they are not Reports-only. `GET /api/session-history` is
Reports-only in the Portal UI except for one consumer: the Dashboard's Completed Sessions stat,
shown to Technicians, reads it today (`DashboardPage.tsx`, using only `finalState` and
`terminalAt`). Narrowing the route alone would break that stat, so the Dashboard moves to a new
read that returns only completion times (FR-015a).

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/session-history` | Rdr, Ops, Adm | Adm, Tech, Rdr | Adm, Ops, Rdr | Narrow |
| GET | `/api/session-history/completed-timestamps` | PA, Rdr | n/a | Adm, Ops, Tech, Rdr | New |

`GET /api/session-history/completed-timestamps` takes the same `from` and `to` query parameters,
reads the same Operator API data, and returns a JSON array of ISO 8601 `terminalAt` strings for
records whose final state is `SessionCompleted`. No serial number, model, location, pre-flight
result, image or error detail is returned. The Dashboard keeps its local-day trend bucketing on
these timestamps. No Operator API change. The route is registered before any parameterised path
in the router.

A Technician calling `GET /api/session-history` receives:

```json
{ "type": "https://cloudimaging.io/errors/forbidden", "title": "Forbidden", "status": 403,
  "detail": "This operation requires one of the following roles: CloudImaging.Reader, CloudImaging.Operations, CloudImaging.Administrator." }
```

## Branding (`routes/branding.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/branding` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/branding/logo/sas` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/branding/logo/content` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| GET | `/api/branding/portal-logo/content` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| PUT | `/api/branding` | Adm | Adm | Adm | None |
| PUT | `/api/branding/logo` | Adm | Adm | Adm | None |
| PUT | `/api/branding/portal-logo` | Adm | Adm | Adm | None |
| DELETE | `/api/branding/logo` | Adm | Adm | Adm | None |
| DELETE | `/api/branding/portal-logo` | Adm | Adm | Adm | None |

## Configuration, certificate, partitioning, pre-flight, update check

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/portal-config` | Adm | Adm | Adm | None |
| PUT | `/api/portal-config` | Adm | Adm | Adm | None |
| GET | `/api/cert/active` | Adm | Adm | Adm | None |
| POST | `/api/cert/generate` | Adm | Adm | Adm | None |
| POST | `/api/cert/rotate` | Adm | Adm | Adm | None |
| GET | `/api/partitioning-scheme` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| PUT | `/api/partitioning-scheme` | Adm | Adm | Adm | None |
| GET | `/api/preflight-overrides` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| DELETE | `/api/preflight-overrides` | Adm | Adm | Adm | None |
| GET | `/api/update-check` | Adm | Adm | Adm | None |
| POST | `/api/update-check/refresh` | Adm | Adm | Adm | None |

Abuse-control observability (spec 002 FR-014), which FR-012 keeps Administrator-only, has no Portal
route today (research R11), so this contract has no row for it and the route matrix does not test it.
Any future route for it uses the inline Administrator guard.

## User preferences (`routes/user-preferences.ts`)

| Method | Path | Guard after | Accepted before | Accepted after | Change |
|---|---|---|---|---|---|
| GET | `/api/user-preferences` | PA | Adm, Tech | Adm, Ops, Tech | Implied |
| PUT | `/api/user-preferences` | PA | Adm, Tech | Adm, Ops, Tech | Implied |

---

## Operator API (no behavioural change)

| Caller | Operator API role | Change |
|---|---|---|
| Portal backend managed identity | `CloudImaging.PortalAccess` (full allowlist) | None. Called only after the Portal backend's own guard passes. |
| Media Builder user | `CloudImaging.MediaBuilderAccess` (read allowlist) | None. Cannot distinguish Technician, Operations, Administrator. |

Doc comments corrected (FR-025): `ImageCatalogFunctions`, `BootImageLifecycleFunctions`,
`LocationFunctions`, `WifiNetworkFunctions`, `AutopilotFunctions`.

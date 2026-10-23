# Contract: Portal Frontend Surfaces

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) (section 4)

The frontend mirrors the backend contract in [portal-server-routes.md](portal-server-routes.md).
Every control shown must lead to an operation the server accepts (FR-022). Gates read capabilities
from `useAuth()`, computed by `capabilitiesFromRoles` in `client/src/lib/portalRoles.ts`.

**Legend**: Adm, Ops, Tech, Rdr, Appr as in the server contract. "Shown to" lists single-role users.

---

## Summary

| Surface kind | Gates changed | Files |
|---|---|---|
| Route guards (`App.tsx`) | 14 | 1 |
| Access denied gate (`ProtectedRoute.tsx`, via `hasPortalAccess`) | 1 | 0 (context only) |
| Navigation items (`Sidebar.tsx`) | 9 | 1 |
| Dashboard cards and stats fetch (`DashboardPage.tsx`) | 7 cards + 2 fetches | 1 |
| In-page controls | 4 pages | 3 (+ Autopilot page via context) |
| User-facing strings | 3 | 2 (`BootImagesPage.tsx`, `RecoveryImagesPage.tsx`) |
| Capability source (`portalRoles.ts` new, `authContext.tsx`) | n/a | 2 |
| Doc comments only (`ReportsPage.tsx`, `AutopilotRegistrationsPage.tsx`) | 0 | 2 |

Unchanged gates (Operations stays out): Branding and Configuration routes, nav items and Dashboard
cards; pre-flight approve and revoke on the Devices page; `UpdateAvailableBanner`; certificate
expiry toast on Configuration.

---

## Route guards (`App.tsx`)

| Path | Guard before | Shown to before | Guard after | Shown to after |
|---|---|---|---|---|
| `/` (HomeRoute) | `hasDashboardAccess` else redirect `/autopilot` | Adm, Tech, Rdr | same flag, expanded | Adm, Ops, Tech, Rdr |
| `/sessions` | `isAdministrator \|\| isTechnician` | Adm, Tech | `canUseImaging` | Adm, Ops, Tech |
| `/os-images` | same | Adm, Tech | `canUseImaging` | Adm, Ops, Tech |
| `/boot-images` | same | Adm, Tech | `canUseImaging` | Adm, Ops, Tech |
| `/recovery-images` | same | Adm, Tech | `canUseImaging` | Adm, Ops, Tech |
| `/autopilot` | `canViewAutopilot` | Adm, Tech, Appr | same flag, expanded | Adm, Ops, Tech, Appr |
| `/locations` | `RequireAdmin` | Adm | `canManageLocations` | Adm, Ops |
| `/wifi-networks` | `RequireAdmin` | Adm | `canManageLocations` | Adm, Ops |
| `/reports` | `isAdministrator \|\| isReader` | Adm, Rdr | `canViewReports` | Adm, Ops, Rdr |
| `/reports/device-outcomes` | same | Adm, Rdr | `canViewReports` | Adm, Ops, Rdr |
| `/reports/location-statistics` | same | Adm, Rdr | `canViewReports` | Adm, Ops, Rdr |
| `/reports/image-inventory` | same | Adm, Rdr | `canViewReports` | Adm, Ops, Rdr |
| `/reports/failures` | same | Adm, Rdr | `canViewReports` | Adm, Ops, Rdr |
| `/reports/autopilot-registrations` | `canViewAutopilotReport` | Adm, Rdr, Appr | same flag, expanded | Adm, Ops, Rdr, Appr |
| `/branding` | `RequireAdmin` | Adm | unchanged | Adm |
| `/configuration` | `RequireAdmin` | Adm | unchanged | Adm |

Refused routes redirect to `/` as today (FR-020).

## Access denied gate

| Gate | Before | After |
|---|---|---|
| `hasPortalAccess` | Adm, Tech, Rdr, Appr | Adm, Ops, Tech, Rdr, Appr |

## Navigation (`Sidebar.tsx`)

New access level `'manage'` for Locations and Wi-Fi Networks; `'admin'` keeps Branding and Configuration.

| Item | Access level before | Shown to before | Access level after | Shown to after |
|---|---|---|---|---|
| Dashboard | dashboard | Adm, Tech, Rdr | dashboard | Adm, Ops, Tech, Rdr |
| Devices | operations | Adm, Tech | imaging (`canUseImaging`) | Adm, Ops, Tech |
| OS Images | operations | Adm, Tech | imaging | Adm, Ops, Tech |
| Boot Images | operations | Adm, Tech | imaging | Adm, Ops, Tech |
| Recovery Images | operations | Adm, Tech | imaging | Adm, Ops, Tech |
| Autopilot | autopilot | Adm, Tech, Appr | autopilot | Adm, Ops, Tech, Appr |
| Reports | reports | Adm, Rdr | reports (`canViewReports`) | Adm, Ops, Rdr |
| Locations | admin | Adm | manage | Adm, Ops |
| Wi-Fi Networks | admin | Adm | manage | Adm, Ops |
| Branding | admin | Adm | admin | Adm |
| Configuration | admin | Adm | admin | Adm |

The existing access-level name `'operations'` is renamed (for example `'imaging'`) so it cannot be
confused with the Operations role.

## Dashboard (`DashboardPage.tsx`)

| Element | Shown to before | Shown to after |
|---|---|---|
| Stats fetch of sessions, images, boot images | Adm, Tech | Adm, Ops, Tech |
| Completed Sessions stat fetch | `/api/session-history`, Adm, Tech, Rdr | `/api/session-history/completed-timestamps`, Adm, Ops, Tech, Rdr |
| Cards: Devices, OS Images, Boot Images, Recovery Images | Adm, Tech | Adm, Ops, Tech |
| Card: Autopilot | Adm, Tech, Appr | Adm, Ops, Tech, Appr |
| Card: Reports | Adm, Rdr | Adm, Ops, Rdr |
| Card: Locations | Adm | Adm, Ops |
| Cards: Branding, Configuration | Adm | Adm |

The Completed Sessions stat reads completion timestamps only, so the Technician Dashboard is
unaffected when `GET /api/session-history` stops accepting Technician (FR-015a). The four Reports
pages that read session outcomes keep calling `GET /api/session-history`.

## In-page controls

| Page | Controls | Flag before | Flag after | Shown to after |
|---|---|---|---|---|
| OS Images | Upload, capacity warning, bulk select and delete, edit, delete, Actions column | `isAdministrator` | `canManageCatalog` | Adm, Ops |
| Boot Images | Upload, Actions column, promote, demote, delete | `isAdministrator` | `canManageCatalog` | Adm, Ops |
| Recovery Images | Upload, Actions column, delete | `isAdministrator` | `canManageCatalog` | Adm, Ops |
| Autopilot | Review, approve, reject, retry, change group tag; history link | `canDecideAutopilot`, `canViewAutopilotReport` | same flags, expanded | Decide: Adm, Ops, Appr. History: Adm, Ops, Rdr, Appr |
| Devices | Pre-flight approve and revoke, override Actions column | `isAdministrator` | unchanged | Adm |
| Locations, Wi-Fi Networks | All controls (page-level gate only) | route `RequireAdmin` | route `canManageLocations` | Adm, Ops |

Upload status polling (`uploadJobService.ts`) needs no gate of its own; it runs only from upload
dialogs that `canManageCatalog` already gates, against a route that accepts Operations.

## User-facing strings

| File | Before | After |
|---|---|---|
| `BootImagesPage.tsx` (demote impact text) | `Technicians can no longer prepare USB devices with it. Only Administrators see it in Media Builder. USB devices are not affected.` | `Technicians can no longer prepare USB devices with it. Only Operations and Administrator users see it in Media Builder. USB devices are not affected.` |
| `BootImagesPage.tsx` (upload dialog selected file) | `` `${file.name} \u00B7 ${fmtSize(file.size)}` `` (middle dot) | `` `${file.name} (${fmtSize(file.size)})` `` |
| `RecoveryImagesPage.tsx` (upload dialog selected file) | same middle dot label | `` `${file.name} (${fmtSize(file.size)})` `` |

The middle dot is written as its escape here so this contract stays free of the character (FR-040).
No client test asserts on the selected-file label.

Role names in refusals reach the user through the server's 403 detail (toasts use
`extractErrorDetail`), which names `CloudImaging.Operations, CloudImaging.Administrator` for
catalog, location and Wi-Fi writes (FR-023). Existing texts that say "an administrator" for
Autopilot settings and retention stay correct.

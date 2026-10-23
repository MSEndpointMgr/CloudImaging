# Research: Operator Role

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-22

All decisions below are grounded in the code as of this date. The spec has no open clarifications;
this document resolves the design questions the spec leaves to implementation.

---

## Current state (what the code does today)

| App | Where roles are read | Hierarchy today | How gates are expressed |
|---|---|---|---|
| Portal backend | `middleware/roleGuard.ts`, `getUserRoles(req)` reads the access token `roles` claim | `ROLE_IMPLICATIONS`: Administrator implies Technician and PortalAccess; Technician implies PortalAccess. One pass, so the table must list the full closure. | `requireRole(...roles)` per route, 72 routes in 17 routers, all guarded. 403 detail lists exactly the roles passed to `requireRole`. |
| Portal frontend | `context/authContext.tsx`, `rolesFromAccount` reads the ID token `roles` claim | None. `isTechnician` is the raw claim; consumers write `isAdministrator \|\| isTechnician` by hand. | Booleans on the auth context (`isAdministrator`, `canViewAutopilot`, `canDecideAutopilot`, `canViewAutopilotReport`, `hasDashboardAccess`, `hasPortalAccess`) plus local `canOperator` and `canReports` recomputed in `Sidebar.tsx` and `DashboardPage.tsx`. |
| Media Builder | `EntraAuthenticationService.HasRole`, ID token `roles` claim | None. `HasMediaBuilderRole = IsAdministrator \|\| IsTechnician`. | `IsAdministrator` drives Generate Boot Image (`ShellViewModel`, `OperationSelectionViewModel`), pre-production visibility (`PrepareStorageDeviceViewModel.VisibleBootImages`) and whether the certificate check runs at all. |
| Operator API | `AppRoleAuthorizationMiddleware` | Service roles only (PortalAccess, MediaBuilderAccess) | Function-name allowlist. Never sees user roles. |

---

## R1. Where the implication hierarchy lives in each app

**Decision**: One table per app, same edges everywhere:
Administrator implies Operator and Technician; Operator implies Technician. The Portal backend
additionally maps each of the three to `CloudImaging.PortalAccess`.

- Portal backend: extend `ROLE_IMPLICATIONS` in `roleGuard.ts` with the full closure
  (`Administrator: [Operator, Technician, PortalAccess]`, `Operator: [Technician, PortalAccess]`,
  `Technician: [PortalAccess]`). Keep the existing single-pass expansion.
- Portal frontend: new pure module `client/src/lib/portalRoles.ts` with the same table (without
  PortalAccess, which is a server-only concept) and `expandRoles(raw)`.
- Media Builder: new static class `Services/MediaBuilderRoles.cs` with the constants, the table and
  `Expand(IEnumerable<string>)`.

**Rationale**: FR-004 requires identical behaviour in three runtimes that share no code. Listing the
closure explicitly keeps the current one-pass loop correct and makes the table readable at a glance.
A test in each suite asserts the table is closed (every implied role's own implications are already
listed), so a future edge cannot silently break transitivity.

**Alternatives considered**:
- Transitive fixed-point expansion: correct, but changes a tested algorithm for no gain at three edges.
- One shared package for client and server: the two are separate npm packages with no workspace; adding one is out of proportion. The Media Builder cannot share it anyway.
- Expanding roles in the Operator API: the Operator API never sees user tokens from the Portal (it sees the Portal backend's managed identity), so it cannot.

---

## R2. How Portal backend guards name accepted roles

**Decision**: Add `requireOperator = requireRole('CloudImaging.Operator', 'CloudImaging.Administrator')`
next to the existing `requireAdmin`, and use the named guards in routers. Autopilot guards list every
accepted role explicitly, including Operator.

**Rationale**: Administrator already implies Operator, so `requireRole('CloudImaging.Operator')`
alone would authorise correctly, but its 403 detail would read "requires ... CloudImaging.Operator"
and omit Administrator. FR-016 and FR-023 need the detail to name every accepted role, and the
Portal shows that detail in its error toasts (`extractErrorDetail`). Naming both also keeps routes
greppable by intent.

**Alternatives considered**: Generating the 403 detail from the implication table (list every role
that implies a required role). More magic, harder to read in a route file, and changes the message
for routes that are otherwise untouched.

---

## R3. Reports and Autopilot allowlists today, and what changes

**Reports data routes**:
- `GET /api/images`, `GET /api/boot-images`, `GET /api/recovery-images` accept `PortalAccess` or `Reader`. Operator passes through the implication table with no edit. Technician keeps them: the Technician catalog pages and Dashboard stats read them.
- `GET /api/session-history` accepts `PortalAccess` or `Reader` today, so Technician passes. **Narrow to Reader, Operator, Administrator** and add a completion-times read for the Dashboard (R13).
- `GET /api/autopilot/history` (`canAudit`) accepts Approver, Administrator, Reader. It does not accept Technician. **Add Operator.**
- The frontend gates the Reports pages on `isAdministrator || isReader` (`RequireReportsAccess`, `Sidebar`, `Dashboard`) and the Autopilot report on `canViewAutopilotReport`. **Add Operator to both.**

**Autopilot decision routes**:
- `canView` (queue list and detail) accepts Approver, Administrator, Technician. Operator passes through the implication table; **add Operator to the list for an accurate 403 detail only**.
- `canDecide` (approve, reject, retry) accepts Approver, Administrator. **Add Operator.** Changing the group tag of a device already in Autopilot goes through `approve`, so it is covered.
- `GET /api/autopilot/group-tags` accepts Approver, Administrator. No Portal screen uses it for an Approver (only `AutopilotConfigPanel` on the Administrator-only Configuration page). **Add Operator** for parity with Approver (FR-003: Operator grants what Approver offers). Group tag writes stay Administrator-only.
- Decision dialogs read group tag options from the registration detail (`groupTagOptions`), so no other read is needed (FR-013 "every read those decisions need").

**Rationale**: Minimal, explicit edits that satisfy FR-013 and FR-015 without touching Reader or Approver behaviour.

**Alternatives considered**: Making Operator imply Reader and AutopilotApprover. Rejected by FR-003, and it would also leak those roles into any future route that allowlists them for a narrower purpose.

---

## R4. How the frontend derives capabilities from raw token roles

**Decision**: `portalRoles.ts` exports `capabilitiesFromRoles(raw: string[]): PortalCapabilities`,
computed from the expanded set. `AuthProvider` calls it once per render and exposes the result on
the context. The flags:

| Flag | Expanded roles that grant it | Replaces |
|---|---|---|
| `hasPortalAccess` | Technician, Reader, AutopilotApprover | same name |
| `hasDashboardAccess` | Technician, Reader | same name |
| `canUseImaging` | Technician | local `canOperator` in Sidebar, Dashboard, `RequireOperatorAccess` |
| `canManageCatalog` | Operator | `isAdministrator` on OS, Boot, Recovery Images pages |
| `canManageLocations` | Operator | `RequireAdmin` on Locations and Wi-Fi Networks, `'admin'` nav level for those two |
| `canViewReports` | Operator, Reader | local `canReports`, `RequireReportsAccess` |
| `canViewAutopilot` | Technician, AutopilotApprover | same name |
| `canDecideAutopilot` | Operator, AutopilotApprover | same name |
| `canViewAutopilotReport` | Operator, Reader, AutopilotApprover | same name |
| `isAdministrator` | Administrator | same name, now only for Administrator-only surfaces |

Because Administrator expands to Operator and Technician, no flag needs an explicit
`|| isAdministrator`.

**Rationale**: The client test suite has no DOM testing library and tests components by invoking
them with mocked hooks. A pure function is the cheapest way to cover the full role-by-capability
matrix (FR-018 to FR-021). It also removes the duplicated `canOperator`/`canReports` logic in
`Sidebar.tsx` and `DashboardPage.tsx`, which is where an Operator-only user would otherwise fall
through (neither `isAdministrator` nor raw `isTechnician` is true for them, so they would hit the
Access denied screen).

**Alternatives considered**:
- Adding `|| isOperator` at each call site: repeats the current scattering and makes the Operator-only edge case easy to miss.
- Reading roles from the access token instead of the ID token: both carry the same app roles for this registration; the ID token is already what the context uses.

---

## R5. Whether the capability map replaces scattered `isAdministrator` checks

**Decision**: Yes for catalog and location surfaces, no for Administrator-only surfaces.

- Replace: `OsImagesPage.tsx`, `BootImagesPage.tsx`, `RecoveryImagesPage.tsx` (`isAdministrator` to `canManageCatalog`); `App.tsx` Locations and Wi-Fi routes; `Sidebar.tsx` and `DashboardPage.tsx` Locations and Wi-Fi entries (new access level `'manage'`).
- Keep `isAdministrator`: `RequireAdmin` for Branding and Configuration, `SessionsPage.tsx` pre-flight approve and revoke, `UpdateAvailableBanner.tsx`, and the certificate expiry toast in `DeploymentConfigPage.tsx` (which reads `rolesFromAccount` directly; Administrator is the top role, so no expansion is needed there).

**Rationale**: Each remaining `isAdministrator` maps to an item on the FR-012 list, so a reviewer can
read intent from the flag name.

---

## R6. Media Builder role model

**Decision**: `MediaBuilderRoles.Expand` feeds three properties on `EntraAuthenticationService`:
`HasMediaBuilderRole` (expanded set contains Technician), `CanGenerateBootImage` (contains
Operator) and `CanSeePreProductionBootImages` (contains Operator). `IsAdministrator` and
`IsTechnician` stay for diagnostics but no gate reads them.

- `ShellViewModel`: gate, nav reason and **certificate check trigger** use `CanGenerateBootImage`.
- `OperationSelectionViewModel`: constructor parameter `isAdministrator` renamed `canGenerateBootImage`; the `HasMediaBuilderRole || canGenerateBootImage` fallback stays.
- `PrepareStorageDeviceViewModel.VisibleBootImages(images, canSeePreProduction)`.

**Rationale**: The certificate check currently runs only `if (IsAdministrator && ...)`, and
`_certificateStatus` starts as `Configured`. Keying only the role gate on Operator would show
Generate Boot Image as available to an Operator user with no certificate configured, contradicting
US3 AS3. Moving the trigger to the same capability closes that. A static pure module makes the role
logic testable without a signed-in MSAL result (`EntraSignInTests` can only cover the signed-out case today).

**Alternatives considered**: An Operator-specific branch in each view model. Rejected: three
places to keep in sync, and the gate is really a capability, not a role.

---

## R7. Operator API

**Decision**: No behavioural change. Correct the XML doc comments that describe who may perform
Portal writes (`ImageCatalogFunctions`, `BootImageLifecycleFunctions` including its inline comment,
`LocationFunctions`, `WifiNetworkFunctions`, `AutopilotFunctions`) to say the Portal backend accepts
Operator or Administrator (Operator, Approver or Administrator for Autopilot decisions). Update
the reason text in two tautological contract tests. The static OpenAPI document names no user roles
and needs no change.

**Rationale**: FR-024 and FR-025. `BootImageLifecycleFunctions` currently claims the middleware
enforces Administrator, which has never been true; the correction removes a misleading statement.

---

## R8. Media Builder gate is a workflow guard, not an access control

**Decision**: Document, do not change. The Operator API sees every Media Builder user as
`MediaBuilderAccess` and cannot distinguish Technician, Operator and Administrator. Publishing a
generated image happens through the Portal's Boot Images upload, which the Portal backend authorises
against the Portal registration.

**Rationale**: FR-030 and the spec assumptions. Tightening certificate reads by Media Builder role is out of scope.

---

## R9. Tests

**Decision**:
- Add `role-route-matrix.test.ts`: mount every router behind an injected-claims stand-in (as in
  `autopilot-routes.test.ts`), mock `operatorApiClient` with resolved values and `updateCheck`
  (whose real `getUpdateStatus` calls GitHub), so no unit test makes a network call, and for each route in
  [contracts/portal-server-routes.md](contracts/portal-server-routes.md) assert "not 403" or "403"
  for Administrator, Operator, Technician, Reader, Approver, and Operator plus Technician. Use
  invalid GUIDs where a route validates them, since `requireGuidParams` runs after the role guard
  and returns 400 without calling the backend.
- Replace the string-equality assertions in `role-enforcement.test.ts` and `auth-guard.test.tsx`;
  they cannot fail and gave no protection. The matrix and `portal-roles.test.ts` take their place.
  The role checks in `image-management.test.ts`, `os-image-chunked-upload.test.ts`,
  `branding.test.ts` and `portal-configuration.test.ts` are of the same kind and are removed as
  redundant with the matrix.
- Media Builder: `MediaBuilderRolesTests` plus Operator cases in the view model tests.
- Manual: Media Builder shell certificate-check trigger (no `ShellViewModel` test harness exists).

**Rationale**: Constitution II and III. The matrix also guards SC-001 and SC-003 mechanically.

---

## R10. Provisioning, documentation and release notes

**Decision**:
- Role creation and assignment stay manual (FR-031). `verify-app-registrations.ps1` stays unchanged (FR-033).
- `uiFormDefinition.json` Portal client ID tooltip lists all five Portal user roles (text only, FR-035, R16).
- `upgrade-instructions.md` gets a release-specific section: add the role to both registrations without touching existing roles or assignments, skip if present, assign, sign out and in, upgrade both streams before relying on it, rollback consequence for Operator-only users.
- `setup-instructions.md`: Operator role on the Portal and Media Builder registrations, a Cloud Imaging Operators persona group, a column in the assignment table (Portal, Media Builder, and `MediaBuilderAccess` on the Operator API), and the Phase 5 Generate Boot Image prerequisite (which today wrongly lists Technician).
- Release notes: written in `.github/release-notes/<stream>/<version>.md` and published by the release workflows (R14); the backend and Media Builder notes link to the upgrade section.
- `docs/image-upload-pipeline.md` and `README.md` state Administrator-only upload, pre-production and boot image generation rules and two user roles; both are corrected to Operator or Administrator and the current role list.

**Rationale**: Matches how the existing roles were introduced and keeps every deployment script unchanged.

---

## R11. Edge cases needing no code

- Operator plus Technician, Reader or Approver: union of expanded capabilities; no duplicate navigation because navigation is filtered, not concatenated.
- Role assigned or removed mid-session: token-bound, as for every role today; documented.
- Group assignment: identical to direct assignment in the token; nested groups follow Entra ID behaviour.
- Rollback: old code ignores Operator, so an Operator-only user gets Access denied (Portal) or the no-role state (Media Builder). Documented in the upgrade instructions.
- Abuse-control observability (spec 002 FR-014): no Portal route exposes it today, so there is nothing to gate.

---

## R12. Spec 005 (driver management)

**Decision**: No driver code exists yet. Spec 005 routes use `requireOperator` for writes and
`requireRole('CloudImaging.PortalAccess')` for Technician reads; its pages use `canManageCatalog`
(or a dedicated flag derived from Operator in `portalRoles.ts`).

---

## R13. Reports data and Technician

**Finding**: Of the Reports data routes, only `GET /api/session-history` accepts Technician
(through `PortalAccess`). `GET /api/autopilot/history` never did. The catalog lists are shared with
Technician pages. One non-Reports consumer of session history exists: `DashboardPage.tsx` fetches
`/api/session-history?from=...` for every role to compute the Completed Sessions stat and its
trend, using only `finalState` and `terminalAt`. No Devices, Sessions or other Technician page
calls it. The route comment in `session-history.ts` names the Dashboard as the reason it accepts
`PortalAccess`.

**Decision**: Narrow `GET /api/session-history` to
`requireRole('CloudImaging.Reader', 'CloudImaging.Operator', 'CloudImaging.Administrator')` and add
`GET /api/session-history/completed-timestamps` with the old guard (`PortalAccess`, `Reader`). The
new route calls the same `operatorApiClient.getSessionHistory(from, to)`, filters to
`SessionCompleted` and returns only the `terminalAt` strings. `DashboardPage.tsx` switches to it;
the Report pages keep the full route. Both changes ship in the backend stream together.

**Rationale**: Narrowest fix that refuses Technician the device, location, pre-flight and error
detail without changing any Technician page. Returning timestamps rather than daily counts keeps
the Dashboard's local-day bucketing exactly as it is.

**Alternatives considered**:
- Narrow only, Dashboard hides the stat for Technician: removes a visible Technician capability (FR-005).
- Compute the count from `GET /api/sessions`: undercounts, because live sessions are purged long before history (the existing Dashboard comment explains this).
- Field projection on the same route by role: one route with two response shapes is harder to test and document than two routes.
- Server-side daily counts: needs the client time zone, and changes the trend logic.

---

## R14. Release notes from a file

**Finding**: All three release workflows (`release-iac.yml`, `release-client.yml`,
`release-mediabuilder.yml`) pass a fixed multi-line `body` to `softprops/action-gh-release@v3`,
consisting of a stream description and an artifact table built from `steps.tag.outputs.version`
(the tag minus its stream prefix, for example `v1.4.0` or `v1.4.0-beta.1`). Each workflow resolves
the tag first, then stages the dependency gate script from the working tree, then checks out the
tag on manual runs. The `-latest` alias releases have a one-line body pointing at the versioned
release.

**Decision**:
- Path: `.github/release-notes/<stream>/<version>.md`, with stream `backend` (`mse-ci-v*`), `client` (`mse-ci-client-v*`) or `mediabuilder` (`mse-ci-mediabuilder-v*`), and `<version>` equal to `steps.tag.outputs.version`. Example: `.github/release-notes/backend/v1.1.0.md`.
- New check inside "Determine version tag", placed after the tag pattern check and before the existence check that creates and pushes a missing tag on manual runs (`release-iac.yml` lines 76 to 78 and 79 to 91; `release-client.yml` 67 to 69 and 70 to 82; `release-mediabuilder.yml` 70 to 72 and 73 to 85). The version is computed there (the `$version` assignment moves up), the file is copied to `$env:RUNNER_TEMP/release-notes.md`, and the step throws naming the expected path if it is missing or whitespace only. A release without notes therefore fails before any tag is created or pushed and before any build. The tag has already been validated against a strict pattern at that point.
- Prerelease tags (for example `mse-ci-v1.1.0-beta.1`) resolve to their own version (`v1.1.0-beta.1`) and need their own notes file.
- Like the gate script, the file is read from the working tree before "Check out the resolved tag": on a tag push that is the tagged commit, on a manual run it is the dispatched branch, so notes can be added or corrected for an existing tag and the run repeated.
- "Create GitHub Release" uses `body_path: ${{ runner.temp }}/release-notes.md` instead of `body`. The stream description and artifact table move into the notes file, so the published body equals the file. A new notes file starts as a copy of the previous one for the same stream.
- All three streams use the convention, so there is one way to write notes. Only the backend and Media Builder notes for the releases that ship this feature carry the Operator notice. Alias release bodies are unchanged. `CONTRIBUTING.md`, which says release notes are generated from PR titles and labels, is updated to describe the files.
- The Media Builder body text's `docs/setup-instructions.md` navigation reference is rewritten with ">" when it moves into the notes file (FR-040).

**Rationale**: FR-034. The notice is reviewed in a pull request like the code it describes, and a
release cannot ship with stale or missing notes.

**Alternatives considered**:
- `docs/release-notes/...`: `docs/` is the GitBook space (`gitbook-docs.yaml`), so the files would be synced and published as pages outside `SUMMARY.md`.
- One rolling `RELEASE_NOTES.md` per stream: loses history, and a forgotten update silently republishes the previous release's notes.
- Notes file plus an artifact table appended by the workflow: less copying, but the body no longer equals the file and the versioned asset names are known when the file is written anyway.
- Editing the release description after tagging (previous plan): manual, unreviewed, and lost if the workflow is re-run.
- `generate_release_notes: true`: produces pull request titles, not administrator instructions.

---

## R15. Banned characters in touched files

**Decision**: Remove U+2014, U+2013, U+00B7, U+2192 and U+27F9 from every file the plan changes,
in docs, UI text, log messages and comments (FR-040). The full file list is in the plan's
Character cleanup section. User-visible replacements:
- Navigation paths: "Entra ID > Enterprise applications > Cloud Imaging Portal > Users and groups".
- Hierarchy line in `roles-and-access.md`: "`Administrator` implies `Operator`, which implies `Technician`, which implies `PortalAccess`".
- Media Builder boot image label: `v{Version}, {Architecture}, Wi-Fi ({Size}){Stage}`.
- Portal upload dialogs: `{file name} ({size})`.
- Log messages: "Location created. HTTP {StatusCode}." and "Boot image delete proxied for {BootImageId}. ImagingCore HTTP {StatusCode}."

**Test impact**: none. No test asserts on these labels or log messages (searched the Media Builder,
Operator API and portal test suites); the two test files in the list change only in comments.

**Alternatives considered**: Scoping the scan to changed lines only. Rejected: it leaves mixed
punctuation in the same document and makes the zero-hit scan unusable as a gate.

---

## R16. Deployment form tooltip

**Finding**: `uiFormDefinition.json` Portal client ID tooltip names only Administrator and
Technician. The Media Builder client ID tooltip names no roles; the Operator API tooltip names the
two service roles.

**Decision**: Portal tooltip becomes: "Client ID of the Cloud Imaging Portal SPA app registration.
Exposes user_impersonation and the CloudImaging.Administrator, CloudImaging.Operator,
CloudImaging.Technician, CloudImaging.Reader and CloudImaging.AutopilotApprover user roles. Used by
the portal frontend (MSAL) and validated by the portal backend." Media Builder and Operator API
tooltips unchanged.

**Rationale**: The tooltip describes what the registration exposes, so a partial list is wrong.

---

## Risks and open questions found while reading the code

1. **Operator-only user hits Access denied** unless `hasPortalAccess` uses expanded roles (frontend has no hierarchy today). Covered by R4.
2. **Media Builder fail-open on the certificate gate** for Operator if only the role gate moves (R6).
3. **Single-pass server expansion**: the table must list the closure; covered by a closure test (R1).
4. **Tautological tests** (`role-enforcement.test.ts`, `auth-guard.test.tsx`, Operator API contract tests) give no regression protection today (R9).
5. **Resolved (R13)**: `GET /api/session-history` accepted Technician through PortalAccess, while the Reports UI is hidden from Technician and `roles-and-access.md` says Technician has no Reports. The route is narrowed and the Dashboard moves to a completion-times read.
6. **Mixed versions** between the backend and Media Builder streams give an Operator user different behaviour per app until both are upgraded (plan, Release Considerations).
7. **Resolved (R14)**: release notes now come from committed files per stream and version.
8. **Resolved (R15)**: banned characters are removed from every touched file, not only changed lines.
9. **Resolved (R16)**: the tooltip lists all five Portal user roles.

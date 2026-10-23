# Contract: Portal Client Surfaces

**Feature**: [../spec.md](../spec.md) | **Routes**: [portal-server-routes.md](portal-server-routes.md) | **Research**: [../research.md](../research.md) R12, R14, R15

All surfaces follow `.github/instructions/portal-ui.instructions.md` (tokens, 400/500/600 weights,
4px grid, 12/16/20/24 icons, primitives from `components/ui/`, `Button` size `sm` for row
actions). Timestamps are absolute (`2026-10-22 02:00`, `formatDateTime`). Table rows are one line
high and truncate. All text is ASCII. Strings below are the proposed user-facing text.

---

## 1. Capabilities (`lib/portalRoles.ts`)

| Flag | Expanded roles | Used by |
|---|---|---|
| `canViewSetupProfiles` | Technician | Navigation item, list page, editor in read-only mode, History (no Restore), Start imaging overlay lines |
| `canManageSetupProfiles` | Operator | New profile, edit controls, save bar, Duplicate, Delete, Restore |

Retention lives on the Configuration page, which is already Administrator-only (`isAdministrator`).

## 2. Navigation and routes (`components/Sidebar.tsx`, `App.tsx`)

- Sidebar, Imaging section: "Setup Profiles" (icon `ListChecks`, size 18 as for every sidebar
  item), after Recovery Images (after Drivers once spec 005 adds it), access level `'imaging'`.
- Routes, lazy-loaded, guarded by `canViewSetupProfiles`: `/setup-profiles`,
  `/setup-profiles/new` (also needs `canManageSetupProfiles`), `/setup-profiles/:profileId`,
  `/setup-profiles/:profileId/versions/:version`. `lib/routeTitles.ts` gets "Setup Profiles".

## 3. List page (`pages/SetupProfilesPage.tsx`)

- Header: title "Setup Profiles"; description "Registry values, scripts and an answer file applied
  to devices before Windows starts and during Windows setup. Devices get the profile that targets
  the location on their USB media."; button "New profile" (default variant, `Plus`), hidden
  without `canManageSetupProfiles`.
- Empty state (`EmptyState`): title "No setup profiles yet"; text "Set registry values and run
  scripts that must be in place before Windows Autopilot starts. Target a profile at locations or
  at all devices."; action "New profile" (managers only).
- Columns: Name; Targeting (`TargetingBadges`: "All devices" outline badge, or first location
  plus "+N more" with a tooltip listing up to five more names); Registry values (count);
  Scripts (count); Last modified (absolute time, author in a tooltip); Actions (ghost icon
  buttons with tooltips: "Edit" or "View", "Duplicate", "Delete"; managers only except View).
- Delete: `ConfirmImpactDialog` title "Delete <name>?"; impact "Devices started from media for
  these locations stop getting this profile: <names>. Deployments already running keep the
  version they started with. History is kept."; `details` lists the locations (or "All devices");
  confirm "Delete", destructive.

## 4. Editor page (`pages/SetupProfileEditorPage.tsx`)

Sections in order, each a `Card` with icon, title and helper text:

1. **Profile**: Name input. Version line "Version 8, modified 2026-10-22 14:03 by ops@contoso.com"
   and a "History" outline button.
2. **Targeting**: `Select` with "All devices" and "Selected locations", and the shared
   `LocationPicker` for Selected locations. A location targeted by another profile is disabled
   with the tooltip "Targeted by <profile>". "All devices" is disabled with the tooltip "Already
   used by <profile>" when another profile has it. Helper text: "A location can belong to one
   profile. Devices whose location has no profile get the All devices profile, if one exists."
3. **Registry values**: table Name, Value (full path; tooltip "Value name: X. Type: DWORD."),
   Data, Actions (edit, delete). "Add registry value" outline button. Dialog fields: Name ("Why
   this value is set"), Hive (Select: HKLM SOFTWARE, HKLM SYSTEM, Default user), Key, Value name
   (placeholder "(Default)"), Type (Select), Data (input; textarea for Multi-string, one string
   per line). Help under Key: "Relative to the hive, for example Policies\Microsoft\FVE. Under
   HKLM SYSTEM, CurrentControlSet is mapped to the image's current control set. 32-bit software
   reads WOW6432Node keys; name them explicitly."
4. **Scripts**: table Order, Name, Runs ("In WinPE" or "During Windows setup"), Timeout ("5 min"),
   Last modified, Actions (move up, move down, edit, delete; same buttons and tooltips as the
   Wi-Fi networks table). Order applies within each phase; the table groups by phase with a
   muted group row. "Add script" outline button. Dialog: Name, Runs and Timeout selects on one row
   beside Name; full-width `CodeTextArea` below; size counter "12.4 of 64 KB"; warning callout
   (`AlertTriangle`): "Everyone who can open Setup Profiles can read this script. Do not put
   passwords, keys or other secrets in it." Runs help: "In WinPE: right after the image is
   applied, as SYSTEM, with the Windows drive in CI_WINDOWS_DRIVE. The result shows on the
   deployment timeline. During Windows setup: before OOBE, as SYSTEM. The result is logged on the
   device only." Timeout options 1, 2, 5, 10, 15, 20, 30 minutes.
5. **Answer file**: one-row table File ("unattend.xml"), Passes (outline badges), Size, Last
   modified, Actions (edit, remove); "Add answer file" when none. Dialog: `CodeTextArea` with line
   numbers, "Upload file" outline button (accepts `.xml`, read as UTF-8, at most 256 KB), live
   problem list under the editor ("Line 4: The windowsPE pass is not allowed, ...") in
   `text-sm text-destructive`, a valid state "Valid answer file. Passes: specialize, oobeSystem.";
   Done disabled while any problem exists. Helper text: "Windows setup reads this file on every
   device that gets this profile. Cloud Imaging adds one command of its own and overwrites any
   answer file in the OS image."
6. **Failure handling**: radio group "Complete with warnings" (default; "Failed items are listed on
   the deployment timeline and the deployment continues.") and "Fail the deployment" ("The
   deployment stops before the device restarts into Windows. Results of Windows setup scripts
   never change the result.").

**Save bar** (sticky bottom, shown when the draft differs): "You have unsaved changes." with
"Discard" (outline) and "Save as version N" (default) when content changed, or "Save changes" when
only name or targeting changed. Save is disabled while the answer file has problems or targeting
is empty ("Choose at least one location or All devices."). Leaving with unsaved changes asks
"Discard your changes?" through `ConfirmImpactDialog`.

**Read-only**: without `canManageSetupProfiles`, and for a viewed version, inputs are replaced by
text, edit actions and the save bar are hidden, and a muted banner says "Read-only" (viewer) or
"Version 5, read-only. The current version is 8." with a "Restore" button for managers.

**Duplicate**: opens `/setup-profiles/new` with name "<name> (copy)", content of the current
version, no targeting.

## 5. History dialog (`components/setup-profiles/ProfileHistoryDialog.tsx`)

- Tabs "Versions" and "Targeting".
- Versions columns: expand chevron, Version, Modified, Modified by, Changes (count), Status
  (green "Current" badge, or muted "In use" badge with tooltip "Used by 3 running deployments"),
  Actions ("View", "Restore" for managers on non-current versions).
- Expanded row: labelled grid fields Registry values ("1 added, 2 changed: Turn on automatic time
  zone, ..."), Scripts, Answer file ("Changed", "Added", "Removed", "No change"), Failure handling
  ("Complete with warnings to Fail the deployment" or "No change"). Restored and duplicated
  versions add a field "Restored from" or "Duplicated from".
- Targeting columns: Modified, Modified by, Change.
- Restore confirmation: title "Restore version 5?"; impact "Version 9 is created with the content
  of version 5 and becomes current. Targeting does not change. Deployments already running keep
  their version."; confirm "Restore".

## 6. Devices page (`pages/SessionsPage.tsx`, `components/ConfirmImpactDialog.tsx`)

- `ConfirmImpactDialog` gains an optional `details?: React.ReactNode` slot rendered under the
  impact text.
- Start imaging overlay: one line per resolved profile, "<profile name>: N devices", then
  "No setup profile: N devices" when some devices resolve to none. Grouping uses each coupled
  session's `locationId` against the profile list; no version numbers. No lines at all when no
  profiles exist.

## 7. Deployment timeline (`lib/imagingProgress.ts`, `components/SessionProgressDetails.tsx`)

- `PIPELINE_STEPS` entries gain `optional: boolean`; new entry `{ name: 'PrepareSetup', label: 'Prepare setup', optional: true }`
  before ConfigureBoot. The rail shows required steps plus optional steps that were reported or
  are expected (session has `setupProfile`); column count follows the list.
- Knob: amber warning marker (`AlertTriangle` in an amber ring) for Completed with
  `warningCount > 0`; summary text "Completed with warnings".
- Not run: a terminal session with `setupProfile` and no PrepareSetup report shows a muted knob
  and "Not run" with the tooltip "This device's boot image is older than Setup Profiles, so the
  profile was not applied. Update the boot image."
- Prepare setup card (neutral): "Stockholm and Gothenburg, version 8" (or "No setup profile");
  summary from `itemCounts`, for example "3 registry values written. 1 of 2 WinPE scripts
  failed. 2 items queued for Windows setup."; "Show items" loads route 10 and lists one line per
  item (name, outcome badge, exit code and duration for scripts), failures first, full reason in
  a tooltip; "Showing the first 500 items." when `itemsTruncated`.

## 8. Locations page (`pages/LocationsPage.tsx`)

The delete confirmation loads setup profiles and, when the location is targeted, adds "It is
also removed from setup profile <name>." A 409 shows its `detail` in the existing error toast.

## 9. Configuration page (`pages/DeploymentConfigPage.tsx`)

Miscellaneous tab: number field "Setup profile versions kept", 5 to 100, default 25, helper
"Versions kept per profile. Older versions are removed at the profile's next save; versions used
by running deployments are always kept." Added to `PortalConfig`, `DEFAULT_CONFIG`,
`portalConfigEquals` and the save-time validation that switches to the tab on error.

## 10. Shared extractions (no behaviour change for Wi-Fi)

| New file | Extracted from | Added |
|---|---|---|
| `components/LocationPicker.tsx` | `WifiNetworkDialog.tsx` (file-private `LocationPicker`) | optional `disabledReason?: (locationId: string) => string \| undefined` |
| `components/TargetingBadges.tsx` | `WifiNetworksPage.tsx` (`LocationsCell`) | `allLabel` prop |
| `components/ui/code-text-area.tsx` | new | monospace textarea with line number gutter, `aria-describedby` for problems |

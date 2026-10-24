# Contract: Portal Client Surfaces

**Feature**: [../spec.md](../spec.md) | **Routes**: [portal-server-routes.md](portal-server-routes.md) | **Research**: [../research.md](../research.md) R12, R14, R15

All surfaces follow `.github/instructions/portal-ui.instructions.md` (tokens, 400/500/600 weights,
4px grid, 12/16/20/24 icons, primitives from `components/ui/`, `Button` size `sm` for row
actions). Timestamps are absolute (`2026-10-22 02:00`, `formatTimestamp`). Table rows are one line
high and truncate. All text is ASCII. Strings below are the proposed user-facing text.

> **Amendment 2026-10-24** (research R18, design source
> `docs/design/setup-profile-scripts-options.html`, wins over the text below and over
> `os-customisation.html` for these surfaces):
>
> - **Phase names**: Runs options and column values "Offline" and "Specialize"; option
>   descriptions "Right after the image is applied, in the boot image. Shown on the timeline. A
>   failure follows the profile's failure handling." and "During Windows setup, after the restart
>   and before OOBE and Autopilot. Logged on the device only."; timeline item kinds "Offline
>   script" and "Specialize script"; summary texts "2 Offline scripts succeeded", "1 Offline script
>   failed", "1 script and 5 driver installers queued for Specialize".
> - **Scripts card**: description "PowerShell, run as SYSTEM in the order below within each phase. A
>   device runs the scripts for all locations and the ones for its own location."; a toolbar with
>   a search input ("Search scripts") and a 260 px location `Select` ("All scripts" or a location);
>   columns Order (88 px: drag handle `GripVertical` plus the number, which turns into a 44 px
>   number input on click; Enter moves, Escape cancels), Name, Locations (240 px, `TargetingBadges`
>   with `allLabel` "All locations"), Runs (130 px), Timeout (90 px), Actions (96 px: edit,
>   delete). Last modified moves into the dialog. With a location selected the footer reads "4 of
>   186 scripts run on devices from Oslo Office". Moving a script inserts it and renumbers its
>   phase; with a filter on, a move places it among the visible rows. A script with no effective
>   locations shows a muted "No locations" badge and the save bar reads "\"<name>\" has no
>   locations left. Choose a location or delete the script." with Save disabled.
> - **Script dialog**: rows Name, Runs, Timeout as today; then a 360 px "Locations" `Select` ("All
>   locations of this profile", "Selected locations") with the shared `LocationPicker` limited to
>   the profile's targeting (footer "4 of 120 selected" and "Only locations this profile
>   targets"); then the "PowerShell" label with an info icon button ("Script environment") and
>   the help "Exit code 0 is success. Scripts cannot restart the device.", the "Upload file"
>   `outline` button centred against that label block, the `CodeTextArea`, and "1.8 of 24 MB. Do
>   not put secrets in scripts: everyone with access to this page can read them." Opening an
>   existing script shows "Loading the script (12.4 MB)" until its text arrives. Done uploads,
>   showing a progress bar and "Uploading 9.8 of 18.2 MB"; over 24 MB shows "This script is 25.3
>   MB. A script can be at most 24 MB." with Done disabled; a failed upload shows "The script was
>   not uploaded." and replaces Done with "Retry" (`RotateCw`). Cancel stops an upload.
> - **Script environment dialog** (new `components/setup-profiles/ScriptEnvironmentDialog.tsx`):
>   title "Script environment", description "What a script can rely on in each phase.", a table
>   with columns Offline and Specialize and rows When, Runs in, PowerShell, Installed Windows,
>   Native variables, Registry, Network, Result, Timeout, Restarts, Secrets, worded as in the
>   design source; footer "Close" (`outline`).
> - **Answer file**: limit 2 MB ("2.4 of 2048 KB" style size line reads "0.1 of 2 MB"); Done
>   uploads like a script and shows the problems Core returns.

**Design source**: [docs/design/os-customisation.html](../../../docs/design/os-customisation.html).
Layout, wording and component choices follow the mockup; where this contract and the mockup
disagree, the mockup wins and this contract is corrected. Section cards use the icon chip header
of the mockup (36 px chip, `bg-primary/10`, title and description beside it, the section action
button on the right).

---

## 1. Capabilities (`lib/portalRoles.ts`)

| Flag | Expanded roles | Used by |
|---|---|---|
| `canViewSetupProfiles` | Technician | Navigation item, list page, editor in read-only mode, History (no Restore), Start imaging overlay lines |
| `canManageSetupProfiles` | Operator | New profile, edit controls, save bar, Duplicate, Delete, Restore |

Retention lives on the Configuration page, which is already Administrator-only (`isAdministrator`).

## 2. Navigation and routes (`components/Sidebar.tsx`, `App.tsx`)

- Sidebar, Imaging section: "Setup Profiles" (icon `SlidersHorizontal`, size 18 as for every sidebar
  item), after Recovery Images (after Drivers once spec 005 adds it), access level `'imaging'`.
- Routes, lazy-loaded, guarded by `canViewSetupProfiles`: `/setup-profiles`,
  `/setup-profiles/new` (also needs `canManageSetupProfiles`), `/setup-profiles/:profileId`,
  `/setup-profiles/:profileId/versions/:version`. `lib/routeTitles.ts` gets "Setup Profiles".

## 3. List page (`pages/SetupProfilesPage.tsx`)

- Header: title "Setup Profiles"; description "Registry values and scripts applied to devices
  before Windows starts, based on the location set on the USB media they started from."; button
  "New profile" (default variant, `Plus`), hidden without `canManageSetupProfiles`.
- Empty state (`EmptyState`): title "No setup profiles yet"; text "Set registry values and run
  scripts that must be in place before Windows Autopilot starts. Target a profile at locations or
  at all devices."; action "New profile" (managers only).
- Columns: Name (26%); Targeting (`TargetingBadges`: "All devices" outline badge, or first
  location plus "+N more" with a tooltip listing up to five more names); Registry values (count,
  right-aligned, 140 px); Scripts (count, right-aligned, 90 px); Last modified (160 px,
  `2026-10-14 10:05`, author in a tooltip); Actions (96 px, ghost icon buttons with tooltips:
  "Edit" or "View", and "Delete" for managers). Duplicate is in the editor header.
- Delete: `ConfirmImpactDialog` title "Delete <name>?"; impact "Devices started from media for
  these locations stop getting this profile: <names>. Deployments already running keep the
  version they started with. History is kept."; `details` lists the locations (or "All devices");
  confirm "Delete", destructive.

## 4. Editor page (`pages/SetupProfileEditorPage.tsx`)

**Header** (no card): breadcrumb "Setup Profiles > <name>" (`text-xs`, the first part links to the
list), the name as the page heading (20 px, weight 600) and a meta line "Version 7, last modified
2026-10-21 14:32 by Anna Berg" (`text-xs` muted). On the right, `outline` buttons "History"
(`History`), "Duplicate" (`Copy`) and "Delete" (`Trash2`), managers only except History. A new
profile has no meta line and no buttons; its breadcrumb reads "Setup Profiles > New profile". For
managers the heading is an input (`aria-label` "Profile name", placeholder "Profile name"), because
the name is edited in place; viewers see plain text.

Section cards in order, each with the icon chip header:

1. **Targeting** (`Crosshair`): description "Which devices get this profile, based on the location
   set on the USB media they started from." A 320 px `Select` with "All devices" ("Every device
   with no profile for its location, including media prepared without a location or for
   locations added later") and "Selected locations" ("Only USB media prepared for the locations
   you pick"), and the shared `LocationPicker` for Selected locations. A location targeted by
   another profile is disabled with the tooltip "Targeted by <profile>". "All devices" is
   disabled when another profile has it, its description then reading "Used by <profile>. Only
   one profile can target all devices." Picker footer: "2 of 6 selected" and "2 targeted by
   other profiles". The empty-targeting message shows only after the user changed targeting or
   tried to save.
2. **Registry values** (`Database`): description "Written before Windows starts, so they are in
   place before Autopilot and Intune policy."; header button "Add value" (`outline`, `Plus`).
   Table Name (30%), Value (monospace, muted, the full path including the value name; tooltip
   "<path> (DWORD)"), Data (80 px, monospace), Actions (96 px: edit, delete). Dialog "Add
   registry value" or "Edit <name>", description "Written to the installed Windows before it starts
   for the first time.": Name (help "Why the value is set. Shown in the list and on the deployment
   timeline."); a row of Hive (Select: HKLM SOFTWARE, HKLM SYSTEM, Default user) and Key (two
   columns wide); a row of Value name (placeholder "(Default)"), Type (Select) and Data (input;
   textarea for Multi-string, one string per line). Help under the Key row: "Relative to the hive,
   for example Policies\Microsoft\FVE. Under HKLM SYSTEM, CurrentControlSet is mapped to the
   image's current control set. 32-bit software reads WOW6432Node keys; name them explicitly."
   Footer Cancel and Add (or Done when editing).
3. **Scripts** (`SquareTerminal`): description "PowerShell, run as SYSTEM in the order below within
   each phase."; header button "Add script" (`outline`, `Plus`). Table Order (72 px), Name, Runs
   (180 px, "WinPE" or "Windows setup"), Timeout (100 px, "2 min"), Last modified (160 px), Actions
   (160 px: move up, move down, edit, delete; same buttons and tooltips as the Wi-Fi networks
   table). WinPE scripts are listed first; Order restarts at 1 for each phase and move up and
   down stay within the phase. No group rows. Dialog "Add script" or "Edit script", description
   "Runs as SYSTEM on every device that gets this profile.": one row of Name, Runs (200 px) and
   Timeout (140 px, "2 minutes"); "PowerShell" `CodeTextArea` (fixed 12-line height) with the help
   above it "Exit code 0 is success. In WinPE, the Windows drive is $env:CI_WINDOWS_DRIVE. Scripts
   cannot restart the device." and below it "0.3 of 64 KB. Do not put secrets in scripts:
   everyone with access to this page can read them." Runs options with descriptions: "In WinPE"
   ("Right after the image is applied. Shown on the timeline. A failure follows the profile's
   failure handling."; default) and "During Windows setup" ("Before OOBE, after the restart and
   before Autopilot. Logged on the device only."); the short labels keep the 200 px trigger readable. Timeout options 1, 2, 5, 10, 15, 20, 30 minutes.
   Footer Cancel and Done; Done only closes the dialog, Save creates the version.
4. **Answer file** (`FileCode`): description "Optional. Your own unattend.xml for Windows setup,
   for settings this profile has no control for." One-row table File ("unattend.xml",
   monospace), Passes (220 px, plain text "specialize, oobeSystem"), Size (100 px), Last modified
   (160 px), Actions (96 px: Edit, Remove). Without a file the table shows one muted row "No answer
   file" with an "Add" `outline` button. Dialog "Edit answer file", description "Used by Windows
   setup on every device that gets this profile.": the label "unattend.xml" and the "Upload file"
   `outline` button on one row (accepts `.xml`, read as UTF-8, at most 256 KB), the `CodeTextArea`
   with line numbers, then either the valid line "Valid. Passes: specialize, oobeSystem."
   (`CheckCircle2`, success colour) or an error box (`XCircle`) headed "N settings would break
   the deployment" ("This file cannot be used" for whole-file problems) listing "Line N: <message>"
   in `text-sm`. Done is disabled while any problem exists.
5. **Failure handling** (`ShieldCheck`): description "What happens when a registry value cannot be
   written, or a WinPE script fails or times out." One settings row: "Deployment result" (weight
   500) with "Windows setup scripts never change the result: they run after the portal stops
   tracking the device." (`text-xs` muted), and on the right a 300 px `Select` with "Complete with
   warnings" (default) and "Fail the deployment".

**Save bar** (at the end of the content, right-aligned, shown when the draft differs): "You have
unsaved changes." with "Discard" (outline) and "Save as version N" (default) when content changed,
or "Save changes" when only name or targeting changed. Save is disabled while the answer file has
problems or targeting is empty ("Choose at least one location or All devices."). Leaving with
unsaved changes asks "Discard your changes?" through `ConfirmImpactDialog`.

**Read-only**: without `canManageSetupProfiles`, and for a viewed version, inputs are replaced by
text, edit actions and the save bar are hidden, and a muted banner says "Read-only" (viewer) or
"Version 5, read-only. The current version is 8." with a "Restore" button for managers.

**Duplicate**: opens `/setup-profiles/new` with name "<name> (copy)", content of the current
version, no targeting.

## 5. History dialog (`components/setup-profiles/ProfileHistoryDialog.tsx`)

- Title "History of <name>"; description "Each save is a version. Restoring one saves its content
  as a new version."; footer "Close" (`outline`).
- Tabs "Versions" and "Targeting".
- Versions columns: expand chevron (44 px), Version (90 px), Modified (160 px), Modified by
  (140 px), Changes ("1 change", "2 changes"), Status (120 px: green dot "Current" badge, or
  muted dot "In use" badge with tooltip "2 running deployments use this version", singular "1
  running deployment uses this version"), Actions (96 px: "View version N" with `Eye`, and
  "Restore version N" with `Undo2` for managers on non-current versions).
- Under the Versions table: "7 versions. The 25 most recent are kept, plus older ones running
  deployments still use." (`text-xs` muted; the number is the retention setting).
- Expanded row: labelled grid fields Registry values, Scripts, Answer file, Failure handling.
  Values: "No change"; names quoted with their change, for example ""Register device in asset
  inventory" changed", joined with commas, plus "and N more" past ten names; scripts add
  "reordered"; Answer file "Added", "Removed" or "Changed"; Failure handling "Complete with
  warnings to Fail the deployment". Restored and duplicated versions add a field "Restored from"
  ("Version 5") or "Duplicated from" ("<profile>, version 3").
- Targeting columns: Modified (160 px), Modified by (140 px), Change ("Gothenburg Office added").
- Restore confirmation (`ConfirmImpactDialog`, icon `Undo2`): title "Restore version 6?"; impact
  "Saves version 6's registry values, scripts, answer file and failure handling as version 8.
  Devices imaged from now on get it; running deployments keep the version they started with.
  Targeting is not changed."; confirm "Restore" (default).

## 6. Devices page (`pages/SessionsPage.tsx`, `components/ConfirmImpactDialog.tsx`)

- `ConfirmImpactDialog` gains an optional `details?: React.ReactNode` slot rendered under the
  impact text, and its icon is centred vertically on the text block.
- Start imaging overlay: a line "Setup profile, by the location on each device's USB media:" and a
  list with one entry per resolved profile, "3 devices: Stockholm and Gothenburg" (singular "1
  device"). Devices that reach the All devices profile only because their media has no location
  read "1 device started from media without a location: Nordics standard". Devices that resolve to
  no profile read "2 devices: No setup profile". Grouping uses each coupled session's
  `locationId` against the profile list; no version numbers. No lines at all when no profiles
  exist or the list cannot be loaded.

## 7. Deployment timeline (`lib/imagingProgress.ts`, `components/SessionProgressDetails.tsx`)

- `PIPELINE_STEPS` entries gain `optional: boolean`; new entry `{ name: 'PrepareSetup', label: 'Prepare setup', optional: true }`
  before ConfigureBoot. The rail shows required steps plus optional steps that were reported or
  are expected (session has `setupProfile`); column count follows the list.
- Knob: amber warning marker for Completed with `warningCount > 0`; summary text "Completed with
  warnings". A failed step keeps the timeline's existing summary ("Failed on Prepare setup after
  4s", docs/design/deployment-timeline-options.html).
- Not run: a terminal session with `setupProfile` and no PrepareSetup report shows a muted knob
  and "Not run" with the tooltip "This device's boot image is older than Setup Profiles, so the
  profile was not applied. Update the boot image."
- Prepare setup card, same pattern as Install drivers: heading "Setup profile: Stockholm and
  Gothenburg, version 7" (or "No setup profile"). Summary from `itemCounts` as separate items,
  each with an icon: `CheckCircle2` success "4 registry values written", "2 WinPE scripts
  succeeded"; `XCircle` destructive "1 WinPE script failed", "1 registry value failed"; `Clock`
  "1 script and 5 driver installers queued for Windows setup"; then a "Show details" `link`
  button that loads route 10 and lists every item. When any item failed, the failed items are
  loaded and listed straight away as one-line rows (`XCircle`, kind "Registry value", "WinPE
  script", "Windows setup script", "Driver installer" or "Answer file", name, "Failed: <short
  reason>" muted; full reason in a tooltip), followed by "Failures are listed first; N other items
  are hidden." until Show details is used. Notes (`text-xs` muted): "Windows setup runs after the
  restart and is recorded on the device only." when anything is queued; "The device does not
  start Windows. Retry the deployment after fixing the profile or the OS image." when the step
  failed. "Showing the first 500 items." when `itemsTruncated`.

## 8. Locations page (`pages/LocationsPage.tsx`)

The delete confirmation loads setup profiles and, when the location is targeted, adds "It is
also removed from setup profile <name>." A 409 shows its `detail` in the existing error toast.

## 9. Configuration page (`pages/DeploymentConfigPage.tsx`)

Miscellaneous tab: a card "Setup profiles" (icon chip `SlidersHorizontal`, description "History
kept for each setup profile.") with one settings row: "Versions kept per profile" and the helper
"Older versions are deleted at the next save. A version a running deployment still uses is always
kept.", and a 120 px number input on the right (5 to 100, default 25; error "Keep between 5 and
100 versions per profile."). Added to `PortalConfig`, `DEFAULT_CONFIG`, `portalConfigEquals` and
the save-time validation that switches to the tab on error.

## 10. Shared extractions (no behaviour change for Wi-Fi)

| New file | Extracted from | Added |
|---|---|---|
| `components/LocationPicker.tsx` | `WifiNetworkDialog.tsx` (file-private `LocationPicker`) | optional `disabledReason?: (locationId: string) => string \| undefined` |
| `components/TargetingBadges.tsx` | `WifiNetworksPage.tsx` (`LocationsCell`) | `allLabel` prop |
| `components/ui/code-text-area.tsx` | new | monospace textarea with line number gutter, `aria-describedby` for problems |

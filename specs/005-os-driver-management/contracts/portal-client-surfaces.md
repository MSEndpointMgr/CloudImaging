# Contract: Portal Client Surfaces

**Feature**: [../spec.md](../spec.md) | **Routes**: [portal-server-routes.md](portal-server-routes.md) | **Design source**: `docs/design/os-drivers.html`

All surfaces follow `.github/instructions/portal-ui.instructions.md` and the `Button` primitive
rules (size `sm` for row actions, `outline` for secondary, `secondary` for Cancel). Tables: every
row one line high with truncation, absolute timestamps (`2026-10-22 02:00`, `formatTimestamp`),
dates only for Last updated. All text ASCII, no spec or task identifiers. Action labels everywhere:
**Offline**, **Offline + Specialize**, **Specialize**, **Skip** (spec clarification 2026-10-24
replaces "WinPE" in FR-062 and the tiles). Where the mockup still says "WinPE", the clarification
wins.

**Vendor names are data, never literals.** Every string that names a vendor (for example "Released
by HP", "downloaded again from HP", "HP has published a new ...") is built from the model's or
component's `vendor` through one `vendorLabel(vendor)` helper in `lib/drivers.ts` (`Hp` gives "HP",
`Dell` gives "Dell"). Components and pages under `components/drivers/` and `pages/Driver*.tsx`
contain no vendor name literals; the vendor filter, Vendors card and onboarding vendor picker list
vendors from the `GET vendors` response. HP and Dell examples in this contract and the mockups are
sample data.

## 1. Capabilities and navigation

- `lib/portalRoles.ts`: `canViewDrivers` (Technician and above), `canManageDrivers` (Operator and
  above).
- Sidebar, Imaging section: "Drivers" (icon `Cpu`, size 18) after Recovery Images and before
  Setup Profiles. Routes, lazy-loaded and guarded by `canViewDrivers`: `/drivers` (tabs via
  `?tab=models|rules|sync|downloads|storage`; the Downloads tab label shows the number of downloads
  in progress), `/drivers/models/:modelId` (`?release=`). `routeTitles.ts`:
  "Drivers".
- Read-only users see every tab without edit controls: no Onboard model, no Action dropdowns
  (plain text), no switches (disabled with values), no Sync now or Check for updates, no rule actions.

## 2. Models tab

Header description: "Drivers installed during deployment, per device model and Windows release."
Toolbar: `SearchInput` ("Search models"), vendor `Select` ("All vendors", HP, Dell), then right-aligned
muted "Last checked 02:00 today" (last run start from `GET /sync/settings`), "Check for updates"
(`outline`, `RefreshCw`; starts the components-only run of route 45; disabled while a run is active or
until `syncNowAvailableAt` (shared with Sync now), with the tooltip "Checks every onboarded model for
new component versions. Available again at 03:00."; on success a toast "Checking all models for updates.") and "Onboard
model" (`default`, `Plus`). Columns and widths: Model 24% (name, match keys below in `text-xs`
muted), Vendor 7%, Windows releases 13%, Included 9%, Skipped 9%, Size 9% (info icon: "The most
one device downloads. Devices without some hardware can download less."), Status 25% (icon and label,
only when something needs attention or is in progress: `CircleDashed` muted "Draft",
`Download` blue "Downloading 12 of 33",
`CircleAlert` amber "1 waiting for approval", `CircleX` red "1 failed" with the reason in a tooltip
on the icon, muted "Sync off"; otherwise empty), chevron 4%.
Empty state: "No models onboarded yet" with "Onboard model".

**Onboard model dialog** (768 px, two steps). Step 1 "Choose models": vendor `Select`,
`SearchInput`, checkbox list (already onboarded disabled with "Onboarded"), footer "2 selected" and
"3 of 1,126 <vendor> models, list from 2026-10-25 14:02",
Cancel and Continue. The list area has four states from route 3: fetching (`EmptyState`, `Loader`
icon, "Getting <vendor>'s model list", "This only happens the first time, or when the list is more
than 7 days old."; search and Next disabled); ready; stale (amber `CircleAlert` line above the list
with the response's `warning`; onboarding allowed); failed with no list (`EmptyState`, red `CircleX`,
"Couldn't get <vendor>'s model list", the 503 `detail`, `outline` "Try again" with `RefreshCw`; Next
disabled). When the vendor's sync is off, a muted `Info` line under the list says "Sync is off for
<vendor>. Completing onboarding turns it on, so these models get updates." Changing the vendor loads
that vendor's list the same way. Step 2 "Choose Windows releases": checkboxes for the releases the selected
models share, table per model (Model, Offline, Offline + Specialize, Specialize, Skip, Size),
loading line per model while its catalog is read, note "Nothing is downloaded yet. Next, choose
what each component does on the model's page, then complete onboarding.", Back and Onboard.
Onboard creates the models as drafts and navigates to the first one's model page
(`/drivers/models/:id`); with several models, the others are reached from the Models tab, where
they show the status Draft.

## 3. Model page

Header, three rows: breadcrumb "Models > <model>"; the model name (`h2`, one line, truncated) with
the match keys as an outline badge to its right ("Baseboard 8C26", "System SKU 0CB2, 0CB4"; no
vendor badge); then a right-aligned row of `outline` buttons: "Check for updates" (`RefreshCw`;
this model only; disabled on a draft with the tooltip "Available once onboarding is complete", while
the vendor's Sync is off with "Sync is off for HP.", while any run is active with "A sync is running.",
and
until `checkAvailableAt` with the tooltip "Available at 14:32"; on success a toast "Checking
<model> for updates." and the History card gets the run), "Add custom component" (`Plus`), "Roll
back driver set" (`Undo2`; tooltip naming the change, "Devices get driver set 6 instead of 7 from
their next deployment"; disabled without a previous set with the tooltip "No previous driver set
to roll back to") and "Remove model" (`Trash2`). No overflow menu. No set version or build time
(they are on the Storage tab). Banners under the header (icon vertically centred, one per state):
- Draft (`CircleDashed`, shown alone): "Onboarding is not complete. Nothing is downloaded and
  devices of this model get no drivers until you complete it." with "Choose what each component
  does below. Downloading starts when you complete onboarding." and "Complete onboarding"
  (`default`, `Check`). On success the banner is replaced by the Preparing banner.
- Approval: "2 new components are waiting for approval. They are not installed until you approve
  them."
- Waiting changes (`History`): "3 changes are waiting. Driver set 54 is built at 14:32, 2 minutes
  after the last change." with "Devices keep getting driver set 53 until set 54 is built and its
  components are downloaded." and "Build now" (`outline`).
- Preparing (`Download`): "Preparing driver set 54 for Windows 11, version 24H2: 12 of 31
  components downloaded." with "Devices keep getting driver set 53 until it is ready." (or, for a
  first set, "Devices of this model get no drivers until it is ready.").
- Custom component notice (`ArrowRight`): "<vendor> has published a new <vendor component> since
  the custom component was added." with "<componentId>, version <v>, released <date>. Test it, then
  remove the custom component to use <vendor>'s driver.", "Remove custom component" (`outline`) and
  a ghost icon button "Dismiss".
Tiles: Offline, Offline + Specialize, Specialize, Skip, Size (same info icon as the Models tab).
Toolbar: search, Windows release `Select` (230 px, "Windows 11, version 24H2"; scopes the tiles and
the table and sets `?release=`), action filter, category filter, "4 updated by today's sync" toggle.

Component table (fixed widths except Component): chevron 44 px; Component (name; ID and category
below; amber "New" badge while waiting; muted "Withdrawn" badge); Version 120 px; Last updated
110 px; Action 176 px (`Select` with the allowed actions; locked: disabled with `Lock` icon and
tooltip "The vendor's data only allows Skip for this component."; replaced: disabled at Skip);
Source 190 px, in precedence order: "Locked"; "Replaced by custom" (muted); "Custom" (`Package`
icon, violet, tooltip on the icon "Replaces <vendor component> on this model until you remove
it."); "Rolled back" (`Undo2` icon, amber, tooltip on the icon "Rolled back to <version> by <upn>
on <date>, skipping <version>. Moves forward when <vendor> publishes a version newer than
<version>." or "... Stays until the roll back is undone."); "Override" with an undo icon button
"Undo override"; the rule name over "Rule"; "Default". Custom component rows show "Uploaded by
<upn>, <category>" under the name instead of the ID.
Footer: "31 of 40 components". Waiting rows have an "Approve" button (`outline`, `sm`) in the
Source column. Withdrawn rows have "Remove" in the expanded details.

Expanded row: three-column grid of labelled fields, then a bordered Description section with
"Release notes" link. HP: Offline injection, Contains an app, Silent install, Default action,
Released by HP, Release type, Added to this model, Previous version, Download size. Dell:
Installer (64-bit or 32-bit), Hardware match, Package type, Default action, Released by Dell,
Criticality, Added to this model, Previous version, Download size. Custom: Package, Uploaded by,
Uploaded, Replaces, Download size. No install command, SHA-256 or Windows list. Then **Version
history** (heading, "Roll back component" `outline` `Undo2` on the right; "Undo roll back" instead
while rolled back): a read-only table Version, Driver sets (pill "52 to 53", the current range
highlighted), From, To ("Now" for the current), Changed by ("Sync", "Onboarding by <upn>",
"Roll back by <upn>"), Status (icon and label: `CircleCheck` green "Stored", `Download` blue
"Downloads on roll back", `X` red icon with muted text "No longer available from <vendor>").

**Roll back component**: `ConfirmImpactDialog` (icon top-aligned) "Roll back <component>?";
impact "Devices of this model imaged from now on get the version you choose. Other components keep
updating. Running deployments keep the version they started with."; details: Version `Select` of
the earlier versions ("31.0.101.5382 (driver sets 41 to 51)"), loaded from route 34 with
`check=true` (disabled with "Checking <vendor>" until it returns), each option with a muted second
line "Stored", "Downloads 760 MB", or "No longer available from <vendor>" (disabled; the newest
available version is preselected), Until radio group ("<vendor>
publishes a version newer than <current>" with "The component then moves forward to that version
on its own.", default; "I undo the roll back"), and the note "This version is stored, so the next
driver set uses it at once." or, when it is not stored, "This version
is no longer stored, so it is downloaded again from <vendor> (790 MB) and checked against
<vendor>'s checksum before the next driver set uses it."; confirm "Roll back". A 409 shows the
reason in the dialog and keeps it open.

**Roll back driver set**: `ConfirmImpactDialog` "Roll back to driver set 6?"; impact "Devices imaged from now
on get driver set 6. Running deployments keep the set they started with. The next sync does not
reinstate set 7."; confirm "Roll back".

**Add custom component**: form dialog (`Card` `max-w-lg`), title "Add custom component", lead "Use
a driver the vendor has not published yet. It is checked and stored like a vendor component.";
fields: Package (file picker; after upload shows file name, size and "14 INF files found"; help
"For Offline, a ZIP with the driver's INF files. For Specialize, an installer. Drivers must be
signed; unsigned drivers fail on the device."), Name and Version, Action and Category, Silent
command (Specialize only), Models (multi-select of onboarded models; help "All Windows releases of
each model. Offline drivers only install on hardware they match."), Replaces (optional `Select` of
the vendor components the selected models share, plus "Nothing, add alongside"; help "The replaced
vendor component is skipped on these models until you remove this one."); Cancel and "Add
component". Upload progress uses the OS image upload's progress bar; field errors from Core are
shown under their fields. Removing a custom component goes through `ConfirmImpactDialog` "Remove
<name>?" stating which models return to the replaced vendor component.

**Remove model**: `ConfirmImpactDialog` "Remove <model>?"; impact "Devices of this model imaged
from now on get no drivers from Cloud Imaging. Running deployments keep their driver set.";
details list "Deleted: 2 driver sets, 3 overrides", "Freed: 2.1 GB at the next clean-up", "Kept:
rules and sync history"; confirm "Remove", destructive.

## 4. Rules tab

Table: Name, Applies to (All vendors, HP, Dell), Matches ("Name: HP Services Scan*", "Category:
Driver - Enabling", "ID: sp150123"), Action, Hits, Actions (edit, delete). Header info icon:
"A model override wins over rules, and vendor rules win over rules for all vendors. Within a layer
an ID rule wins over a name rule, which wins over a category rule. A component the vendor
withdraws stays at its last version until you remove it from the model." Buttons "Add rule" and
"Suggested rules" (dialog listing suggestions with Add). Rule dialog: Name, Applies to, Match
(Category with a `Select` of known categories, Name pattern with help "Use * as a wildcard.",
Component ID), Action.

**Deployment failure** card below: description "Choose the driver categories whose failure
fails the whole deployment. Other failures complete with warnings."; one setting row per vendor
with a multi-select of its categories; applies on change.

## 5. Sync tab

**Sync** card: description "Downloads new driver versions for onboarded models from the vendors.";
setting rows: Schedule (`Select`: Every day, Selected days, Manually only; for Selected days a repeat `Select`
(Every week, Every 2 weeks, Every 3 weeks, Every 4 weeks; 170 px like the Schedule select) left of the day toggles Mon to Sun,
Monday to Friday every week preselected on switching to it), Time (`Input type="time"`), Time zone (searchable `Select`, 440 px, of the `timeZones` from
`GET sync/settings`, each shown by its Windows display name, "(UTC+01:00) Amsterdam, Berlin, Bern,
Rome, Stockholm, Vienna", in the Windows order; a "Your browser" group with the entry whose `covers` includes the browser's zone, then "All time zones"; default the
browser's); then, after an inset divider, a "Sync now" row (help "Runs the same sync as the
schedule: refreshes the model list of every enabled vendor and checks their onboarded models for
component updates. Last sync started 2026-10-25 02:00."; `outline` button "Sync" with `RefreshCw`, calling route 26; disabled while a run
is active or until `syncNowAvailableAt`, with the time in its tooltip); then, after a full-width
divider, "Newer versions" (text "Use
automatically"), "New components" (`Switch` "Wait for approval"). Changes apply immediately with a
toast; no Save button. Under the controls, aligned with the time, a 16 px muted line shows
`nextRunAt` in the schedule's time zone and the repeat: "Next sync: Monday 2026-10-26 02:00, then
every day.", "... then every week on Monday to Friday." (three or more consecutive days as "X to
Y", otherwise "Monday, Wednesday and Friday"), "... then every 2 weeks on Monday.". Manually only
has no line. The line keeps its height, and "Choose at least one day. The last saved days still
apply." replaces it in red under the day toggles, so neither moves another control or resizes the
card. A sync refreshes each vendor's model list as well as
checking components; Check for updates on the Models tab checks components only.

**Vendors** card: one setting row per vendor: name, line "3 models onboarded. Matched by system
ID." (HP) or "Matched by system SKU." (Dell), `Switch` "Sync". Lenovo row: "Not supported yet.",
switch disabled.

**History** card: rows Started, Result badge (Running, Completed, Partly failed, Failed), Details
("4 updated, 1 failed", "1 new", "No changes", and for a model's check the model name first:
"HP EliteBook 840 G11: 1 updated"; a sync that found new models adds "HP: 3 new models available",
for example "No changes. HP: 3 new models available"); chevron only when there are items, opening one
line per item: Model, Component (link to `/drivers/models/:id?release=...#<lineageKey>`),
Change ("23.70 to 23.80"), Outcome.

## 6. Downloads tab

Section "In progress" with the description "Files queued by completing onboarding, an action
change, a sync or a check for updates. A new driver set reaches devices only when all its
components are downloaded." Table: Model,
Windows (release), Driver set (pill "Set 54"), Progress 240 px (bar plus "12 of 31 components",
no animation), Size right-aligned ("1.1 of 2.5 GB"), Status (icon and label: `Download` blue
"Downloading", `Clock` muted "Waiting", `CircleX` red "1 failed" with the component and reason in a
tooltip on the icon), Started. When nothing is in progress the table keeps its header and shows
`EmptyState` (`CircleCheck`, "No downloads in progress", "Every driver set is ready. Downloads
start after completing onboarding, an action change or a sync."). Section "Finished in the last 7 days":
Model, Windows, Driver set, Components ("4 new, 27 already stored"), Size, Status (`CircleCheck`
"Ready"), Finished. Polls every 15 seconds while anything is in progress.

## 7. Storage tab

Tiles: "Total used" (info icon: "Components shared by several models are stored once, so the
total is less than the sum of the models."), one per vendor. Then a card with one setting row:
"Previous versions", help "Earlier versions of each component kept in storage, so rolling back to
one is instant and does not depend on the vendor. Older versions are downloaded again from the
vendor when someone rolls back to them. Uses 1.4 GB now." (`previousVersionsBytes`: files kept only because of this setting), `Select`
240 px "Keep none", "Keep 1 previous version" (default), "Keep 2 previous versions", "Keep 3
previous versions", calling route 46 on change with a toast; disabled for read-only users. Table: chevron, Model, Vendor,
Windows releases, Driver sets ("2 current, 2 roll-back"), Size. Expanding a model loads route 31
and shows one block per Windows release: heading "Windows 11, version 24H2", pill "Set 7" with
"Current, built 2026-10-22 02:41", pill "Set 6" with "Roll-back, built 2026-10-08 02:37", and on
the right "33 components, 2.3 GB"; then a table Component (name and ID), Version, Size, Driver sets
(pills "7" and "6", "Previous version", or "Next set"), Shared with ("This model only", "3 models"), Status (icon and
label: `CircleCheck` green "Stored", `Download` blue "Downloading", `CircleX` red "Failed" with the
reason in a tooltip on the icon), Downloaded (timestamp, or "Not yet").

## 8. OS Images

`ImageEditorDialog` and the upload dialog get a "Windows release" `Select` (Windows 11, version
25H2 / 24H2 / 23H2; Windows 10, version 22H2; "Not set"), help "Used to pick the driver set for
each device model. Without it no drivers are installed."; the OS Images table shows it.

## 9. Deployment timeline

`lib/imagingProgress.ts`: `InstallDrivers` optional step "Install drivers" between Apply Windows
image and Prepare setup, expected when the session has a driver pin or skip reason. A card in
`SessionProgressDetails.tsx` like the Prepare setup card: "Driver set: HP EliteBook 840 G11,
Windows 11 24H2, set 7" (or the status note when skipped), summary ("26 installed offline", "3
staged for Specialize", "2 skipped, no matching hardware", "1 failed"), failures listed first, Show
details. Item kind label "Driver", outcome texts "Installed", "Installed, app staged", "Staged for
Specialize", "Skipped: no matching hardware", "Failed: <reason>". Completed with warnings uses the
amber marker. When `driverChosenManually` is true, a muted line under the driver set reads
"Chosen manually by <upn>". The item list shows Driver (name, with "Custom", "Rolled back" or the
component ID below), Version (`componentVersion`) and Outcome (icon and label), so the deployment
shows what it installed after its driver set is gone. When any item has `fromUsbCache`, the
summary adds "24 from the USB drive, 2 downloaded" and those items show "From the USB drive" under
their outcome.

## 10. Devices page: driver set per coupled device

Design source: `docs/design/driver-set-selection-options.html`, option A. Lives in the Coupled
Devices card of `pages/SessionsPage.tsx`; the logic goes in a new
`components/drivers/DriverSetCell.tsx` and a `useDriverAssignmentPreview` hook in
`lib/drivers.ts`.

- **Column** "Driver set" (300 px), between Location and Registered, shown only to users who can
  start imaging. Hidden when the preview returns no options and no session matched (nothing
  onboarded).
- **Preview**: fetched when the selected OS image or the coupled session IDs change
  (route 30), debounced 300 ms, previous request aborted. While loading the cells show a
  `Skeleton` of the select's size. On error the column shows a muted "Driver sets could not be
  loaded" with a Retry link, and Start imaging still works (no choices sent).
- **Cell states**:
  - No OS image selected: muted "Choose an OS image first".
  - Image without a release: muted "The OS image has no Windows release".
  - Matched: `Select` size `sm` with the model's display name.
  - No match: `Select` with placeholder "No match, choose a set", amber border, and a
    `CircleAlert` icon (amber, 16 px, `aria-label` with the reason) whose `Tooltip` is the reason
    with the match key: "System ID 8D41 is not onboarded for Windows 11, version 24H2.",
    "System SKU 0CC1 is not onboarded for Windows 11, version 24H2.", "No system ID was read
    from this device.", "This model has no driver set for Windows 11, version 24H2 yet.",
    "This model is onboarded for ARM64, the OS image is x64.".
  - Changed (choice differs from the match): the same `Select` and a ghost icon `Button`
    (`Undo2`, `aria-label` "Use the matched set") that clears the choice. No badge.
- **Options**: grouped per vendor ("HP", "Dell"), the matched model first in its group, each item
  the display name with the size right-aligned in muted `text-xs` (`formatBytes(maxDownloadBytes)`
  from `lib/drivers.ts`); then "No drivers". No set versions. The `Select` primitive has no groups
  or trailing text today: `SelectOption` gains optional `group` (a non-selectable heading row
  rendered when the value changes, skipped by arrow keys and type-ahead, list `role="group"` with
  `aria-label`) and optional `meta` (right-aligned muted text, not matched by type-ahead). Existing
  call sites are unchanged.
- **State**: choices are kept per session ID in page state, cleared when the OS image changes and
  dropped for sessions that leave the table. Only rows that differ from the match are sent.
- **Start imaging overlay**: `StartImagingProfileLines` gets a sibling `StartImagingDriverLines`
  under the label "Driver set:", one line per distinct outcome: "2 devices: HP EliteBook 840/860
  G11", "1 device: no drivers", "1 device: no match", with " (chosen manually)" in
  `font-medium` for manual rows. Not shown while the column is hidden.
- **409 on start**: error toast with the detail, the preview is refetched, and the affected rows
  return to their match.
- **Accessibility**: each `Select` has `aria-label` "Driver set for <serial>"; the alert icon and
  undo button are keyboard reachable; the tooltip text is also the icon's accessible name.

# Feature Specification: OS Driver Management

**Feature Branch**: `005-os-driver-management` (suggested name; no branch created yet)

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "OS driver management for deployments. Administrators onboard device models from supported vendor catalogs (HP first, then Dell), a scheduled catalog sync keeps a resolved driver set per onboarded model and Windows release in the customer's storage, each component gets one action (WinPE, WinPE + Specialize, Specialize, Skip), the Device Gateway pins the resolved set on the session, and the Client installs drivers in a new imaging step between ApplyImage and ConfigureBoot plus a Specialize phase for vendor installers. Source of truth: todo/os-image-driver-injection.md."

## Clarifications

### Session 2026-10-22

- Q: Should a failed storage or network driver fail the deployment? A: Administrators choose centrally, per vendor driver category, which categories fail the whole deployment when one of their components fails. All other failures end the step as Completed with warnings.
- Q: How is the Windows release of an OS image determined? A: The administrator enters it per OS image. It is not read from the image, and there is no fallback to another release.
- Q: How does the device get access to the files of its pinned set? A: One time-limited, read-only link per component, issued with the pinned set, so a retry of one component never depends on another component's access.
- Q: Driver downloads of 2 to 3 GB per HP model may exceed the constitution's 15-minute end-to-end target. A: The 15 minutes covers the base OS image (constitution 1.5.0); drivers carry their own budget, Install drivers under 10 minutes on reference models at 500 Mbit/s (SC-011).
- When two rules of the same layer and match kind conflict, the most recently changed rule wins (agreed).
- A failed download keeps the last verified version and is retried at the next sync (agreed).
- Sync run history is kept for 90 days (agreed).
- A component the vendor withdraws stays in the set until an administrator removes it (changed from "leaves at the next sync").

### Session 2026-10-23

- Q: Who writes `Panther\unattend.xml` and runs Specialize installers? A: Setup Profiles (spec 003). Its Prepare setup step, which runs right after Install drivers, writes the device's single answer file with one setup runner command; the setup runner runs driver installers first, then setup profile scripts. This applies whether or not a setup profile targets the device.

### Session 2026-10-24

- Q: What are the action names? A: Offline, Offline + Specialize, Specialize and Skip, matching the phase names Setup Profiles uses (spec 003). "WinPE" in the action names, the Action column, the summary tiles and the timeline ("26 installed in WinPE") reads "Offline" instead, because a Windows RE boot image runs the same step. Every later mention of the "WinPE" action in this spec means Offline.
- Q: Are the match keys (HP baseboard product ID, Dell system SKU) covered by the device signature? A: No, the same as manufacturer and model today. A device that reports another model's key only receives that model's driver set.
- Q: Can the person starting imaging see and change the driver set a device gets? A: Yes. The Coupled Devices table shows the matched model per device and lets anyone who can start imaging, Technicians included, pick another onboarded model or no drivers, so an incorrect or missing match can be corrected for that deployment. The choice is a model, never a set version: the session always gets the chosen model's current set. A choice is recorded on the session with who made it.
- Q: Should matching fall back to the model name when the match key is not onboarded? A: No. The Coupled Devices table names the device's match key when nothing matches, and the person starting imaging picks the model.

### Session 2026-10-25

- Q: What makes a new driver set? A: Only a change to the included components: a newer version, an action change, an added or removed component, a component roll back, or a custom component. Skipped components do not count, and critical categories are looked up when a session is assigned instead of being stored in the set. Changes made within 2 minutes of each other build one set; Build now builds it at once.
- Q: How is a bad update to one component undone when other components have moved on? A: Per component. Each component keeps its version history for as long as the model is onboarded, and Roll back component holds it at an earlier version, by default until the vendor publishes a version newer than the one that failed. Roll back driver set stays for undoing a whole set.
- Q: What if a fixed driver is not in the vendor's catalog yet? A: An Operator uploads a custom component for one or more models, optionally replacing a vendor component. It stays until removed; the portal says when the vendor publishes a new version of the replaced component, but never claims it contains the same fix.
- Q: Where are stored content and download progress shown? A: The Storage tab lists each model's components per Windows release and driver set, with a Size column instead of Stored and Freed if removed. A new Downloads tab shows downloads in progress and those finished in the last 7 days. The model page header no longer shows the set version and build time.
- Q: Should new driver sets be held for test deployments before production? A: Not in this version (see Assumptions).
- Q: Can drivers come from the cache on the USB drive? A: Yes. The Client keeps every driver file it downloads in the USB drive's CACHE partition, keyed by SHA-256 like OS images, and reuses it on later deployments after checking it again. Media Builder can also preload the current driver sets of chosen models when it prepares a USB drive, for sites with slow or no internet access.
- Q: What happens right after Onboard? A: The models are created as drafts and the first model's page opens. The administrator decides there what each component does and which to skip, then presses Complete onboarding, which starts the downloads. A draft matches no devices and downloads nothing.
- Q: How often does background work run? A: Only the scheduled sync recurs (every day at 02:00 by default, customizable). Downloads and set builds start from the event that needs them. A sync (scheduled, or Sync now on the Sync tab) refreshes each vendor's model list and checks every onboarded model for component updates. Check for updates checks component updates only: on the Models tab for every onboarded model, on a model page for that model alone. A newer version of a component that is not skipped downloads automatically.
- Q: What is the size limit for a custom component? A: 5 GB.
- Q: How does Onboard model get a vendor's model list on a fresh install or for a vendor whose sync is off? A: The dialog fetches the list itself when it is missing or more than 7 days old; vendors add models a few times a year, so a week-old list is current enough. If the catalog cannot be reached, an older list is shown with a warning and onboarding still works; with no list at all the dialog shows the error and Try again. There is no separate refresh button: Sync now refreshes the list at once. A sync that finds new models says so in the History card.
- Q: Can a sync run less often than weekly? A: Yes. Selected days repeats every week, or every 2, 3 or 4 weeks, counted from the week of the first sync after the schedule is saved. Under the schedule controls the Sync card always shows the next sync and how it repeats, so nobody has to work out which week is on; Manually only shows none.
- Q: How many earlier versions of a component stay stored? A: The previous version of each component by default, so rolling back after a bad update is instant and does not depend on the vendor. The Storage tab offers 0 to 3. Older versions are downloaded again from the vendor when rolled back to; the version history says per version whether it is stored, downloads on roll back, or is no longer available from the vendor, which Roll back component checks when it opens.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Onboard an HP model and have its drivers installed during deployment (Priority: P1)

An administrator opens the new Drivers page, onboards an HP model (for example EliteBook 840 G11) for Windows 11, version 24H2, and is taken to the model's page, where the model is a draft. There they review what each component will do, skip what they do not want, and press Complete onboarding; only then are the vendor's files downloaded into the customer's storage. A technician then deploys a matching device: Imaging Core recognises the model and pins the driver set on the session at assignment, and the Client injects the drivers into the applied Windows volume while still in the boot environment. The deployment timeline shows an Install drivers step.

**Why this priority**: This is the whole value chain in its smallest form. Today the installed OS only gets drivers from Windows Update at OOBE, which fails for new hardware and offline scenarios (for example a NIC needed before Entra join or Autopilot). Without this slice nothing else in the feature matters.

**Independent Test**: Onboard one HP model with one release, complete onboarding, deploy a device of that model, and confirm that the vendor's Offline components are present in the installed OS's driver store at first boot and that the timeline shows the Install drivers step with its component list.

**Acceptance Scenarios**:

1. **Given** the HP vendor catalog is available, **When** an administrator opens Onboard model, selects one or more HP models and continues, **Then** step 2 asks for Windows releases and shows a per-model table with counts for Offline, Offline + Specialize, Specialize, Skip and the download size, and nothing has been downloaded yet.
2. **Given** step 2, **When** the administrator presses Onboard, **Then** the models are created as drafts, the page of the first one opens with a banner saying nothing is downloaded until onboarding is completed, its actions can be changed, and devices do not match a draft model.
3. **Given** a draft model, **When** the administrator presses Complete onboarding, **Then** the downloads start at once (shown on the Downloads tab), every included component's original vendor file is stored in the customer's storage account and verified against the vendor's SHA-256, and the model gets a resolved driver set with a version and build time.
4. **Given** a device whose HP baseboard ID is in an onboarded model's system ID list and whose assigned OS image matches an onboarded release, **When** the session is assigned, **Then** the current resolved set is pinned on the session and later policy edits, syncs or roll backs do not change it.
5. **Given** a pinned set, **When** the Client reaches the step after Apply image, **Then** it downloads, verifies, extracts with HP's own extractor and injects each Offline component into the applied volume without allowing unsigned drivers, before Configure boot runs.
6. **Given** a device that matches no onboarded model, **When** the Client reaches the Install drivers step, **Then** the step is skipped with a warning and the deployment continues.
7. **Given** an ARM64 HP model (for example EliteBook Ultra G1q) and an ARM64 OS image, **When** a matching device is deployed, **Then** its components, which HP ships as x64 programs, are staged unextracted and run during the specialize pass, because the ARM64 boot environment cannot run them; an x64 OS image on that device gets no drivers and the step note says the architectures do not match.
8. **Given** a completed deployment, **When** an operator opens its timeline, **Then** an Install drivers step lists the components and their outcomes.

---

### User Story 2 - Decide per component what happens, with defaults, rules and overrides (Priority: P1)

An administrator reviews a model's component table. Each component already has a default action from the vendor's own data. The administrator adds a rule to skip vendor telemetry and service components across all HP models, and overrides one component on one model. Components the vendor data rules out cannot be changed. The Source column shows why each component has its action.

**Why this priority**: The vendor's data alone installs telemetry services and leaves out companion apps. Without control over actions, customers cannot accept the result, so this is part of the minimum viable product.

**Independent Test**: With one onboarded model, create a name rule and a model override, and confirm the resolved set and the Source column reflect the precedence order, and that a locked component stays at Skip.

**Acceptance Scenarios**:

1. **Given** an HP component with `DPBCompliant = true` and no `Uwp` content, **When** no rule or override matches, **Then** its action is Offline and its Source is Default.
2. **Given** a rule "Skip HP Services Scan" matching by name for all HP models, **When** the policy is resolved, **Then** that component is Skip with the rule name shown in Source, and the rule's Hits count includes it.
3. **Given** a rule and a model override that both match a component, **When** the policy is resolved, **Then** the override wins, Source shows Override with an undo button, and undo returns the component to the rule's action.
4. **Given** a component the vendor data allows only Skip for, **When** an administrator opens its Action dropdown, **Then** the dropdown is locked with a lock icon and a tooltip explaining why, and no rule or override can change it.
5. **Given** a rule whose action is not allowed for a matching component, **When** the policy is resolved, **Then** the rule does not apply to that component and the next layer decides.

---

### User Story 3 - Install vendor companion apps and installer-only drivers during Specialize (Priority: P2)

For components such as HP Hotkey Support, Realtek audio or Intel graphics, injecting the INF makes the hardware work but leaves out the vendor's companion app. With the action Offline + Specialize (or Specialize for installer-only components), the Client stages the component on the applied volume and Windows setup runs the vendor's silent installer as SYSTEM during the specialize pass, before OOBE.

**Why this priority**: Required to match the vendor's factory experience, but devices are usable without it. It also depends on the Prepare setup step and setup runner from Setup Profiles (spec 003).

**Independent Test**: Deploy a device whose pinned set contains one Offline + Specialize component and one Specialize component, and confirm after OOBE that the companion apps are installed for all users and a local log lists each component's exit code.

**Acceptance Scenarios**:

1. **Given** a pinned set with Specialize work, **When** the Client finishes the Install drivers step, **Then** the components are handed to the Prepare setup step, which lists them first in the setup manifest and adds the setup runner command to the device's answer file, and the components' staged folders remain on the applied volume.
2. **Given** a pinned set without Specialize work, **When** the step finishes, **Then** no driver entries are added to the setup manifest and all extracted folders are deleted.
3. **Given** the specialize pass runs, **When** each component's staged files match the manifest, **Then** the setup runner runs the vendor's silent installer as SYSTEM with a timeout, exit codes 0 and 3010 count as success, and no restart happens inside specialize.
4. **Given** a staged file that no longer matches the manifest, **When** the setup runner reaches it, **Then** that component is not run, the failure is logged, and the setup runner continues with the next component.
5. **Given** the setup runner has finished, **Then** the staged content is deleted and results remain only in the local log.

---

### User Story 4 - Keep driver sets current with a scheduled, observable sync (Priority: P2)

An administrator sets the sync to run on selected weekdays at 02:00 in their time zone, turns Dell off because no Dell models are onboarded yet, and checks the History card the next morning: one run with "4 updated, 1 failed", expandable to one line per change.

**Why this priority**: Without a recurring sync, driver sets go stale. Administrators also need to see what changed and what failed. A manual run covers the first deployments, so this ranks below the P1 slices.

**Independent Test**: Configure a schedule, wait for (or simulate) the slot, and verify that exactly one run starts, that it only reads enabled vendors, and that the history shows a correct result and per-item changes. Then press Sync now twice and confirm the second press is refused with the time it becomes available.

**Acceptance Scenarios**:

1. **Given** a schedule of Selected days (Mon, Wed, Fri) at 02:00 Europe/Stockholm, **When** Wednesday 02:00 local time passes, **Then** exactly one run starts for that slot and none starts on Tuesday.
2. **Given** an administrator changes the schedule, **When** the change is made, **Then** it applies immediately without a Save button and without restarting any service.
3. **Given** a run started 30 seconds ago, **When** anyone presses Sync now or Check for updates on the Models tab, or calls the API directly, **Then** the request is refused and the response states when it becomes available (1 minute after the last run started).
4. **Given** Dell sync is off, **When** a run starts, **Then** no Dell catalog is read and Dell models keep their current set, which devices continue to receive; their rows show a muted "Sync off" badge.
5. **Given** the HP catalog host is unreachable during a run, **When** the run finishes, **Then** Dell changes are still applied and the run result is Partly failed with a failed item naming the HP reason.
6. **Given** a run where only a component waiting for approval was added, **When** it finishes, **Then** the result is Completed and the summary reads "1 new".
7. **Given** HP publishes a newer audio driver during the day, **When** an Operator presses Check for updates on the model page, **Then** only that model's catalog is read, the newer version downloads at once (the Downloads tab shows it), a newer version of a skipped component is not downloaded, the History card records a run for that model, and a second press within 15 minutes is refused with the time it becomes available.
8. **Given** it is Sunday 2026-10-25, **When** an administrator sets Selected days, Monday, every 2 weeks at 02:00, **Then** the Sync card reads "Next sync: Monday 2026-10-26 02:00, then every 2 weeks on Monday.", and scheduled runs start on 2026-10-26 and 2026-11-09 but not on 2026-11-02.

---

### User Story 5 - Approve new components before they reach devices (Priority: P2)

With "New components: Wait for approval" on, a sync adds a component that does not continue any known one (for example a new HP utility). It appears with an amber New badge and a banner on the model page. The administrator checks its default action, changes it if needed, and presses Approve.

**Why this priority**: Stops unexpected software reaching devices automatically, which is the main trust concern with vendor catalogs. Newer versions of known components still flow without review, so routine updates are not blocked.

**Independent Test**: Turn on the setting, introduce a new component and a newer version of a known component in a sync, and verify only the new component is held out of the resolved set until approved, and that Approve keeps its current action, including Skip.

**Acceptance Scenarios**:

1. **Given** the setting is on and a sync adds a new component, **When** the set is resolved, **Then** the component is excluded from the resolved set, shown with a New badge, and counted in the model's Status badge and the page banner.
2. **Given** a component waiting for approval with action Skip, **When** the administrator presses Approve, **Then** the component is confirmed as Skip and no separate Skip button exists.
3. **Given** a newer version of a known component (HP `Supersedes`, Dell hardware component ID), **When** a sync finds it, **Then** it replaces the old version automatically, keeps the old version's action, and never waits for approval.
4. **Given** the setting is off, **When** a sync adds a new component, **Then** the component enters the resolved set with its resolved action and the run history records it as new.

---

### User Story 6 - Dell models with hardware-matched downloads (Priority: P3)

An administrator onboards Dell Latitude 5450 (system SKUs 0CB2 and 0CB4) as one model. Defaults follow Dell's data: 64-bit drivers Offline, drivers bundling an app Offline + Specialize, 32-bit drivers Specialize, applications skipped. A device without mobile broadband or an NVIDIA GPU does not download those components.

**Why this priority**: Second vendor, same policy and phases. HP delivers the value first.

**Independent Test**: Onboard one Dell model, deploy a device of one of its SKUs without optional hardware, and confirm that only matching components were downloaded and that 32-bit packages were never run in the boot environment.

**Acceptance Scenarios**:

1. **Given** the Dell catalog index, **When** an administrator onboards a Dell model, **Then** one onboarded model covers all of that model name's system SKUs, each with its own component catalog.
2. **Given** a Dell per-SKU catalog with the full release history, **When** the set is resolved, **Then** only the newest package per lineage (hardware component ID, falling back to the name) is used, and components that do not list the target Windows version are dropped.
3. **Given** a device whose PCI hardware does not match a component's published hardware IDs, **When** the Client runs the Install drivers step, **Then** that component is not downloaded and is reported as skipped by hardware match.
4. **Given** a 32-bit Dell package, **When** its allowed actions are shown, **Then** Offline and Offline + Specialize are not offered.
5. **Given** a Dell device, **When** the Client collects hardware data, **Then** the system SKU is included in the signed hardware payload and Imaging Core matches on it.
6. **Given** a Dell ARM64 model (for example Latitude 7455), **When** it is onboarded and a device of it is deployed with an ARM64 OS image, **Then** its ARM64 driver packages follow the same defaults as 64-bit packages, run from the ARM64 boot environment, and an x64 OS image on that device gets no drivers with the note that the architectures do not match.

---

### User Story 7 - Understand storage, roll back and remove models (Priority: P3)

An administrator checks the Storage tab to see total and per-vendor usage, rolls a model back to its previous driver set after a bad vendor update, and removes a model no longer deployed after reviewing an impact overlay that shows the space freed.

**Why this priority**: Operational hygiene. Clean-up is automatic, so the feature works without this story, but administrators need to see costs, recover from bad updates and retire models.

**Independent Test**: With two models sharing a component, verify the Storage tab counts it once, roll one model back and confirm new sessions get the previous set, then remove a model and confirm the overlay's freed space matches what the next clean-up deletes.

**Acceptance Scenarios**:

1. **Given** two models sharing a component version, **When** the Storage tab loads, **Then** total used counts the file once, each model's Size counts it once, and expanding a model shows the component under each Windows release that uses it with "Shared with 2 models".
2. **Given** a model with two Windows releases, each with a current and a roll-back set, **When** its Storage row is expanded, **Then** each release has a heading naming both sets with their build times, and each component shows which of those sets use it.
3. **Given** a model with a previous set, **When** an administrator presses Roll back driver set, **Then** new sessions get the previous set, running sessions keep their pinned set, and the next sync does not automatically reinstate the set that was rolled back from.
4. **Given** an administrator presses Remove model, **When** the impact overlay opens, **Then** it states what devices will experience, what is deleted, the space freed, and what is kept, and nothing changes until the administrator confirms.
5. **Given** a removal is confirmed, **Then** new deployments stop getting the model's sets at once, running deployments keep their pinned set, sets and overrides are deleted, rules and sync history are kept, and unreferenced files go in the next clean-up.
6. **Given** a sync finishes, **When** a stored file is referenced by no current set, roll-back set or running deployment, **Then** it is deleted without any manual action.
7. **Given** onboarding, a policy change or a sync has queued downloads, **When** the Downloads tab opens, **Then** each driver set being prepared shows its progress, and sets that became ready in the last 7 days are listed below.

---

### User Story 8 - Check and correct the driver set before imaging starts (Priority: P2)

A technician couples four devices and selects an OS image. The Coupled Devices table now shows a Driver set column: three devices show their matched model, one HP EliteBook 840 G12 shows "No match, choose a set" because its system ID is not onboarded. The technician picks the G11 model for it, sets another device to No drivers, and starts imaging. The confirmation overlay lists the driver set per device with the manual choices called out, and each deployment timeline names who chose its set.

**Why this priority**: Matching is automatic and correct for onboarded models, so deployments work without this story. It removes the blind spot where a wrong or missing match is only found after imaging, and lets a technician fix it without an Operator.

**Independent Test**: With two onboarded models, couple one matching and one unmatched device, select an OS image, choose a model for the unmatched device and No drivers for the matched one, start imaging, and confirm each session pins what was chosen, with the chooser on the timeline.

**Acceptance Scenarios**:

1. **Given** coupled devices and a selected OS image with a Windows release, **When** the Coupled Devices table loads, **Then** each device shows the model it matches, preselected, or "No match, choose a set" with the reason naming the device's match key.
2. **Given** a device's Driver set dropdown, **When** it opens, **Then** it lists every onboarded model with a current set for the image's Windows release and architecture, grouped by vendor with the matched model first, each with its download size, plus No drivers; set versions are not shown.
3. **Given** a technician picks another model or No drivers, **When** imaging starts, **Then** the session pins the chosen model's current set (or no set), records the choice and the technician, and the timeline card says who chose it.
4. **Given** a changed row, **When** the technician presses undo, **Then** the row returns to the matched model (or to no match).
5. **Given** the chosen model is removed or loses its set for the release before imaging starts, **When** the technician starts imaging, **Then** nothing is assigned and the error names the affected devices and asks to choose again.
6. **Given** no model is onboarded, or no OS image is selected, or the image has no Windows release, **When** the table loads, **Then** the column is hidden, shows "Choose an OS image first", or shows "The OS image has no Windows release" respectively.

---

### User Story 9 - Roll back one component without undoing the others (Priority: P2)

A model has been onboarded for two years and has had 50 driver set updates. Test deployments show that last week's Intel Graphics Driver update breaks external displays, while the Wi-Fi and audio updates since then are needed. The Operator opens the component, sees its version history, and rolls it back to the previous version until HP publishes a newer one. Every other component keeps updating.

**Why this priority**: Roll back driver set only goes back one set and undoes every other update with it. Without a per-component roll back, a bad update found late cannot be undone at all.

**Independent Test**: With a model whose current set has a newer version of two components, roll back one of them to a version that is no longer stored, and confirm the next set contains the older version of that component (downloaded again and verified) and the newer version of the other, and that the component moves forward when a later sync finds a version newer than the skipped one.

**Acceptance Scenarios**:

1. **Given** a component on a model page, **When** its row is expanded, **Then** a Version history lists every version the model has used for the selected Windows release with its driver sets, from and to dates, what changed it, and whether its file is stored, downloads on roll back, or is no longer available from the vendor.
2. **Given** Roll back component with a chosen earlier version and "Until the vendor publishes a newer version", **When** confirmed, **Then** the next driver set uses that version, the Source column shows Rolled back with who and when, and the other components are unchanged.
3. **Given** the chosen version's file is no longer stored, **When** the roll back is confirmed, **Then** the file is downloaded again from the vendor and verified before a set uses it; if the vendor no longer hosts it, the roll back fails with that reason and nothing changes.
4. **Given** a component rolled back until the vendor publishes a newer version, **When** a sync finds a version newer than the one it skipped, **Then** the component moves forward to it and the roll back ends.
5. **Given** a component rolled back until undone, **When** an Operator undoes the roll back, **Then** the next set uses the component's newest version.
6. **Given** the default of one previous version, **When** a sync replaces Intel Graphics Driver 31.0.101.5382 with 31.0.101.5592 and the clean-up runs, **Then** 31.0.101.5382 is still stored and a roll back to it downloads nothing.
7. **Given** a version that is not stored and that HP no longer hosts, **When** an Operator opens Roll back component, **Then** that version shows "No longer available from HP" and cannot be chosen, and the version history shows the same status.

---

### User Story 10 - Use a driver the vendor has not published yet (Priority: P3)

Intel releases a Wi-Fi driver that fixes disconnects, but HP has not yet packaged it. The Operator uploads Intel's driver as a custom component for the two affected HP models, replacing HP's Intel Wireless LAN Driver. Months later HP publishes a new Intel Wireless LAN Driver; the model page says so, the Operator tests it, and removes the custom component.

**Why this priority**: Covers a gap in the vendor catalogs that otherwise forces customers back to their own driver tooling. Rarer than the stories above.

**Independent Test**: Upload a signed driver ZIP as a custom component replacing a vendor component on one model, deploy a device of that model, confirm the custom driver is in the driver store and the replaced component was not installed, then simulate a newer vendor version and confirm the banner appears and removing the custom component restores the vendor component.

**Acceptance Scenarios**:

1. **Given** Add custom component with a ZIP containing INF files, a name, a version, an action, a category and one or more models, **When** saved, **Then** the component is stored with its SHA-256, appears on each model with Source Custom, and enters the next driver set of each model and Windows release.
2. **Given** the custom component replaces a vendor component, **Then** that vendor component shows Replaced by custom, its action is locked to Skip, and it returns to its own action when the custom component is removed.
3. **Given** a ZIP without INF files for an Offline action, or a file over the size limit, **When** uploading, **Then** the upload is refused with the reason.
4. **Given** a replaced vendor component, **When** a sync finds a new version of it, **Then** the model page shows a banner naming the vendor's new version and release date with Remove custom component; dismissing it is recorded.

---

### User Story 11 - Reuse and preload drivers on the USB drive (Priority: P2)

A technician images twenty EliteBook 840 G11 devices at a branch office with a slow internet connection, using the same USB drive. The first deployment downloads the 2.5 GB driver set and keeps it in the USB drive's cache; the next nineteen take the drivers from the USB drive and only download components that changed. Before travelling to a site without internet access, the technician prepares a USB drive in Media Builder with the driver sets of the two models used there already on it.

**Why this priority**: Driver payloads of 2 to 3 GB per model are the largest download after the OS image, which is already cached the same way. Without the cache every deployment downloads them again.

**Independent Test**: Image two devices of one model with the same USB drive and confirm the second deployment downloads no driver files and its timeline shows the components as taken from the USB drive; then prepare a USB drive in Media Builder with one model's driver set and confirm the first deployment of that model downloads nothing.

**Acceptance Scenarios**:

1. **Given** a USB drive with a CACHE partition, **When** the Client downloads a component file, **Then** it keeps the verified file in the cache under its SHA-256 and the next deployment that needs the same file uses it without downloading.
2. **Given** a cached file, **When** the Client uses it, **Then** it checks the file's SHA-256 again before running anything from it; a file that no longer matches is deleted and downloaded again.
3. **Given** the cache partition is full or missing (ISO, network boot), **When** the step runs, **Then** components are downloaded to the Windows volume as before and the deployment is unaffected.
4. **Given** Media Builder preparing a USB drive with two models selected under "Driver sets", **When** preparation finishes, **Then** the CACHE partition holds every file of those models' current driver sets for the boot image's architecture, verified, and the first deployment of those models downloads nothing.
5. **Given** the selected driver sets do not fit next to the space reserved for OS images, **When** the technician selects them, **Then** Media Builder shows the size and the space left before preparing, and refuses to start when they do not fit.
6. **Given** a completed deployment that used cached components, **When** its timeline is opened, **Then** the Install drivers card says how many components came from the USB drive and how many were downloaded.

---

### Edge Cases

- One HP catalog entry lists several system IDs, or several models share one ID (EliteBook 840 and 860 G11 share `8C26`): matching works on any ID in the list, and an ID can belong to only one onboarded model.
- An HP component's `SSMCompliant` flag and its `SilentInstall` text disagree (free text such as `None`, `NA`, `Do not support`): the component is treated as not installable.
- TPM firmware filed under a software category: excluded as firmware because the name contains "Firmware".
- A Dell driver was renamed between releases (the Intel Wi-Fi driver has had four names): the hardware component ID keeps the lineage, so the change is a newer version, not a new component.
- A dock driver with hardware match: a dock only matches when it is connected during imaging, so undocked devices skip it.
- A rule action is not allowed for a matched component: the rule is ignored for that component and the next layer decides.
- A component the vendor no longer publishes: it stays in the resolved set at its last verified version, is marked Withdrawn on the model page, and the change is recorded in the run history. Only an administrator removes it.
- A download fails or its SHA-256 does not match: the file is never published, the item is recorded as failed, the model keeps the component's last verified version (or the component is left out if none exists), and the next run retries.
- The previous set is rolled back to and the next sync produces the same fingerprint as the set rolled back from: no new set is created, so the roll back holds.
- A model or release is removed while sessions are running: those sessions keep their pinned set and its files until they end.
- The session's OS image release has no onboarded release on the matched model: the step is skipped with a warning (see FR-044 for the fallback question).
- Sync now or Check for updates on the Models tab pressed during a scheduled run, or within 1 minute of the last sync or all-models check: refused by the API with the time it becomes available.
- A scheduled slot passes while a run is still in progress: no second run starts for that slot.
- A vendor is turned off while a run is reading its catalog: the in-progress run finishes; the next run skips that vendor.
- Onboarding a model of a vendor that is off: the vendor is turned on.
- The applied volume lacks space for extraction: the affected components fail with a clear reason; extraction never falls back to the boot environment's RAM disk.
- A device restarts or loses power during the Install drivers step: the existing session failure and retry behaviour applies; a partial driver store is replaced when the image is applied again.
- The specialize installer hangs: the setup runner stops it at the timeout, logs it, and continues.
- A sync builds a new set for a model between the Coupled Devices table loading and imaging starting: the session pins the set that is current when imaging starts, chosen or matched.
- Two technicians start imaging for the same devices with different choices: the existing assignment state check lets only the first start; the second skips those sessions.
- A device is given another vendor's model: allowed. INF drivers only bind to hardware they match; the overlay calls out the manual choice so a wrong pick is caught before the disk is wiped.
- An Operator makes several policy changes in a row: one driver set is built 2 minutes after the last change (or at once with Build now); devices keep the current set meanwhile.
- A component is rolled back to a version the vendor no longer hosts: the roll back fails with that reason and the component is unchanged.
- The vendor withdraws a component that a custom component replaces: the custom component stays; the withdrawn component is removed as usual.
- A custom component is removed while deployments that use it are running: they keep their pinned set and its files until they end.
- Two custom components replace the same vendor component on one model: refused; one replacement per vendor component per model.
- A preloaded driver set is replaced by a newer set before the USB drive is used: unchanged components still come from the USB drive (files are matched by SHA-256, not by set); only changed components are downloaded.
- The USB drive's cache fills up during the step: the remaining components are downloaded to the Windows volume and not cached; nothing already cached is evicted mid-deployment.
- A cached driver file is corrupt: it fails the SHA-256 check, is deleted from the cache and downloaded again; it never reaches extraction.
- A device matches a model whose onboarding is not completed: it is treated as matching no model, and the Coupled Devices tooltip and the step note say that the model is a draft.
- A scheduled sync finds a newer version for a draft model: the draft's component list shows the newer version, and nothing is downloaded until onboarding is completed.

## Requirements *(mandatory)*

### Functional Requirements

#### Scope and vendors

- **FR-001**: The system MUST support HP and Dell business client catalogs as driver sources. Catalog reading, device match key, component classification, version lineage and where unpacking happens MUST be isolated per vendor, so that adding a vendor later (Lenovo) does not change policy, sync, storage, matching or the Client phases.
- **FR-002**: Lenovo MUST be shown as an unsupported vendor (listed with a disabled Sync switch) and MUST NOT be onboardable. Microsoft Surface and any vendor without a machine-readable catalog MUST NOT be supported; the system MUST NOT scrape vendor web sites.
- **FR-003**: BIOS, system firmware, TPM firmware, dock firmware and SSD firmware MUST be excluded entirely: never downloaded, never listed, never matched by rules. Exclusion MUST use the vendor's category or type (HP `BIOS*`, `Firmware`, dock firmware; Dell `FRMW`, `BIOS`) and MUST also exclude any component whose name contains "Firmware".
- **FR-004**: OS images MUST remain unchanged by this feature; drivers MUST NOT be added to the stored OS image.

#### Model catalog and onboarding

- **FR-005**: For each enabled vendor, a sync (scheduled or Sync now) MUST maintain the list of models the vendor publishes; Check for updates does not refresh it, and the Onboard model dialog fetches it when needed (FR-101). HP's list comes from its platform list (system ID, product names, and the architecture per OS release); Dell from its catalog index (model name, system SKUs, one component catalog per SKU, with its SHA-256). x64 and ARM64 models MUST both be supported; each onboarded model has one architecture, and a driver set is only used for a device when the OS image's architecture matches the model's.
- **FR-006**: Administrators MUST be able to onboard models in a two-step dialog. Step 1: multi-select from a vendor's model list with a vendor dropdown and search; already onboarded models are shown disabled. Step 2: choose Windows releases and review a per-model table (Offline, Offline + Specialize, Specialize, Skip, Size). Onboard MUST create the models as drafts and open the page of the first one.
- **FR-101**: When step 1 of the Onboard model dialog opens a vendor whose stored model list is missing or more than 7 days old, Imaging Core MUST fetch that vendor's model list itself, whether the vendor's sync is on or off; the fetch reads only the model list and downloads no drivers, is not a run, and does not count for the run limits. The dialog MUST show, in place of the model list: while fetching, "Getting <vendor>'s model list"; when ready, the models with "list from <timestamp>" in the footer; when the catalog cannot be reached but an older list is stored, that list with the warning "Showing the model list from <timestamp>. Can't reach catalog at <catalog URL>." and onboarding still allowed; when the catalog cannot be reached and no list is stored, "Couldn't get <vendor>'s model list" with "Can't reach catalog at <catalog URL>. Make sure Imaging Core can reach it, then try again." and a Try again button, with Next disabled. A list fetched within 7 days MUST be used without fetching.
- **FR-007**: An onboarded model MUST record vendor, display name, match keys and Windows releases. HP: one onboarded model per system ID set. Dell: one onboarded model per model name, covering all its system SKUs. A match key MUST NOT belong to more than one onboarded model.
- **FR-008**: Completing onboarding of a model of a vendor whose sync is off MUST turn that vendor's sync on.
- **FR-099**: A draft model MUST NOT match devices, MUST NOT be offered when a model is chosen at imaging start, and MUST NOT download anything. Its page MUST show the components with their resolved actions, allow overrides, release changes and removal, and show a banner stating that nothing is downloaded until onboarding is completed, with a Complete onboarding button. Complete onboarding MUST start the downloads of the included components at once and build the set when they are stored; the model is then active. This work MUST run as a sync run for that model (trigger "Complete onboarding", no time limit), so the Sync tab shows its progress and History records its result and any catalog failure; when another run is active, the onboarding is queued: the running sync's card lists it under "Up next" with who completed it and when, the model page says it waits for the running sync, and queued onboardings start as their own runs, oldest first, as soon as the active run finishes. The Models tab MUST show a draft model with the status Draft. A draft MUST NOT expire on its own.
- **FR-009**: Administrators MUST be able to add and remove Windows releases of an onboarded model.
- **FR-010**: Devices that match no onboarded model, or whose OS image release is not onboarded for the matched model, MUST receive no driver work unless a model is chosen for them when imaging starts (FR-080); the Install drivers step is skipped with a warning that names the device's match key when nothing matched.

#### Offboarding

- **FR-011**: Administrators MUST be able to remove a model or one Windows release of it. The removal MUST take effect immediately for new deployments; running deployments keep their pinned set. The removed model's (or release's) sets and overrides MUST be deleted; rules and sync history MUST be kept; its stored files MUST be deleted in the next clean-up unless another set refers to them. Onboarding the model again MUST start from the current catalog with no previous overrides.
- **FR-012**: Removal MUST be confirmed through the portal's impact overlay stating what devices will experience, what is deleted, the space freed, and what is kept.

#### Actions and classification

- **FR-013**: Every component MUST have exactly one action:
  - **Offline**: INF content injected into the applied Windows volume from the boot environment, before Windows starts.
  - **Offline + Specialize**: as Offline, plus the vendor's silent installer during the specialize pass to add the companion app. The hardware works even if the specialize run fails.
  - **Specialize**: only the vendor's silent installer during the specialize pass.
  - **Skip**: not downloaded and not installed.
- **FR-014**: Each component MUST have a set of allowed actions derived from vendor data. When vendor data allows only Skip, the component MUST be locked to Skip and no rule or override can change it.
- **FR-015**: HP "installable" MUST mean `SSMCompliant = true` and `SilentInstall` is not `None`, `NA`, empty, or text containing "not support".
- **FR-016**: HP defaults and allowed changes MUST be:

  | Vendor data | Default | May change to |
  |---|---|---|
  | `DPBCompliant = true`, no `Uwp` content | Offline | Skip |
  | `DPBCompliant = true`, `ContentTypes = Uwp` | Offline + Specialize | Offline, Skip |
  | `DPBCompliant = true`, `ContentTypes = Uwp`, not installable | Offline (drivers only, the app is not installed) | Skip |
  | `Software - *`, `DPBCompliant = false`, installable | Skip | Specialize |
  | `Driver - *`, `DPBCompliant = false`, installable | Specialize | Skip |
  | Not installable | Skip (locked) | |
  | `Diagnostic`, `Manageability*`, `OS`, `Utility` with `DPBCompliant = false` | Skip (locked) | |

  On HP ARM64 models Offline and Offline + Specialize MUST NOT be allowed, because HP's packages are x64 programs that the ARM64 boot environment cannot run. Defaults that would be either become Specialize, and Specialize runs the vendor package during the specialize pass.

- **FR-017**: Dell defaults MUST be: 64-bit driver package (`DRVR`, `LW64`, including ARM64 packages) Offline; driver that bundles an app ("Driver and Application" in the name, or an app-type device entry) Offline + Specialize; 32-bit driver package (`LWXP`) Specialize; application (`APAC`) Skip, changeable to Specialize. 32-bit packages MUST NOT be offered Offline or Offline + Specialize.
- **FR-018**: Version lineage MUST be vendor-specific: HP via the `Supersedes` attribute; Dell via the hardware component ID, falling back to the component name when absent. For Dell, only the newest package per lineage is used, and components that do not list the target Windows version (for example Windows 10 only) MUST be dropped.

#### Policy: rules and overrides

- **FR-019**: Administrators MUST be able to create, edit and delete rules with a name, a scope (all vendors or one vendor), a match kind (category, name pattern, or component ID) and an action. The Rules tab MUST show each rule's number of matched components (Hits).
- **FR-020**: Administrators MUST be able to override a component's action on one model and undo the override.
- **FR-021**: Precedence MUST be: model override, then vendor-scoped rules, then rules for all vendors, then the vendor default. Within one layer an ID rule beats a name rule, which beats a category rule. The most specific match wins.
- **FR-022**: A rule or override whose action is not allowed for a matched component MUST NOT apply to that component; the next layer decides.
- **FR-023**: The system MUST offer suggested Skip rules (for example HP Services Scan for Commercial, Seamless Firmware Update Service, HP App) that an administrator can add. Suggestions MUST never be applied without an administrator adding them.
- **FR-024**: A policy change MUST produce a new resolved set for each affected model and release. Changes made within 2 minutes of each other MUST produce one set, built 2 minutes after the last change; Build now on the model page builds it at once, and the model page MUST say when the set will be built. Components that need a file not yet stored MUST be downloaded and verified before the new set becomes current; until then devices continue to receive the current set.

#### New components and newer versions

- **FR-025**: A newer version of a known component MUST keep the component's action and MUST never wait for approval. With "Newer versions: Use automatically" (the default) it MUST be used as soon as its set is ready; with "Wait for testing" its set MUST wait in testing (FR-103).
- **FR-026**: A component that does not continue a known lineage MUST be flagged as New on its model. When "New components: Wait for approval" is on, it MUST be held out of the resolved set until an administrator approves it. Approve MUST confirm the component's current action, Skip included; there is no separate Skip action. When the setting is off, new components MUST enter the resolved set with their resolved action.
- **FR-027**: A component waiting for approval MUST NOT affect a sync run's result.
- **FR-103**: The Sync tab MUST offer "Newer versions: Use automatically / Wait for testing", stored server-side and recorded in the change log. With Wait for testing, every new driver set (a new model's first set included) MUST, once ready, be held in testing instead of becoming current: devices keep the current set, or get no drivers when there is none. A newer set that arrives while one is in testing MUST replace it; a set equal to the current set MUST clear testing. Switching back to Use automatically MUST release every set in testing, each logged as a release by the user who switched; when any set is in testing, the portal MUST first confirm the switch, saying how many sets it releases.
- **FR-104**: Only Operators and Administrators MUST be able to choose a set in testing, per device in the Coupled Devices Driver set dropdown, shown as "<model> (testing)" after the model's current set. The portal server MUST refuse such a choice from anyone else with 403, and imaging start MUST refuse it when the model no longer has a set in testing for the image's release. A device whose model's only set is in testing MUST tell Operators and Administrators so and to choose "<model> (testing)", with that option first; other users see that the model has no driver set yet.
- **FR-105**: The model page MUST show a banner while a set is in testing, naming the set and what other devices get, with a Release action for Operators and Administrators. Release MUST make the set in testing current without a confirmation, keep the previous current set as the roll-back set, record its component versions in their version history, and be logged. The Models tab MUST show In testing as the model's status.
- **FR-106**: A set in testing MUST count as in use for storage retention, removal impact and the storage page, where the model row counts it ("2 current, 2 roll-back, 1 in testing") and the release block shows it as "Set 8 In testing, built <time>".

#### Resolved driver sets

- **FR-028**: For each onboarded model and release, the system MUST maintain a resolved driver set (component, version, action, stored file) with a fingerprint, a version and a build time. The set and its fingerprint MUST contain only included components (action other than Skip), with custom components in place of the vendor components they replace and rolled-back components at their held version. A new set MUST be created only when the fingerprint changes, so a new version of a skipped component or a change of critical categories creates no set.
- **FR-029**: The system MUST keep the current set and the previous set (roll-back set) per model and release.
- **FR-030**: Administrators MUST be able to roll back to the previous set. Roll back affects new deployments only. A sync MUST NOT automatically reinstate the set that was rolled back from; only a set with a different fingerprint, or an administrator action, replaces the rolled-back set.
- **FR-031**: A set MUST become current only when every file it refers to is stored and verified. A component whose download or verification fails MUST keep its last verified version in the new set, or be left out if none exists, and MUST be retried at the next run.
- **FR-078**: A component the vendor withdraws from its catalog MUST stay in the resolved set at its last verified version, MUST show a muted Withdrawn badge next to its name, and MUST leave the set only when an administrator removes it from the model. If the vendor publishes the lineage again, the component continues as a newer version and the Withdrawn badge clears.

#### Catalog sync

- **FR-032**: Administrators MUST be able to set the sync schedule to every day, selected weekdays, or manually only, at a chosen time in a chosen time zone. The default MUST be every day at 02:00 in the browser's time zone. Time zones MUST be offered the way Windows presents them: one entry per Windows time zone, labelled with the Windows display name ("(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna", "(UTC) Coordinated Universal Time") and in the Windows order, searchable, with the entry for the browser's zone first. Each entry MUST store an official IANA Time Zone Database name, which the schedule uses. The list MUST be stored in the product, and only entries whose IANA name the server can resolve MUST be offered or accepted. Selected days with no day chosen MUST be refused, and the previous days keep applying. Selected days MUST repeat every week (the default), every 2, 3 or 4 weeks, counted from the week of the first sync after the schedule is saved. For every schedule except manually only, the Sync card MUST show the next sync and how it repeats ("Next sync: Monday 2026-10-26 02:00, then every week on Monday to Friday."), in the schedule's time zone, in a line that keeps its height so the day error can take its place without moving other controls. The schedule MUST be stored server-side and MUST take effect without a restart or redeployment.
- **FR-033**: A scheduled run MUST start at most once per scheduled slot, and only one run MUST be active at a time. The scheduled sync MUST be the only recurring background work: downloads and set builds MUST start from the event that needs them (completing onboarding, a run or check that finds a newer version, an action change, a roll back, a custom component, Build now), not from a polling interval.
- **FR-100**: The model page MUST offer Check for updates, which reads the vendor catalog for that model only and applies what it finds like a run limited to the model: newer versions of components that are not skipped download automatically, and new components follow FR-026. It MUST be recorded in the History card as a run for that model, refused while any run is in progress (FR-033) and for 1 minute after the model's last check, with the response stating when it becomes available. It MUST be disabled on a draft model until onboarding is completed, and refused and disabled while the model's vendor has Sync off (FR-035), with the reason "Sync is off for <vendor>.".
- **FR-034**: Sync now on the Sync tab MUST start the same run as a scheduled one: refresh the model list of every vendor that is on (FR-005), then check every onboarded model of those vendors for component updates. Check for updates on the Models tab MUST start a run that checks every onboarded model for component updates without refreshing the model lists. Both MUST be refused while a run is in progress and for 1 minute after the last sync (scheduled or Sync now) or all-models check started; the two buttons share this limit. The refusal MUST be enforced by the API, and the response MUST state when the button becomes available; the portal MUST disable the button while a run is in progress and until that time, show the time in its tooltip, and enable it again by itself when the time passes. A run that finishes within 3 seconds of its card appearing MUST keep its stages on screen, filled, until 3 seconds have passed before its result is shown.
- **FR-035**: Each supported vendor MUST have a stored Sync on/off setting. A run MUST read catalogs only for vendors that are on, and only for their onboarded models. Turning a vendor off MUST pause its updates only: its models keep their current set and devices keep receiving it.
- **FR-036**: Catalogs MUST be processed per vendor, so one vendor's outage does not block another vendor's changes.
- **FR-037**: Each run MUST record its start time, a result and one item per change (model, component, old and new version, outcome: updated, new and waiting for approval, new, removed, or failed with reason). Results: **Running** while active; **Completed** when no item failed; **Partly failed** when some items or vendors failed and others succeeded; **Failed** when nothing could be processed.

#### Download, integrity and storage

- **FR-038**: Each included component MUST be stored as the vendor's original, unmodified file in the customer's storage account. Files MUST NOT be repackaged or pruned by file type.
- **FR-039**: Every stored file MUST be verified against the vendor's published SHA-256 over its full content. A file that fails verification MUST NOT be published or referenced by any set.
- **FR-040**: Download locations taken from vendor catalogs MUST be restricted to the vendor's official HTTPS download hosts.
- **FR-041**: Files MUST be shared by reference: one stored file per vendor component version, used by every set that includes it.
- **FR-042**: At the end of every scheduled sync, Sync now and Check for updates on the Models tab (not a check of one model), the system MUST delete stored files that no current set, roll-back set, pending set, component roll back, custom component, kept previous version (FR-102) or running deployment refers to; a lower Previous versions setting takes effect at the next of these. There MUST be no manual clean-up action.

#### Device matching and assignment

- **FR-043**: The Client MUST include the device's HP baseboard product ID (already collected) and Dell system SKU (newly collected) in its registration payload.
- **FR-044**: Administrators MUST enter the Windows release of each OS image when uploading or editing it on the OS Images page, chosen from a list that names the Windows version too (for example "Windows 11, version 24H2"). The release is not read from the image. When the image has no release, or the matched model has no onboarded set for that release, the Install drivers step MUST be skipped with a warning; there is no fallback to another release.
- **FR-045**: When a session is assigned an OS image, Imaging Core MUST match the device to an active onboarded model of the image's architecture (HP: any ID in the model's system ID list; Dell: system SKU), or take the model chosen for it (FR-080), select that model's current set for the image's release, and pin it on the session. The Device Gateway MUST only relay the pinned set to the Client. Later syncs, policy edits, roll backs and removals MUST NOT change a pinned set.
- **FR-046**: Files of a pinned set MUST remain available until the session reaches a terminal state.
- **FR-047**: The device MUST receive one time-limited, read-only link per component of its pinned set, issued with the pinned set and scoped to that component's file only. A retry of one component MUST NOT depend on another component's link. When a link expires before the component finishes, the Client MUST be able to request a new link for that component alone. Per-component progress MUST be aggregated by the Client so the step stays within the existing progress-report budget (10 calls per 30 seconds per session).

#### Driver set choice when imaging starts

- **FR-080**: When an OS image with a Windows release is selected, the Coupled Devices table MUST show a Driver set column with a dropdown per device. It MUST preselect the matched model; when nothing matches it MUST show "No match, choose a set" with an alert icon whose tooltip names the reason and the device's match key (for example "System ID 8D41 is not onboarded for Windows 11, version 24H2.").
- **FR-081**: The dropdown MUST list every onboarded model that has a current set for the image's Windows release and architecture, grouped by vendor with the matched model first, each with its maximum download size, followed by No drivers. It MUST NOT list set versions or roll-back sets: a choice is a model, and the session gets that model's current set when imaging starts.
- **FR-082**: A row whose choice differs from the match MUST show an undo button that returns it to the match. No separate badge marks the change. A row left at "No match" gets no driver work, as before.
- **FR-083**: The Start imaging overlay MUST list the driver set per group of devices ("2 devices: HP EliteBook 840/860 G11", "1 device: no drivers"), marking manual choices "(chosen manually)".
- **FR-084**: The server MUST validate every choice when imaging starts: the model exists and has a current set for the image's release and architecture. If any choice is no longer valid, no session in the request MUST be assigned and the error MUST name the affected devices.
- **FR-085**: A session started with a manual choice MUST record that the set was chosen manually and by whom, and the deployment timeline card MUST show it ("Chosen manually by anna@contoso.com"). A session started with No drivers chosen MUST skip the Install drivers step without a warning and say so in the step note.
- **FR-086**: The column MUST be hidden when no model is onboarded, show "Choose an OS image first" before an image is selected, and show "The OS image has no Windows release" when the image has none.

#### Client: Install drivers step (boot environment)

- **FR-048**: The Client MUST run a new imaging step, Install drivers, after Apply image and before Configure boot.
- **FR-049**: For Dell, components with published hardware IDs MUST be downloaded only when the device has matching PCI hardware; components without hardware IDs are always processed. HP components in the pinned set MUST all be processed.
- **FR-050**: For each processed component, the Client MUST take it from the USB drive's cache (FR-093) or download it, verify its SHA-256 before running anything from it, and extract it with the vendor's own extractor where the vendor package can run in the boot image (HP: silent extract switches with a retry using the legacy switches, success judged by the files produced; Dell 64-bit and ARM64: the package's driver extract option). Extraction MUST target the applied Windows volume, never the boot environment's RAM disk or the USB drive, and MUST be confined to the component's own folder. Components whose package cannot run in the boot image (HP on ARM64) MUST be staged unextracted for the specialize pass.
- **FR-051**: The Client MUST inject the extracted INF content of Offline and Offline + Specialize components into the applied Windows volume using offline driver servicing, where the vendor package can run in the boot image. Unsigned drivers MUST NOT be allowed.
- **FR-052**: The Client MUST keep the staged content of Offline + Specialize and Specialize components for the specialize pass, and delete the extracted content of all other components after injection.
- **FR-053**: A failed component MUST be recorded with a reason and the step MUST continue with the remaining components. Administrators MUST be able to mark, centrally and per vendor driver category (for example HP "Driver - Storage", Dell "Network"), which categories are critical. When a component in a critical category fails, the session MUST fail after the step finishes, with the failed components as the reason. Otherwise a step with failed components MUST end as Completed with warnings and the deployment MUST still succeed. No category is critical by default. The critical categories in force when a session is assigned MUST apply to it, so a change applies to the next assignment without building a new driver set.
- **FR-054**: The Client MUST report each component's outcome to the session: injected, staged for Specialize, skipped by hardware match, or failed with reason. Each outcome MUST record the component's version and whether it is a vendor, custom or rolled-back component, so a deployment shows what it installed after its driver set is gone.

#### USB drive cache

- **FR-093**: When the boot media has a CACHE partition, the Client MUST keep every driver or custom component file it has downloaded and verified in that partition, keyed by its SHA-256, next to the cached OS images. The same rules as cached OS images MUST apply: entries unused for 30 days are removed, nothing is written when less than 500 MB would stay free, nothing already cached is evicted to make room, and a deployment without a CACHE partition (ISO, network boot) downloads to the Windows volume and caches nothing.
- **FR-094**: Before downloading a component, the Client MUST look for its SHA-256 in the cache and use the cached file when found, copying it into the component's folder on the Windows volume and verifying its SHA-256 there before anything runs from it. A cached file that fails the check MUST be deleted from the cache and the component downloaded again. The hardware match (FR-049) MUST be applied first, so the cache is never read for a component the device does not need. Partially written or unverified files MUST never remain in the cache.
- **FR-095**: Media Builder MUST let the person preparing a USB drive add driver sets to it: an optional "Driver sets" section listing onboarded models with a current set for the boot image's architecture, one choice per model and Windows release, each with its size. Selected sets MUST be downloaded and verified before the drive is partitioned and copied into the CACHE partition in the same layout the Client uses. Files shared by several selected sets MUST be counted and stored once.
- **FR-096**: Media Builder MUST show the total size of the selected driver sets and the cache space left for OS images, warn when less than 20 GB would stay free for OS images, and refuse to start preparation when the selection does not fit the CACHE partition.
- **FR-097**: The preparation manifest MUST record the preloaded driver sets (model, Windows release, set version, component count, bytes). Preloading MUST NOT pin anything: a deployment always uses the set pinned at assignment, and takes from the USB drive only the files whose SHA-256 matches.
- **FR-098**: The Install drivers step MUST report, per component, whether its file came from the USB drive or was downloaded, and the timeline card MUST summarise it ("24 from the USB drive, 2 downloaded"). Status lines MUST say "Copying drivers from the USB drive (n of m)" while cached files are used.

#### Client: Specialize phase

- **FR-055**: When the pinned set has Specialize work (Offline + Specialize or Specialize components), the Client MUST hand those components to the Prepare setup step of Setup Profiles (spec 003, FR-024 to FR-026), which lists them first in the setup manifest and adds the setup runner command to the device's answer file, whether or not a setup profile targets the device. Without Specialize work, no driver entries are added.
- **FR-056**: For driver components, the setup runner (spec 003, FR-026) MUST run as SYSTEM, process them in resolved order before any setup profile scripts, re-verify each component's staged files against the manifest, run the vendor's silent installer with a timeout, and log the exit code. Exit codes 0 and 3010 MUST count as success (3010: restart pending). The setup runner MUST NOT restart the device inside specialize, and MUST continue after a failed component.
- **FR-057**: Specialize results MUST be written to a local log on the device only; nothing is reported to the portal after the reboot. The setup runner MUST delete the staged content when done, including after failures.

#### Deployment timeline

- **FR-058**: The deployment timeline MUST show an Install drivers step with a neutral card listing the components and their outcomes. Specialize components MUST be shown as staged, without a result.
- **FR-059**: A step with failed components MUST show an amber warning marker and the label "Completed with warnings"; a skipped step MUST show the skip reason.
- **FR-060**: The timeline MUST NOT assume a fixed number of steps, so deployments recorded before this feature (without the step) still display correctly.

#### Portal: Drivers page

- **FR-061**: The portal MUST provide a Drivers page under Imaging with tabs Models, Rules, Sync, Downloads and Storage.
- **FR-062**: Action names MUST be Offline, Offline + Specialize, Specialize and Skip everywhere in the UI, including the tiles and the timeline ("26 installed offline"). The Action column header MUST have an info icon explaining them.
- **FR-063**: The Models tab MUST show one row per onboarded model with columns Model (match keys underneath), Vendor, Windows releases, Included, Skipped, Size, Status and an open chevron, plus a toolbar with search, a vendor filter, the time of the last check, Check for updates (FR-034) and Onboard model. Model MUST get the widest of the fixed column shares; Status MUST be wide enough for its labels on one line and no wider, and the table MUST NOT show a model count. Size MUST have an info icon stating that it is the most one device downloads and that devices without some hardware can download less; the text MUST be vendor-neutral and never model-specific.
- **FR-064**: Status MUST be a single icon and label showing only what needs attention or is in progress (for example Draft, "Downloading 12 of 33", components waiting for approval, a failed download), or a muted "Sync off" when the model's vendor sync is off. Models that need nothing show a green "Ready".
- **FR-065**: The model page header MUST show a breadcrumb, the model name with its match keys as a badge to its right ("Baseboard 8C26", "System SKU 0CB2, 0CB4"), and, on the same row aligned right when both fit, otherwise on a row of their own aligned left, the buttons Updates (Check for updates, FR-100), Add component (Add custom component), Windows releases, Roll back (Roll back driver set) and Remove (Remove model), each with a tooltip saying what it does. The match key badge MUST always stay next to the name, which wraps rather than the badge. It MUST NOT show a vendor badge, the driver set version or build time. Roll back MUST be disabled without a previous set, and its tooltip MUST name the set devices would get instead. Banners MUST appear when the model is a draft (with Complete onboarding), when components wait for approval, when a driver set is being prepared ("Preparing driver set 54: 12 of 31 components downloaded", with what devices get meanwhile), when changes are waiting to be built (with Build now), and when the vendor publishes a new version of a component a custom component replaces.
- **FR-066**: The model page MUST show a tile per action plus a Size tile (the most one device downloads, with the same info icon as the Models tab), and a toolbar with search, the Windows release selector, action and category filters, and an "N updated by today's sync" filter. The Windows release selector is only a filter: it lists the model's releases and scopes the tiles and the component table. Windows releases opens a dialog listing only the releases the vendor publishes drivers for on this model, each with a checkbox, its component count, download size and status (Onboarded, Adds, Removes, with what a removal deletes); nothing changes until Save, the last release cannot be unticked, and Remove always removes the whole model.
- **FR-067**: The component table MUST have columns: chevron; Component (name, with ID and category below, an amber New badge while waiting for approval, and a muted Withdrawn badge when the vendor no longer publishes it); Version; Last updated (date only); Action (fixed-width dropdown); Source. Every column except Component MUST have a fixed width, so names only wrap on narrow screens. All components MUST be listed (no paging or "show more"); search and filters narrow the list and the footer shows the count. Components the vendor locks to Skip that have nothing to approve or remove MUST NOT appear as rows; the footer MUST offer "N locked to Skip", which opens a flyout listing them (name, ID, category, version).
- **FR-068**: Source MUST show, in precedence order: **Locked** (lock icon and tooltip on the dropdown), **Replaced by custom** (action locked to Skip), **Custom**, **Rolled back**, **Override** (with undo), the rule's name over **Rule**, or **Default**. Custom and Rolled back MUST use icon and label, with the details (what it replaces; who rolled it back, the skipped version and when it moves forward) in a tooltip on the icon. Values MUST NOT repeat the vendor name.
- **FR-069**: Each component row MUST expand to a three-column grid of labelled fields followed by a bordered Description section with a link to the vendor's release notes. HP fields: Offline injection, Contains an app, Silent install, Default action, Released by HP, Release type, Added to this model, Previous version, Download size. Dell fields: Installer (64-bit or 32-bit), Hardware match, Package type, Default action, Released by Dell, Criticality, Added to this model, Previous version, Download size. Install commands, SHA-256 values and supported Windows releases MUST NOT be shown.
- **FR-070**: The Rules tab MUST show the rules table (Name, Applies to, Matches, Action, Hits) and Add rule, with an info icon explaining precedence and what happens to components a vendor withdraws, followed by a Deployment failure card: per vendor, the driver categories whose failure fails the whole deployment (FR-053), changed on the spot with no Save button.
- **FR-071**: The Sync tab MUST show, in order: a Sync card (description; schedule controls Every day, Selected days with a repeat (every 1 to 4 weeks) and day toggles, or Manually only; time; IANA time zone; the next-sync line (FR-032); a Sync now row with a Sync button (FR-034), separated from the schedule by an inset divider; then, after a full-width divider, "Newer versions: Use automatically / Wait for testing" (FR-103) and the "New components: Wait for approval" switch), with settings applied on change and no Save button; a Vendors card in the same setting-row layout with one row per vendor (short line with onboarded models and match key, and a Sync switch; unsupported vendors listed with a disabled switch); and a History card.
- **FR-072**: History rows MUST be one line each: Started as an absolute timestamp (`2026-10-22 02:00`), a Result badge, and a generic summary ("4 updated, 1 failed", "1 new", "No changes"). A sync that finds models a vendor has added MUST add "<vendor>: N new models available" to its summary. A chevron MUST open one line per item (Model, Component linking to its row on the model page, Change, Outcome). Runs without changes MUST have no chevron.
- **FR-073**: The Storage tab MUST show tiles for total used (info icon: shared components are stored once, so the total is less than the sum of the models) and per vendor, a Previous versions setting row (FR-102), and a one-line table per model with Vendor, Windows releases, Driver sets ("2 current, 2 roll-back") and Size. Each model row MUST expand to one block per Windows release whose heading names its current and roll-back set with their build times and the block's component count and size, followed by a table of the stored components: Component (name and ID), Version, Size, Driver sets (which kept sets use it, "Previous version", or "Next set"), Shared with (other models), Status (Stored, Downloading or Failed as icon and label, the failure reason in a tooltip on the icon) and Downloaded. Storage MUST NOT be shown on the Models tab.
- **FR-074**: All Drivers tables MUST use equal, single-line row heights with truncation, and absolute timestamps.
- **FR-087**: Every text that names a vendor (dialogs, banners, tooltips, detail field labels such as "Released by HP", toasts, errors, API messages, the device log) MUST take the name from the vendor of the model or component it is about, so a Dell component says Dell and an HP component says HP. Vendor names MUST NOT be written into those texts; the only fixed vendor names are the vendor list itself (vendor filter, Vendors card, onboarding vendor picker).
- **FR-088**: The Downloads tab MUST show downloads in progress, one line per model, Windows release and driver set (Model, Windows, Driver set, Progress as a bar with "12 of 31 components", Size as "1.1 of 2.5 GB", Status as icon and label: Downloading, Waiting or "1 failed" with the reason in a tooltip, Started), and driver sets that became ready in the last 7 days (Model, Windows, Driver set, Components as "4 new, 27 already stored", Size, Status, Finished). With nothing in progress it MUST show the portal's empty state. The tab label MUST show the number of downloads in progress.

#### Component history, roll back and custom components

- **FR-089**: For each component of each onboarded model and Windows release, the system MUST keep a version history for as long as the model is onboarded: version, the driver sets that used it, from and to dates, what changed it (sync, onboarding, roll back, or the user), and the vendor's download location and SHA-256. The expanded component row MUST show it as a collapsible section, closed by default with the number of versions, holding a read-only table (Version, Driver sets, From, To, Change: Onboard, Sync, Roll back or Custom component, By: the user or "Automatic", Status: Stored, Downloads on roll back, or No longer available from <vendor>).
- **FR-090**: Operators MUST be able to roll back one component to a version from its history (Roll back component, a confirmation overlay with a Version choice) until the vendor publishes a version newer than the current one (default) or until the roll back is undone. The rolled-back version MUST be used by the next driver set while every other component keeps updating. A version that is no longer stored MUST be downloaded again from the vendor's recorded location and verified against its SHA-256 before a set uses it; if that fails, the roll back MUST fail with the reason and change nothing. When Roll back component opens, the system MUST check that the vendor still hosts each version that is not stored, record the result on the version, and offer a version it cannot reach only as "No longer available from <vendor>", not as a choice.
- **FR-102**: Operators MUST be able to choose on the Storage tab how many previous versions of each component stay stored: none, or 1 to 3 (default 1). Previous versions are the versions a model and Windows release used directly before the current one, newest first; custom components do not count, as they stay until removed. Raising the number MUST NOT download versions that are already deleted; lowering it frees their files at the next clean-up. The setting MUST show how much storage the kept previous versions use.
- **FR-091**: Operators MUST be able to add a custom component: an uploaded package (for Offline and Offline + Specialize a ZIP containing at least one INF file; for Specialize an installer with its silent command), name, version, action, category, one or more onboarded models (all their Windows releases), and optionally the vendor component it replaces on those models. The upload MUST be refused with a reason when the ZIP has no INF file or the file exceeds 5 GB. The SHA-256 MUST be computed on upload and verified on the device like any component. Drivers in it MUST be signed; the Client never allows unsigned drivers. A custom component and its earlier uploaded versions MUST stay stored while any set or its history refers to them, because they cannot be downloaded again.
- **FR-092**: A replaced vendor component MUST show Replaced by custom with its action locked to Skip, and return to its own action when the custom component is removed. One vendor component MUST NOT be replaced by more than one custom component on the same model. A custom component MUST stay until an Operator removes it; when a sync finds a new version of the component it replaces, the model page MUST show a banner with that version and its release date and a Remove custom component action, and dismissing the banner MUST be recorded.

#### Access control and audit

- **FR-075**: Only `CloudImaging.Operator` and `CloudImaging.Administrator` MUST be able to onboard, remove, edit rules and overrides, approve, roll back a driver set or a component, add, change and remove custom components, dismiss custom component banners, use Build now, change sync, vendor, storage and critical-category settings, and use Sync now and Check for updates. Setting an OS image's Windows release follows the OS Images catalog permissions (Operator or Administrator). Choosing a device's driver set when imaging starts (FR-080) follows the Start imaging permission (Technician, Operator, Administrator), because it changes only that deployment. These restrictions MUST be enforced by the server, not only by the UI.
- **FR-076**: Users with read access MUST see the Drivers page without edit controls. `CloudImaging.Technician` reads; Reader, Autopilot Approver and users without a role have no access. Media Builder MUST list driver sets and download their files only through endpoints on its own allowlist, available to `CloudImaging.MediaBuilderAccess`, which return read links for the files of current sets only.
- **FR-077**: Every change listed in FR-075 MUST be recorded with who made it and when. A manual driver set choice is recorded on the session (FR-085).
- **FR-079**: No part of the feature outside code comments (UI text, messages, logs, API responses, the device log, docs) MUST contain requirement, success criterion, task or story identifiers or pointers to them.

### Key Entities *(include if feature involves data)*

- **Vendor Setting**: a supported vendor (HP, Dell) or listed unsupported vendor (Lenovo), with its Sync on/off state and match key type.
- **Vendor Model Entry**: a model from a vendor's catalog: name, match keys (HP system IDs; Dell system SKUs), architecture, available Windows releases.
- **Onboarded Model**: vendor, display name, match keys, architecture, onboarded Windows releases, state (draft or active). Owns overrides and resolved sets.
- **Vendor Component**: one vendor package version for a model and release: ID, name, category or type, version, release date, vendor flags used for classification, allowed actions, default action, lineage key, size, SHA-256, download location, description link, and for Dell the hardware IDs and installer architecture.
- **Rule**: name, scope (all vendors or one vendor), match kind (category, name pattern, component ID), match value, action, hit count.
- **Model Override**: onboarded model, component lineage, action.
- **Approval State**: per model and component lineage: new and waiting, or approved with the confirmed action.
- **Resolved Driver Set**: model, release, set version, fingerprint, build time, role (current or roll-back), items (component, version, action, stored file reference).
- **Stored Driver File**: one verified original vendor file per component version, referenced by sets and pinned sessions.
- **Sync Settings**: schedule mode, weekdays, repeat in weeks and the week it counts from, time, IANA time zone, new-components approval setting, number of previous component versions to keep.
- **Critical Category**: vendor and driver category whose component failure fails the deployment.
- **Driver Set Build**: one build of a model and release's driver set: what triggered it (onboarding, sync, policy change, roll back, custom component), when it was built and when all its files were ready, component counts (total, new, reused, failed) and bytes (total, downloaded); feeds the Downloads tab and the version history, kept 90 days.
- **OS Image (extended)**: gains an administrator-entered Windows release.
- **Sync Run**: start time, trigger (scheduled, Sync now, Check for updates on all models, or Check for updates on one model), scope (all models or one model), whether model lists were refreshed (syncs only), result, items (model, component, old and new version, outcome, reason).
- **Session Driver Assignment**: session, matched or chosen model, release, pinned set version, whether it was chosen manually and by whom, the critical categories in force at assignment, per-component outcomes reported by the Client with each component's version and origin (vendor, custom, rolled back).
- **Component Version**: model, release, component lineage, version, driver sets that used it, from and to dates, what changed it, vendor download location and SHA-256, and whether the vendor still hosts it as of the last check.
- **Component Roll Back**: model, release, component lineage, held version, until (a newer vendor version or undone), who and when.
- **Custom Component**: name, version, action, category, uploaded file and SHA-256, silent command (Specialize), models, replaced vendor component per model, who uploaded it and when, dismissed banners.
- **Cached Driver File**: on the USB drive's CACHE partition: SHA-256, file name, size, cached and last used time. Shared by every set and model that uses the same file.
- **Preloaded Driver Set**: recorded in the USB preparation manifest: model, Windows release, set version, component count, bytes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An administrator can onboard a model, review its components and complete onboarding in under 3 minutes, and the model's driver set becomes ready without waiting for a scheduled sync.
- **SC-002**: 100% of stored driver files match the vendor's published SHA-256; 0 files that fail verification are ever referenced by a set or downloaded by a device.
- **SC-003**: On each reference HP and Dell model (x64 and ARM64), a deployment completes the Install drivers step without warnings, and every Offline component of the pinned set is present in the installed OS at first boot, after the specialize pass for HP ARM64 models.
- **SC-004**: 100% of in-flight deployments use the set pinned at assignment, regardless of syncs, policy edits, roll backs or removals during the deployment.
- **SC-005**: 100% of Sync now and all-models Check for updates requests made while a run is active or within 1 minute of the last sync or all-models check start are refused, including direct API calls.
- **SC-006**: A vendor whose sync is off receives 0 catalog or download requests during runs.
- **SC-007**: After any sync run, 0 stored files remain that no current set, roll-back set, pending set, component roll back, custom component, kept previous version or running deployment refers to.
- **SC-008**: 0 new deployments receive a removed model's or release's sets after the removal is confirmed.
- **SC-009**: With approval on, 0 new components reach devices before an administrator approves them, while 100% of newer versions of known components are used without approval.
- **SC-010**: From the Models tab alone, without opening a model, an administrator can see every model that is a draft, downloading, waiting for approval, failed or has Sync off, each named in its Status column.
- **SC-011**: On a reference model over a 500 Mbit/s connection, the Install drivers step adds no more than 10 minutes to a deployment.
- **SC-012**: 100% of sessions started with a manual driver set choice pin the chosen model's current set (or none for No drivers), and their timeline names who chose it; a technician can correct an unmatched device in under 30 seconds from the Coupled Devices table.
- **SC-013**: An Operator can roll back one component to an earlier version in under 1 minute, and 100% of the following driver sets keep every other component at its newest resolved version.
- **SC-014**: Within 1 minute of completing onboarding, a policy change or a check for updates that finds a newer version, the Downloads tab shows the resulting downloads and their progress.
- **SC-015**: 0 new driver sets are created by a new version of a skipped component, a change of critical categories, or a burst of edits beyond one set per 2 minutes of quiet.
- **SC-016**: The second deployment of a model with the same USB drive downloads 0 bytes for components whose file is unchanged, and a deployment from a USB drive preloaded in Media Builder downloads 0 driver bytes when the pinned set equals the preloaded one.

## Assumptions

- The match keys are not covered by the device signature, the same as manufacturer and model today. A device that reports another model's key only receives that model's driver set.
- Read access: the todo names `CloudImaging.PortalAccess` as the reading role. In this product that role is the Portal backend's managed identity on the Operator API, not a user role, so the Drivers endpoints are called through it while user read access follows the existing catalog pattern (Technician reads, Operator and Administrator edit).
- Retention is the current set plus one previous set per model and release. Keeping more previous sets and a storage size budget per model are out of scope for this version.
- A set in testing is not targeted by location or device group: an Operator chooses it per device at imaging start. Releasing it needs no test deployments first.
- When two rules of the same layer and match kind match one component with different actions, the most recently changed rule wins, and Source names it.
- A component the vendor withdraws from its catalog stays in the resolved set at its last verified version until an administrator removes it, because the hardware it supports is still in the field. Its stored file is kept while the component is in a set.
- Sync run history is retained for at least 90 days.
- HP components come from HP's per-platform reference file, not HP's monthly driver pack; a driver-pack mode is out of scope.
- Architecture reduction of payloads (keeping only the target architecture's INF files) is out of scope until x86 and arm64 content in real packages has been measured.
- Injecting drivers into WinRE during Apply recovery image is out of scope for this version.
- Firmware management is a separate feature.
- The existing session failure, retry and progress mechanisms are reused; only the new step and its outcomes are added.

### Dependencies

- **Operator role (prerequisite, spec 004)**: a new `CloudImaging.Operator` role, between Technician and Administrator (Administrator implies Operator, Operator implies Technician), manages day-to-day operations: OS, boot and recovery images, driver management, Locations, Wi-Fi networks, Autopilot approvals, Reports, and boot image generation in Media Builder. Configuration, Branding, Autopilot settings and other tenant-wide security settings stay Administrator-only. The role is introduced by its own change, not by this specification. Until it exists, Administrator alone holds the FR-075 permissions.
- **Setup Profiles (prerequisite, spec 003)**: owns the device's single `Panther\unattend.xml` and the setup runner. Its Prepare setup step runs right after Install drivers and before Configure boot, and writes the answer file entry that starts the setup runner during specialize. User Story 3 cannot ship before it; User Stories 1, 2 and 4 to 7 do not depend on it.
- The Install drivers and Prepare setup steps use the same step ordering and reporting model, and both are optional steps that older deployments do not have.
- Vendor catalogs remain publicly reachable at their current locations and formats.
- **USB drive cache (spec 001, FR-009a to FR-009d and FR-055)**: the two-partition USB layout (FAT32 BOOT, NTFS CACHE) and the Client's OS image cache are reused for driver files. The cache is looked up by SHA-256: a match is used instead of downloading, anything else is downloaded, and when the cache is full the file is downloaded without being cached.

### Risks and items still to validate

1. HP's silent extractor running in this product's x64 boot image: the program architecture is confirmed, behaviour is not.
2. HP silent installers during specialize as SYSTEM with no user signed in and no Store (Hotkey Support, Realtek audio, Intel video, HP Power Manager), and whether companion apps are provisioned for all users rather than only SYSTEM.
3. Offline injection followed by the same component's installer (Offline + Specialize) must not cause a second driver install or a restart request.
4. Which INFs install companion software by themselves at first boot, to avoid a double install.
5. Dell 64-bit and ARM64 package extraction in this product's boot images, and Dell's silent installer during specialize for drivers that bundle an app.
6. Dock drivers with hardware match only match when the dock is connected during imaging.
7. Driver payloads of 2.0 to 3.2 GB per HP platform are outside the constitution's 15-minute reference workload, which covers the base OS image (constitution 1.5.0). Their own budget is SC-011, and the plan measures the full deployment as well.
8. Whether streamed SHA-256 verification of the largest files (around 800 MB) fits the chosen cloud compute's limits.

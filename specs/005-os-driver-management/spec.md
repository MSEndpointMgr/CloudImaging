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
- Q: Driver downloads of 2 to 3 GB per HP model may exceed the constitution's 15-minute end-to-end target. A: Accepted.
- When two rules of the same layer and match kind conflict, the most recently changed rule wins (agreed).
- A failed download keeps the last verified version and is retried at the next sync (agreed).
- Sync run history is kept for 90 days (agreed).
- A component the vendor withdraws stays in the set until an administrator removes it (changed from "leaves at the next sync").

### Session 2026-10-23

- Q: Who writes `Panther\unattend.xml` and runs Specialize installers? A: Setup Profiles (spec 003). Its Prepare setup step, which runs right after Install drivers, writes the device's single answer file with one setup runner command; the setup runner runs driver installers first, then setup profile scripts. This applies whether or not a setup profile targets the device.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Onboard an HP model and have its drivers installed during deployment (Priority: P1)

An administrator opens the new Drivers page, onboards an HP model (for example EliteBook 840 G11) for Windows 11 24H2, and reviews what the model will get before anything downloads. The next sync downloads the vendor's files into the customer's storage. A technician then deploys a matching device: the Device Gateway recognises the model, pins the driver set on the session, and the Client injects the drivers into the applied Windows volume while still in WinPE. The deployment timeline shows an Install drivers step.

**Why this priority**: This is the whole value chain in its smallest form. Today the installed OS only gets drivers from Windows Update at OOBE, which fails for new hardware and offline scenarios (for example a NIC needed before Entra join or Autopilot). Without this slice nothing else in the feature matters.

**Independent Test**: Onboard one HP model with one release, run a sync, deploy a device of that model, and confirm that the vendor's WinPE components are present in the installed OS's driver store at first boot and that the timeline shows the Install drivers step with its component list.

**Acceptance Scenarios**:

1. **Given** the HP vendor catalog is available, **When** an administrator opens Onboard model, selects one or more HP models and continues, **Then** step 2 asks for Windows releases and shows a per-model table with counts for WinPE, WinPE + Specialize, Specialize, Skip and the download size, and nothing has been downloaded yet.
2. **Given** an onboarded model and release, **When** the next sync runs, **Then** every included component's original vendor file is stored in the customer's storage account, verified against the vendor's SHA-256, and the model gets a resolved driver set with a version and build time.
3. **Given** a device whose HP baseboard ID is in an onboarded model's system ID list and whose assigned OS image matches an onboarded release, **When** the session is assigned, **Then** the current resolved set is pinned on the session and later policy edits, syncs or roll backs do not change it.
4. **Given** a pinned set, **When** the Client reaches the step after Apply image, **Then** it downloads, verifies, extracts with HP's own extractor and injects each WinPE component into the applied volume without allowing unsigned drivers, before Configure boot runs.
5. **Given** a device that matches no onboarded model, **When** the Client reaches the Install drivers step, **Then** the step is skipped with a warning and the deployment continues.
6. **Given** a completed deployment, **When** an operator opens its timeline, **Then** an Install drivers step lists the components and their outcomes.

---

### User Story 2 - Decide per component what happens, with defaults, rules and overrides (Priority: P1)

An administrator reviews a model's component table. Each component already has a default action from the vendor's own data. The administrator adds a rule to skip vendor telemetry and service components across all HP models, and overrides one component on one model. Components the vendor data rules out cannot be changed. The Source column shows why each component has its action.

**Why this priority**: The vendor's data alone installs telemetry services and leaves out companion apps. Without control over actions, customers cannot accept the result, so this is part of the minimum viable product.

**Independent Test**: With one onboarded model, create a name rule and a model override, and confirm the resolved set and the Source column reflect the precedence order, and that a locked component stays at Skip.

**Acceptance Scenarios**:

1. **Given** an HP component with `DPBCompliant = true` and no `Uwp` content, **When** no rule or override matches, **Then** its action is WinPE and its Source is Default.
2. **Given** a rule "Skip HP Services Scan" matching by name for all HP models, **When** the policy is resolved, **Then** that component is Skip with the rule name shown in Source, and the rule's Hits count includes it.
3. **Given** a rule and a model override that both match a component, **When** the policy is resolved, **Then** the override wins, Source shows Override with an undo button, and undo returns the component to the rule's action.
4. **Given** a component the vendor data allows only Skip for, **When** an administrator opens its Action dropdown, **Then** the dropdown is locked with a lock icon and a tooltip explaining why, and no rule or override can change it.
5. **Given** a rule whose action is not allowed for a matching component, **When** the policy is resolved, **Then** the rule does not apply to that component and the next layer decides.

---

### User Story 3 - Install vendor companion apps and installer-only drivers during Specialize (Priority: P2)

For components such as HP Hotkey Support, Realtek audio or Intel graphics, injecting the INF makes the hardware work but leaves out the vendor's companion app. With the action WinPE + Specialize (or Specialize for installer-only components), the Client stages the component on the applied volume and Windows setup runs the vendor's silent installer as SYSTEM during the specialize pass, before OOBE.

**Why this priority**: Required to match the vendor's factory experience, but devices are usable without it. It also depends on the Prepare setup step and setup runner from Setup Profiles (spec 003).

**Independent Test**: Deploy a device whose pinned set contains one WinPE + Specialize component and one Specialize component, and confirm after OOBE that the companion apps are installed for all users and a local log lists each component's exit code.

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
3. **Given** a run started 20 minutes ago, **When** anyone presses Sync now or calls the API directly, **Then** the request is refused and the response states when Sync now becomes available (60 minutes after the last run started).
4. **Given** Dell sync is off, **When** a run starts, **Then** no Dell catalog is read and Dell models keep their current set, which devices continue to receive; their rows show a muted "Sync off" badge.
5. **Given** the HP catalog host is unreachable during a run, **When** the run finishes, **Then** Dell changes are still applied and the run result is Partly failed with a failed item naming the HP reason.
6. **Given** a run where only a component waiting for approval was added, **When** it finishes, **Then** the result is Completed and the summary reads "1 new".

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

An administrator onboards Dell Latitude 5450 (system SKUs 0CB2 and 0CB4) as one model. Defaults follow Dell's data: 64-bit drivers in WinPE, drivers bundling an app in WinPE + Specialize, 32-bit drivers in Specialize, applications skipped. A device without mobile broadband or an NVIDIA GPU does not download those components.

**Why this priority**: Second vendor, same policy and phases. HP delivers the value first.

**Independent Test**: Onboard one Dell model, deploy a device of one of its SKUs without optional hardware, and confirm that only matching components were downloaded and that 32-bit packages were never run in WinPE.

**Acceptance Scenarios**:

1. **Given** the Dell catalog index, **When** an administrator onboards a Dell model, **Then** one onboarded model covers all of that model name's system SKUs, each with its own component catalog.
2. **Given** a Dell per-SKU catalog with the full release history, **When** the set is resolved, **Then** only the newest package per lineage (hardware component ID, falling back to the name) is used, and components that do not list the target Windows version are dropped.
3. **Given** a device whose PCI hardware does not match a component's published hardware IDs, **When** the Client runs the Install drivers step, **Then** that component is not downloaded and is reported as skipped by hardware match.
4. **Given** a 32-bit Dell package, **When** its allowed actions are shown, **Then** WinPE and WinPE + Specialize are not offered.
5. **Given** a Dell device, **When** the Client collects hardware data, **Then** the system SKU is included in the signed hardware payload and the Device Gateway matches on it.

---

### User Story 7 - Understand storage, roll back and remove models (Priority: P3)

An administrator checks the Storage tab to see total and per-vendor usage, rolls a model back to its previous driver set after a bad vendor update, and removes a model no longer deployed after reviewing an impact overlay that shows the space freed.

**Why this priority**: Operational hygiene. Clean-up is automatic, so the feature works without this story, but administrators need to see costs, recover from bad updates and retire models.

**Independent Test**: With two models sharing a component, verify the Storage tab counts it once, roll one model back and confirm new sessions get the previous set, then remove a model and confirm the overlay's freed space matches what the next clean-up deletes.

**Acceptance Scenarios**:

1. **Given** two models sharing a component version, **When** the Storage tab loads, **Then** total used counts the file once and each model's "Freed if removed" excludes files another model uses.
2. **Given** a model with a previous set, **When** an administrator presses Roll back, **Then** new sessions get the previous set, running sessions keep their pinned set, and the next sync does not automatically reinstate the set that was rolled back from.
3. **Given** an administrator presses Remove model, **When** the impact overlay opens, **Then** it states what devices will experience, what is deleted, the space freed, and what is kept, and nothing changes until the administrator confirms.
4. **Given** a removal is confirmed, **Then** new deployments stop getting the model's sets at once, running deployments keep their pinned set, sets and overrides are deleted, rules and sync history are kept, and unreferenced files go in the next clean-up.
5. **Given** a sync finishes, **When** a stored file is referenced by no current set, roll-back set or running deployment, **Then** it is deleted without any manual action.

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
- Sync now pressed during a scheduled run, or within 60 minutes of any run start: refused by the API with the time it becomes available.
- A scheduled slot passes while a run is still in progress: no second run starts for that slot.
- A vendor is turned off while a run is reading its catalog: the in-progress run finishes; the next run skips that vendor.
- Onboarding a model of a vendor that is off: the vendor is turned on.
- The applied volume lacks space for extraction: the affected components fail with a clear reason; extraction never falls back to WinPE's RAM disk.
- A device restarts or loses power during the Install drivers step: the existing session failure and retry behaviour applies; a partial driver store is replaced when the image is applied again.
- The specialize installer hangs: the setup runner stops it at the timeout, logs it, and continues.

## Requirements *(mandatory)*

### Functional Requirements

#### Scope and vendors

- **FR-001**: The system MUST support HP and Dell business client catalogs as driver sources. Catalog reading, device match key, component classification, version lineage and where unpacking happens MUST be isolated per vendor, so that adding a vendor later (Lenovo) does not change policy, sync, storage, matching or the Client phases.
- **FR-002**: Lenovo MUST be shown as an unsupported vendor (listed with a disabled Sync switch) and MUST NOT be onboardable. Microsoft Surface and any vendor without a machine-readable catalog MUST NOT be supported; the system MUST NOT scrape vendor web sites.
- **FR-003**: BIOS, system firmware, TPM firmware, dock firmware and SSD firmware MUST be excluded entirely: never downloaded, never listed, never matched by rules. Exclusion MUST use the vendor's category or type (HP `BIOS*`, `Firmware`, dock firmware; Dell `FRMW`, `BIOS`) and MUST also exclude any component whose name contains "Firmware".
- **FR-004**: OS images MUST remain unchanged by this feature; drivers MUST NOT be added to the stored OS image.

#### Model catalog and onboarding

- **FR-005**: For each enabled vendor, a sync MUST maintain the list of models the vendor publishes: HP from its driver pack catalog (system name, system ID list, OS releases); Dell from its catalog index (model name, system SKUs, one component catalog per SKU, with its SHA-256).
- **FR-006**: Administrators MUST be able to onboard models in a two-step dialog. Step 1: multi-select from a vendor's model list with a vendor dropdown and search; already onboarded models are shown disabled. Step 2: choose Windows releases and review a per-model table (WinPE, WinPE + Specialize, Specialize, Skip, Size) before anything downloads.
- **FR-007**: An onboarded model MUST record vendor, display name, match keys and Windows releases. HP: one onboarded model per system ID set. Dell: one onboarded model per model name, covering all its system SKUs. A match key MUST NOT belong to more than one onboarded model.
- **FR-008**: Onboarding a model of a vendor whose sync is off MUST turn that vendor's sync on.
- **FR-009**: Administrators MUST be able to add and remove Windows releases of an onboarded model.
- **FR-010**: Devices that match no onboarded model, or whose OS image release is not onboarded for the matched model, MUST receive no driver work; the Install drivers step is skipped with a warning.

#### Offboarding

- **FR-011**: Administrators MUST be able to remove a model or one Windows release of it. The removal MUST take effect immediately for new deployments; running deployments keep their pinned set. The removed model's (or release's) sets and overrides MUST be deleted; rules and sync history MUST be kept; its stored files MUST be deleted in the next clean-up unless another set refers to them. Onboarding the model again MUST start from the current catalog with no previous overrides.
- **FR-012**: Removal MUST be confirmed through the portal's impact overlay stating what devices will experience, what is deleted, the space freed, and what is kept.

#### Actions and classification

- **FR-013**: Every component MUST have exactly one action:
  - **WinPE**: INF content injected into the applied Windows volume while in WinPE.
  - **WinPE + Specialize**: as WinPE, plus the vendor's silent installer during the specialize pass to add the companion app. The hardware works even if the specialize run fails.
  - **Specialize**: only the vendor's silent installer during the specialize pass.
  - **Skip**: not downloaded and not installed.
- **FR-014**: Each component MUST have a set of allowed actions derived from vendor data. When vendor data allows only Skip, the component MUST be locked to Skip and no rule or override can change it.
- **FR-015**: HP "installable" MUST mean `SSMCompliant = true` and `SilentInstall` is not `None`, `NA`, empty, or text containing "not support".
- **FR-016**: HP defaults and allowed changes MUST be:

  | Vendor data | Default | May change to |
  |---|---|---|
  | `DPBCompliant = true`, no `Uwp` content | WinPE | Skip |
  | `DPBCompliant = true`, `ContentTypes = Uwp` | WinPE + Specialize | WinPE, Skip |
  | `Software - *`, `DPBCompliant = false`, installable | Skip | Specialize |
  | `Driver - *`, `DPBCompliant = false`, installable | Specialize | Skip |
  | Not installable | Skip (locked) | |
  | `Diagnostic`, `Manageability*`, `OS`, `Utility` with `DPBCompliant = false` | Skip (locked) | |

- **FR-017**: Dell defaults MUST be: 64-bit driver package (`DRVR`, `LW64`) WinPE; driver that bundles an app ("Driver and Application" in the name, or an app-type device entry) WinPE + Specialize; 32-bit driver package (`LWXP`) Specialize; application (`APAC`) Skip, changeable to Specialize. 32-bit packages MUST NOT be offered WinPE or WinPE + Specialize.
- **FR-018**: Version lineage MUST be vendor-specific: HP via the `Supersedes` attribute; Dell via the hardware component ID, falling back to the component name when absent. For Dell, only the newest package per lineage is used, and components that do not list the target Windows version (for example Windows 10 only) MUST be dropped.

#### Policy: rules and overrides

- **FR-019**: Administrators MUST be able to create, edit and delete rules with a name, a scope (all vendors or one vendor), a match kind (category, name pattern, or component ID) and an action. The Rules tab MUST show each rule's number of matched components (Hits).
- **FR-020**: Administrators MUST be able to override a component's action on one model and undo the override.
- **FR-021**: Precedence MUST be: model override, then vendor-scoped rules, then rules for all vendors, then the vendor default. Within one layer an ID rule beats a name rule, which beats a category rule. The most specific match wins.
- **FR-022**: A rule or override whose action is not allowed for a matched component MUST NOT apply to that component; the next layer decides.
- **FR-023**: The system MUST offer suggested Skip rules (for example HP Services Scan for Commercial, Seamless Firmware Update Service, HP App) that an administrator can add. Suggestions MUST never be applied without an administrator adding them.
- **FR-024**: A policy change MUST produce a new resolved set for each affected model and release. Components that need a file not yet stored MUST be downloaded and verified before the new set becomes current; until then devices continue to receive the current set.

#### New components and newer versions

- **FR-025**: A newer version of a known component MUST be used automatically, MUST keep the component's action, and MUST never wait for approval.
- **FR-026**: A component that does not continue a known lineage MUST be flagged as New on its model. When "New components: Wait for approval" is on, it MUST be held out of the resolved set until an administrator approves it. Approve MUST confirm the component's current action, Skip included; there is no separate Skip action. When the setting is off, new components MUST enter the resolved set with their resolved action.
- **FR-027**: A component waiting for approval MUST NOT affect a sync run's result.

#### Resolved driver sets

- **FR-028**: For each onboarded model and release, the system MUST maintain a resolved driver set (component, version, action, stored file) with a fingerprint, a version and a build time. A new set MUST be created only when the fingerprint changes.
- **FR-029**: The system MUST keep the current set and the previous set (roll-back set) per model and release.
- **FR-030**: Administrators MUST be able to roll back to the previous set. Roll back affects new deployments only. A sync MUST NOT automatically reinstate the set that was rolled back from; only a set with a different fingerprint, or an administrator action, replaces the rolled-back set.
- **FR-031**: A set MUST become current only when every file it refers to is stored and verified. A component whose download or verification fails MUST keep its last verified version in the new set, or be left out if none exists, and MUST be retried at the next run.
- **FR-078**: A component the vendor withdraws from its catalog MUST stay in the resolved set at its last verified version, MUST show a muted Withdrawn badge next to its name, and MUST leave the set only when an administrator removes it from the model. If the vendor publishes the lineage again, the component continues as a newer version and the Withdrawn badge clears.

#### Catalog sync

- **FR-032**: Administrators MUST be able to set the sync schedule to every day, selected weekdays, or manually only, at a chosen time in a chosen IANA time zone (default: the browser's time zone). The schedule MUST be stored server-side and MUST take effect without a restart or redeployment.
- **FR-033**: A scheduled run MUST start at most once per scheduled slot, and only one run MUST be active at a time.
- **FR-034**: Sync now MUST start the same run as a scheduled one. It MUST be refused while a run is in progress and for 60 minutes after the last run (scheduled or manual) started. The refusal MUST be enforced by the API, and the response MUST state when Sync now becomes available; the portal MUST disable the button and show that time.
- **FR-035**: Each supported vendor MUST have a stored Sync on/off setting. A run MUST read catalogs only for vendors that are on, and only for their onboarded models. Turning a vendor off MUST pause its updates only: its models keep their current set and devices keep receiving it.
- **FR-036**: Catalogs MUST be processed per vendor, so one vendor's outage does not block another vendor's changes.
- **FR-037**: Each run MUST record its start time, a result and one item per change (model, component, old and new version, outcome: updated, new and waiting for approval, new, removed, or failed with reason). Results: **Running** while active; **Completed** when no item failed; **Partly failed** when some items or vendors failed and others succeeded; **Failed** when nothing could be processed.

#### Download, integrity and storage

- **FR-038**: Each included component MUST be stored as the vendor's original, unmodified file in the customer's storage account. Files MUST NOT be repackaged or pruned by file type.
- **FR-039**: Every stored file MUST be verified against the vendor's published SHA-256 over its full content. A file that fails verification MUST NOT be published or referenced by any set.
- **FR-040**: Download locations taken from vendor catalogs MUST be restricted to the vendor's official HTTPS download hosts.
- **FR-041**: Files MUST be shared by reference: one stored file per vendor component version, used by every set that includes it.
- **FR-042**: At the end of every sync run, the system MUST delete stored files that no current set, roll-back set or running deployment refers to. There MUST be no manual clean-up action.

#### Device matching and assignment

- **FR-043**: The Client MUST include the device's HP baseboard product ID (already collected) and Dell system SKU (newly collected) in its signed hardware payload.
- **FR-044**: Administrators MUST enter the Windows release (for example 24H2) of each OS image when uploading or editing it on the OS Images page. The release is not read from the image. When the image has no release, or the matched model has no onboarded set for that release, the Install drivers step MUST be skipped with a warning; there is no fallback to another release.
- **FR-045**: When a session is assigned an OS image, the Device Gateway MUST match the device to an onboarded model (HP: any ID in the model's system ID list; Dell: system SKU), select the current set for the image's release, and pin it on the session. Later syncs, policy edits, roll backs and removals MUST NOT change a pinned set.
- **FR-046**: Files of a pinned set MUST remain available until the session reaches a terminal state.
- **FR-047**: The device MUST receive one time-limited, read-only link per component of its pinned set, issued with the pinned set and scoped to that component's file only. A retry of one component MUST NOT depend on another component's link. When a link expires before the component finishes, the Client MUST be able to request a new link for that component alone. Per-component progress MUST be aggregated by the Client so the step stays within the existing progress-report budget (10 calls per 30 seconds per session).

#### Client: Install drivers step (WinPE)

- **FR-048**: The Client MUST run a new imaging step, Install drivers, after Apply image and before Configure boot.
- **FR-049**: For Dell, components with published hardware IDs MUST be downloaded only when the device has matching PCI hardware; components without hardware IDs are always processed. HP components in the pinned set MUST all be processed.
- **FR-050**: For each processed component, the Client MUST download it, verify its SHA-256 before running anything from it, and extract it with the vendor's own extractor (HP: silent extract switches with a retry using the legacy switches, success judged by the files produced; Dell 64-bit: the package's driver extract option). Extraction MUST target the applied Windows volume, never WinPE's RAM disk, and MUST be confined to the component's own folder.
- **FR-051**: The Client MUST inject the extracted INF content of WinPE and WinPE + Specialize components into the applied Windows volume using offline driver servicing. Unsigned drivers MUST NOT be allowed.
- **FR-052**: The Client MUST keep the staged content of WinPE + Specialize and Specialize components for the specialize pass, and delete the extracted content of all other components after injection.
- **FR-053**: A failed component MUST be recorded with a reason and the step MUST continue with the remaining components. Administrators MUST be able to mark, centrally and per vendor driver category (for example HP "Driver - Storage", Dell "Network"), which categories are critical. When a component in a critical category fails, the session MUST fail after the step finishes, with the failed components as the reason. Otherwise a step with failed components MUST end as Completed with warnings and the deployment MUST still succeed. No category is critical by default.
- **FR-054**: The Client MUST report each component's outcome to the session: injected, staged for Specialize, skipped by hardware match, or failed with reason.

#### Client: Specialize phase

- **FR-055**: When the pinned set has Specialize work (WinPE + Specialize or Specialize components), the Client MUST hand those components to the Prepare setup step of Setup Profiles (spec 003, FR-024 to FR-026), which lists them first in the setup manifest and adds the setup runner command to the device's answer file, whether or not a setup profile targets the device. Without Specialize work, no driver entries are added.
- **FR-056**: For driver components, the setup runner (spec 003, FR-026) MUST run as SYSTEM, process them in resolved order before any setup profile scripts, re-verify each component's staged files against the manifest, run the vendor's silent installer with a timeout, and log the exit code. Exit codes 0 and 3010 MUST count as success (3010: restart pending). The setup runner MUST NOT restart the device inside specialize, and MUST continue after a failed component.
- **FR-057**: Specialize results MUST be written to a local log on the device only; nothing is reported to the portal after the reboot. The setup runner MUST delete the staged content when done, including after failures.

#### Deployment timeline

- **FR-058**: The deployment timeline MUST show an Install drivers step with a neutral card listing the components and their outcomes. Specialize components MUST be shown as staged, without a result.
- **FR-059**: A step with failed components MUST show an amber warning marker and the label "Completed with warnings"; a skipped step MUST show the skip reason.
- **FR-060**: The timeline MUST NOT assume a fixed number of steps, so deployments recorded before this feature (without the step) still display correctly.

#### Portal: Drivers page

- **FR-061**: The portal MUST provide a Drivers page under Imaging with tabs Models, Rules, Sync and Storage.
- **FR-062**: Action names MUST be WinPE, WinPE + Specialize, Specialize and Skip everywhere in the UI. The Action column header MUST have an info icon explaining them.
- **FR-063**: The Models tab MUST show one row per onboarded model with columns Model (match keys underneath), Vendor, Windows releases, Included, Skipped, Max download, Status and an open chevron, plus a vendor filter, search and an Onboard model button. Max download MUST have an info icon stating that it is the most one device downloads and that devices without some hardware can download less; the text MUST be vendor-neutral and never model-specific.
- **FR-064**: Status MUST be a single badge showing only what needs attention (for example components waiting for approval, a failed sync item), or a muted "Sync off" badge when the model's vendor sync is off.
- **FR-065**: The model page header MUST show the vendor badge, match keys, driver set version and build time, a Windows release selector, Roll back and Remove model. An approval banner MUST appear when components wait for approval.
- **FR-066**: The model page MUST show a tile per action plus a Download per device tile, and a toolbar with search, action and category filters and an "N updated by today's sync" filter.
- **FR-067**: The component table MUST have columns: chevron; Component (name, with ID and category below, an amber New badge while waiting for approval, and a muted Withdrawn badge when the vendor no longer publishes it); Version; Last updated (date only); Action (fixed-width dropdown); Source. Every column except Component MUST have a fixed width, so names only wrap on narrow screens. All components MUST be listed (no paging or "show more"); search and filters narrow the list and the footer shows the count.
- **FR-068**: Source MUST show, in precedence order: **Locked** (lock icon and tooltip on the dropdown), **Override** (with undo), the rule's name over **Rule**, or **Default**. Values MUST NOT repeat the vendor name.
- **FR-069**: Each component row MUST expand to a three-column grid of labelled fields followed by a bordered Description section with a link to the vendor's release notes. HP fields: Offline injection, Contains an app, Silent install, Default action, Released by HP, Release type, Added to this model, Previous version, Download size. Dell fields: Installer (64-bit or 32-bit), Hardware match, Package type, Default action, Released by Dell, Criticality, Added to this model, Previous version, Download size. Install commands, SHA-256 values and supported Windows releases MUST NOT be shown.
- **FR-070**: The Rules tab MUST show the rules table (Name, Applies to, Matches, Action, Hits) and Add rule, with an info icon explaining precedence and what happens to components a vendor withdraws, followed by a Deployment failure card: per vendor, the driver categories whose failure fails the whole deployment (FR-053), changed on the spot with no Save button.
- **FR-071**: The Sync tab MUST show, in order: a Sync card (description; schedule controls Every day, Selected days with day toggles, or Manually only; time; IANA time zone; Sync now; then "Newer versions: Use automatically" and the "New components: Wait for approval" switch), with settings applied on change, no Save button and no next-sync line; a Vendors card in the same setting-row layout with one row per vendor (short line with onboarded models and match key, and a Sync switch; unsupported vendors listed with a disabled switch); and a History card.
- **FR-072**: History rows MUST be one line each: Started as an absolute timestamp (`2026-10-22 02:00`), a Result badge, and a generic summary ("4 updated, 1 failed", "1 new", "No changes"). A chevron MUST open one line per item (Model, Component linking to its row on the model page, Change, Outcome). Runs without changes MUST have no chevron.
- **FR-073**: The Storage tab MUST show tiles for total used (info icon: shared components are stored once, so the total is less than the sum of the models) and per vendor, and a one-line table per model with Vendor, Windows releases, Driver sets (current and roll-back), Stored, and Freed if removed (only files no other model uses). Storage MUST NOT be shown on the Models tab.
- **FR-074**: All Drivers tables MUST use equal, single-line row heights with truncation, and absolute timestamps.

#### Access control and audit

- **FR-075**: Only `CloudImaging.Operator` and `CloudImaging.Administrator` MUST be able to onboard, remove, edit rules and overrides, approve, roll back, change sync, vendor and critical-category settings, and use Sync now. Setting an OS image's Windows release follows the OS Images catalog permissions (Operator or Administrator). These restrictions MUST be enforced by the server, not only by the UI.
- **FR-076**: Users with read access MUST see the Drivers page without edit controls. `CloudImaging.Technician` reads; Reader, Autopilot Approver and users without a role have no access.
- **FR-077**: Every change listed in FR-075 MUST be recorded with who made it and when.
- **FR-079**: No part of the feature outside code comments (UI text, messages, logs, API responses, the device log, docs) MUST contain requirement, success criterion, task or story identifiers or pointers to them.

### Key Entities *(include if feature involves data)*

- **Vendor Setting**: a supported vendor (HP, Dell) or listed unsupported vendor (Lenovo), with its Sync on/off state and match key type.
- **Vendor Model Entry**: a model from a vendor's catalog: name, match keys (HP system IDs; Dell system SKUs), available Windows releases.
- **Onboarded Model**: vendor, display name, match keys, onboarded Windows releases. Owns overrides and resolved sets.
- **Vendor Component**: one vendor package version for a model and release: ID, name, category or type, version, release date, vendor flags used for classification, allowed actions, default action, lineage key, size, SHA-256, download location, description link, and for Dell the hardware IDs and installer architecture.
- **Rule**: name, scope (all vendors or one vendor), match kind (category, name pattern, component ID), match value, action, hit count.
- **Model Override**: onboarded model, component lineage, action.
- **Approval State**: per model and component lineage: new and waiting, or approved with the confirmed action.
- **Resolved Driver Set**: model, release, set version, fingerprint, build time, role (current or roll-back), items (component, version, action, stored file reference).
- **Stored Driver File**: one verified original vendor file per component version, referenced by sets and pinned sessions.
- **Sync Settings**: schedule mode, weekdays, time, IANA time zone, new-components approval setting.
- **Critical Category**: vendor and driver category whose component failure fails the deployment.
- **OS Image (extended)**: gains an administrator-entered Windows release.
- **Sync Run**: start time, trigger (scheduled or manual), result, items (model, component, old and new version, outcome, reason).
- **Session Driver Assignment**: session, matched model, release, pinned set version, per-component outcomes reported by the Client.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An administrator can onboard a model and review its per-action preview in under 3 minutes, and the model's driver set is ready after one sync run.
- **SC-002**: 100% of stored driver files match the vendor's published SHA-256; 0 files that fail verification are ever referenced by a set or downloaded by a device.
- **SC-003**: On reference HP and Dell models, at least 95% of deployments complete the Install drivers step without warnings, and every WinPE component of the pinned set is present in the installed OS at first boot.
- **SC-004**: 100% of in-flight deployments use the set pinned at assignment, regardless of syncs, policy edits, roll backs or removals during the deployment.
- **SC-005**: 100% of Sync now requests made while a run is active or within 60 minutes of the last run start are refused, including direct API calls.
- **SC-006**: A vendor whose sync is off receives 0 catalog or download requests during runs.
- **SC-007**: After any sync run, 0 stored files remain that no current set, roll-back set or running deployment refers to.
- **SC-008**: 0 new deployments receive a removed model's or release's sets after the removal is confirmed.
- **SC-009**: With approval on, 0 new components reach devices before an administrator approves them, while 100% of newer versions of known components are used without approval.
- **SC-010**: An administrator can tell which models need attention from the Models tab alone, without opening each model.
- **SC-011**: On a reference model over a 500 Mbit/s connection, the Install drivers step adds no more than 10 minutes to a deployment.

## Assumptions

- Read access: the todo names `CloudImaging.PortalAccess` as the reading role. In this product that role is the Portal backend's managed identity on the Operator API, not a user role, so the Drivers endpoints are called through it while user read access follows the existing catalog pattern (Technician reads, Operator and Administrator edit).
- Retention is the current set plus one previous set per model and release. Keeping more previous sets and a storage size budget per model are out of scope for this version.
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

### Risks and items still to validate

1. HP's silent extractor running in this product's WinPE: x64 is confirmed, behaviour is not.
2. HP silent installers during specialize as SYSTEM with no user signed in and no Store (Hotkey Support, Realtek audio, Intel video, HP Power Manager), and whether companion apps are provisioned for all users rather than only SYSTEM.
3. WinPE injection followed by the same component's installer (WinPE + Specialize) must not cause a second driver install or a restart request.
4. Which INFs install companion software by themselves at first boot, to avoid a double install.
5. Dell 64-bit package extraction in this product's WinPE, and Dell's silent installer during specialize for drivers that bundle an app.
6. Dock drivers with hardware match only match when the dock is connected during imaging.
7. Driver payloads of 2.0 to 3.2 GB per HP platform may exceed the constitution's 15-minute end-to-end provisioning budget. Accepted (2026-10-22); the plan still measures it.
8. Whether streamed SHA-256 verification of the largest files (around 800 MB) fits the chosen cloud compute's limits.

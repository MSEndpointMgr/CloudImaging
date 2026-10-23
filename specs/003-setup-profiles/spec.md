# Feature Specification: Setup Profiles

**Feature Branch**: `003-setup-profiles` (suggested name; no branch created yet)

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Setup profiles: registry values, scripts and an optional answer file, applied to devices before Windows starts and during Windows setup, targeted by the location set on the USB media the device started from. A new Prepare setup imaging step writes the registry values, runs or stages scripts and writes the single Windows setup answer file that the OS driver management feature (spec 005) also needs for its Specialize installers. Source of truth: the agreed mockup docs/design/os-customisation.html and todo/os-customisation-stage.md."

## Clarifications

### Session 2026-10-23

- Q: What does a profile contain? A: Registry values, PowerShell scripts and an optional answer file, plus a failure handling setting. Computer name, regional settings, built-in app removal and Windows features are out of scope.
- Q: How does a device get a profile? A: By the location set on the USB media it started from. A profile targets either all devices or one or more selected locations; a location can belong to one profile, and only one profile can target all devices. A location profile wins over the all-devices profile. A profile can never target nothing.
- Q: When does a script run? A: Each script chooses: in WinPE right after the image is applied (reported to the portal, can fail the deployment), or during Windows setup before OOBE (logged on the device only).
- Q: Who owns the answer file? A: Cloud Imaging. One file per device, written by the Prepare setup step. A profile's own answer file is the starting point and Cloud Imaging merges its single setup runner entry into it. Any answer file already inside the OS image is overwritten without a warning.
- Q: What must an answer file not contain? A: The windowsPE and offlineServicing passes, domain join, SkipMachineOOBE, SkipUserOOBE, AutoLogon and plain-text passwords. Such files cannot be saved.
- Q: Who can change profiles? A: CloudImaging.Operator and CloudImaging.Administrator, scripts included.
- Q: Versions? A: Every save is a new version. Versions can be viewed and restored; restore creates a new version. Targeting changes appear in history but are not versioned or restorable. 25 versions are kept by default, configurable by an Administrator.
- Q: Navigation name? A: Setup Profiles, under Imaging. The new imaging step is called Prepare setup.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Set registry values before Autopilot runs (Priority: P1)

An operator creates the profile "Stockholm and Gothenburg", targets the Stockholm HQ and Gothenburg Office locations, and adds registry values that must be in place before Autopilot and Intune policy arrive: stop automatic device encryption until Intune sets the cipher, set BitLocker to XTS-AES 256 for the OS drive, and turn on automatic time zone. A technician starts imaging a device whose USB media was prepared for Stockholm HQ. The deployment timeline shows a Prepare setup step listing the values written.

**Why this priority**: This is the smallest slice that solves real Autopilot limitations, and it introduces the profile, targeting, pinning and the new imaging step that every other story builds on.

**Independent Test**: Create a profile with one registry value targeting one location, image a device from media prepared for that location, and confirm at first boot that the value is present and that the timeline shows the Prepare setup step with the value as written.

**Acceptance Scenarios**:

1. **Given** no profiles exist, **When** an operator opens Setup Profiles, **Then** an empty state explains what profiles do and offers New profile.
2. **Given** a profile targeting Stockholm HQ with three registry values, **When** a device started from media prepared for Stockholm HQ is assigned an OS image, **Then** the profile's current version is pinned on the session and later edits do not change what that device receives.
3. **Given** a pinned profile, **When** the Client reaches the step after Install drivers (or after Apply image when no driver step exists), **Then** it writes every registry value to the applied Windows volume before Configure boot runs.
4. **Given** a device started from media prepared for a location no profile targets, **When** a profile targets all devices, **Then** that profile is used; **When** none does, **Then** no profile work is done.
5. **Given** a completed deployment, **When** an operator opens its timeline, **Then** a Prepare setup step shows the profile name and version and a summary of what was written.

---

### User Story 2 - Run scripts in WinPE or during Windows setup (Priority: P1)

The operator adds two scripts to the profile: "Enable Wake on LAN in HP firmware", set to run in WinPE right after the image is applied, and "Wired 802.1X for the office LAN", set to run during Windows setup so the device can reach Autopilot on a secured network. The WinPE script's result shows on the timeline; the Windows setup script's result is logged on the device.

**Why this priority**: Scripts cover the Autopilot prerequisites that registry values cannot express, and the Windows setup phase is the same mechanism the driver feature needs. Without it, spec 005's Specialize installers cannot ship.

**Independent Test**: Add one WinPE script and one Windows setup script, image a device, and confirm the WinPE script's exit code appears on the timeline, and that after OOBE the device log shows the Windows setup script ran before the first sign-in.

**Acceptance Scenarios**:

1. **Given** a script set to run in WinPE, **When** the Prepare setup step runs, **Then** the script runs as SYSTEM in WinPE with the Windows drive available to it, within its timeout, and its exit code is reported; exit code 0 is success.
2. **Given** a script set to run during Windows setup, **When** the Prepare setup step runs, **Then** the script is staged on the applied volume and runs as SYSTEM during the specialize pass, after any driver installers and before OOBE.
3. **Given** several scripts, **When** they run, **Then** they run in the order shown in the profile within each phase; all WinPE scripts run before the restart.
4. **Given** a script that exceeds its timeout, **When** the timeout passes, **Then** the script is stopped, recorded as failed, and the next script runs.
5. **Given** a script that tries to restart the device, **Then** the restart does not happen inside the imaging step or inside Windows setup.

---

### User Story 3 - Add a custom answer file without breaking Autopilot (Priority: P2)

The operator pastes an unattend.xml with a registered organisation, a time zone and an input locale. The editor confirms it is valid and lists its passes. Another operator tries to save a file containing a domain join and a windowsPE pass; the editor lists both problems with line numbers and how to fix them, and the profile cannot be saved.

**Why this priority**: Gives customers an escape hatch for Windows setup settings this feature has no control for, while protecting the Autopilot flow. Devices are usable without it.

**Independent Test**: Save a valid answer file and confirm the device's Panther\unattend.xml contains its settings plus the Cloud Imaging entry; then try to save each refused construct and confirm both the portal and a direct API call refuse it.

**Acceptance Scenarios**:

1. **Given** a valid answer file, **When** it is saved, **Then** the profile shows one Answer file row with its passes, size and last modified time.
2. **Given** an answer file containing any refused construct (FR-020), **When** the operator edits it, **Then** every problem is listed at once with its line number and a one-sentence fix, and Done and Save are disabled.
3. **Given** the same file sent directly to the API, **When** it is saved, **Then** the request is refused with the same problems.
4. **Given** a device whose profile has an answer file and which has Windows setup work, **When** Prepare setup runs, **Then** the device's answer file contains the profile's settings plus exactly one Cloud Imaging setup runner command, ordered before the profile's own commands.

---

### User Story 4 - Review history and restore an earlier version (Priority: P2)

A script change in version 6 breaks a site. The operator opens History, expands version 5 to see what changed, views it read-only, and restores it. Version 8 is created with version 5's content. Devices imaged from then on get version 8; deployments already running keep the version they started with.

**Why this priority**: Every save changes what runs as SYSTEM on every targeted device, so recovering from a bad change must be quick and auditable. The feature works without it, so it ranks below the core flow.

**Independent Test**: Save three versions, restore the first, and confirm a fourth version with the first one's content is created, current, and used by the next deployment, while a running deployment keeps its pinned version.

**Acceptance Scenarios**:

1. **Given** a profile with seven versions and a targeting change, **When** the operator opens History, **Then** a Versions tab and a Targeting tab are shown, each a one-line-per-row table.
2. **Given** the Versions tab, **When** a row is expanded, **Then** labelled fields show what changed in Registry values, Scripts, Answer file and Failure handling, with "No change" where nothing changed.
3. **Given** a version a running deployment uses, **Then** it shows an In use status with the number of deployments in its tooltip; the current version shows Current.
4. **Given** an earlier version, **When** the operator presses Restore and confirms, **Then** a new version with that content becomes current and targeting is not changed.

---

### User Story 5 - See which profile each device gets before imaging (Priority: P3)

A technician selects four coupled devices and an OS image and presses Start imaging. The confirmation overlay lists which setup profile each group of devices will get, by the location on its USB media, so media prepared for the wrong location is caught before the disk is wiped.

**Why this priority**: Prevents a wrong-site deployment, but the feature works without it.

**Independent Test**: Couple devices from media prepared for two locations and from media without a location, start imaging, and confirm the overlay lists the right profile per group.

**Acceptance Scenarios**:

1. **Given** devices whose media targets different profiles, **When** Start imaging is pressed, **Then** the overlay lists each profile with its device count and no version numbers.
2. **Given** no profiles exist, **Then** the overlay shows no profile lines.

### Edge Cases

- A device's media has no location, or a location no profile targets: the all-devices profile applies; if there is none, no profile work is done.
- A device has no profile but its driver set (spec 005) has Specialize installers: Prepare setup still runs to write the answer file for the driver installers. A device with neither skips the step.
- The OS image already contains Windows\Panther\unattend.xml: it is overwritten. This is neither a warning nor a timeline entry.
- A WinPE script runs on a device whose boot media has no PowerShell: the script fails with that reason and the failure handling setting decides the result.
- A registry hive of the applied image cannot be loaded (for example the default user profile is missing): the affected values fail with a clear reason.
- A location targeted by a profile is deleted: handled as FR-007 describes, so a profile never targets nothing.
- An operator tries to target a location another profile already targets: the location is shown disabled in the picker with that profile named in its tooltip.
- An operator tries to target all devices while another profile does: the option is disabled with the other profile named.
- A profile is edited or restored while devices are imaging: running sessions keep their pinned version and its files until they end.
- A profile is deleted while devices are imaging: running sessions keep their pinned version; the deletion is confirmed through the impact overlay first.
- The answer file cannot be written to the applied volume: the step fails following the failure handling setting; without a profile it completes with warnings and names the driver installers that will not run.
- Retention is lowered below the number of versions a profile has: older versions are removed at that profile's next save, never immediately, and versions in use by running deployments are always kept.
- The pinned profile cannot be downloaded or fails its integrity check after retries: the session fails before the disk is formatted, with a message to check the network and start imaging again, so the device keeps its current installation.
- The pinned profile was saved in a format the device's Client does not understand: the session fails before the disk is formatted, with a message to update the boot image.
- A device starts from a boot image whose Client predates Setup Profiles while a profile is pinned: the deployment runs without profile work and the timeline shows Prepare setup as Not run, with a tooltip asking to update the boot image.
- The OS image is not generalized (captured without Sysprep generalize) and the device has Windows setup work: Windows setup never runs the specialize pass, so the answer file item fails with that reason and the failure handling setting decides the result.
- A script deliberately works around the restart guard (for example through a service it creates): the guard does not stop it. Only Operator and Administrator can edit scripts and every change is recorded, as for any content that runs as SYSTEM.

## Requirements *(mandatory)*

### Functional Requirements

#### Profiles and targeting

- **FR-001**: The portal MUST provide a Setup Profiles page under Imaging, after Recovery Images (after Drivers once that page exists), listing profiles with columns Name, Targeting, Registry values, Scripts, Last modified and Actions (edit, delete), a description line, and a New profile button. Rows MUST be one line high with absolute timestamps.
- **FR-002**: A profile MUST target either All devices or Selected locations with at least one location. Save MUST be refused when Selected locations has none. Profile names MUST be unique among existing profiles, ignoring case.
- **FR-003**: A location MUST belong to at most one profile, and at most one profile MAY target All devices. The location picker MUST show locations targeted by another profile as disabled, naming that profile in a tooltip; the All devices option MUST be disabled with the reason when another profile uses it.
- **FR-004**: The location picker MUST match the Wi-Fi network location picker: search, Select all, Clear, each location shown with its name, region and country only, and a footer with the selected count.
- **FR-005**: The profile for a device MUST be resolved from the location set on the USB media the device started from: the profile targeting that location, otherwise the All devices profile, otherwise none.
- **FR-006**: The Targeting column MUST show location names as outline badges (first location plus "+N more" with a tooltip) or an "All devices" outline badge, using the same component as the Wi-Fi networks table.
- **FR-007**: Deleting a location MUST remove it from any profile's targeting and record the change in that profile's history, except that deleting the only location of a Selected locations profile MUST be refused with a message naming the profile.

#### Registry values

- **FR-008**: A profile MUST hold registry values, each with a name stating why it is set, a hive (HKLM SOFTWARE, HKLM SYSTEM or Default user), a key, a value name, a type (String, Expandable string, Multi-string, DWORD, QWORD) and data. Binary values and deleting values are out of scope.
- **FR-009**: The registry table MUST show Name, Value (full path, with value name and type in the tooltip), Data and Actions.
- **FR-010**: The Client MUST write all registry values to the applied Windows volume in WinPE during the Prepare setup step and report each value's outcome.

#### Scripts

- **FR-011**: A profile MUST hold PowerShell scripts, each with a name, a Runs setting (In WinPE or During Windows setup), a timeout and the script text, limited to 64 KB per script. The Runs and Timeout dropdowns MUST sit beside the Name field so the script editor gets the full dialog width.
- **FR-012**: The scripts table MUST show Order, Name, Runs, Timeout, Last modified and Actions (move up, move down, edit, delete), reordering the same way as the Wi-Fi networks table. Order applies within each phase.
- **FR-013**: In WinPE scripts MUST run as SYSTEM after the registry values are written, in order, each within its timeout, with the applied Windows drive available to them. Exit code 0 MUST count as success. Each outcome (exit code, duration, last output line) MUST be reported to the session.
- **FR-014**: During Windows setup scripts MUST be staged on the applied volume and run as SYSTEM during the specialize pass by the setup runner (FR-026), after any driver installers, in order, each within its timeout. Their results MUST be written to a log on the device only.
- **FR-015**: Scripts MUST run without the privileges to shut down or restart the device, during the Prepare setup step and during Windows setup, and an exit code that requests a restart (1641 or 3010) MUST count as a failure. When restarts cannot be blocked on a device, its scripts and driver installers MUST NOT run and MUST fail with that reason. This guard prevents accidental restarts; it is not a security boundary against a script written to work around it.
- **FR-016**: The script dialog MUST warn that scripts are readable by everyone with access to the page and must not contain secrets.
- **FR-017**: Boot images generated by Media Builder MUST include PowerShell so In WinPE scripts can run. A device whose boot media lacks it MUST fail its In WinPE scripts with that reason.

#### Answer file

- **FR-018**: A profile MAY hold one answer file (unattend.xml), edited in a dialog with an XML editor and Upload file, up to 256 KB. The profile page MUST show it as one row with File, Passes, Size, Last modified and Actions (edit, remove).
- **FR-019**: The answer file MUST be validated as it is edited, and again by the server on save. Every problem MUST be listed at once with its line number and a one-sentence fix, and Done and Save MUST stay disabled while any problem exists.
- **FR-020**: The following MUST be refused: content that is not well-formed XML or not an answer file; the windowsPE and offlineServicing passes; the domain join component (Microsoft-Windows-UnattendedJoin); SkipMachineOOBE and SkipUserOOBE; AutoLogon; and plain-text passwords anywhere in the file.
- **FR-021**: Each device MUST get exactly one answer file, written by the Prepare setup step to Windows\Panther\unattend.xml on the applied volume, overwriting any file the OS image contained without a warning.
- **FR-022**: The device's answer file MUST start from the profile's answer file, or an empty one. When the device has driver installers (spec 005) or During Windows setup scripts, the Prepare setup step MUST add exactly one command that starts the setup runner in the specialize pass for the device's architecture, creating the pass and component if missing, ordered before the profile's own commands, which keep their relative order.
- **FR-023**: The driver part of the answer file MUST never be shown or edited in the portal.

#### Prepare setup step and setup runner

- **FR-024**: The Client MUST run a new imaging step, Prepare setup, after Install drivers (or after Apply image when no Install drivers step exists) and before Configure boot. It MUST run when the device has a profile or driver installers, and be skipped otherwise.
- **FR-025**: The Prepare setup step MUST, in order: write registry values, run In WinPE scripts, stage During Windows setup scripts and driver installer payloads, then write the answer file.
- **FR-026**: The setup runner MUST be part of the Client release (not of the OS image), copied by the Prepare setup step to the applied volume with a manifest listing driver installers then During Windows setup scripts, each with its SHA-256 and timeout. During the specialize pass it MUST verify each item against the manifest before running it, run it with its timeout, continue after failures, log every result to a device log, and delete the staged content when it ends, including after failures.
- **FR-027**: Each profile MUST have a failure handling setting, Complete with warnings (default) or Fail the deployment, applying to registry values that cannot be written, In WinPE scripts that fail or time out, and an answer file that cannot be written. During Windows setup results MUST never change the deployment result. A device without a profile MUST treat these failures as Complete with warnings.
- **FR-028**: With Fail the deployment, a failure MUST stop the session before Configure boot so the device does not boot into a partly prepared Windows, and the existing retry path MUST apply. The step MUST stop at the first failed item and report every later item as not run, with that reason.
- **FR-029**: Per-item progress MUST be aggregated by the Client so the step stays within the existing progress-report budget.

#### Pinning and versions

- **FR-030**: Every save of a profile's registry values, scripts, answer file or failure handling MUST create a new version. The editor MUST show the current version and its last modified time and author, and a save bar with Discard and "Save as version N". When only the name or targeting changed, the save bar MUST read "Save changes" and no version is created.
- **FR-031**: Start imaging MUST pin the current version of the resolved profile on the session. Later edits, restores or deletion MUST NOT change a pinned version, and its content MUST remain available until the session reaches a terminal state.
- **FR-032**: Targeting MUST NOT be versioned. Targeting changes MUST be recorded in history with who and when, and MUST NOT be restorable.
- **FR-033**: A History dialog MUST offer a Versions tab (columns: expand chevron, Version, Modified, Modified by, Changes as a count, Status, Actions) and a Targeting tab (columns: Modified, Modified by, Change). Expanding a version MUST show labelled fields for Registry values, Scripts, Answer file and Failure handling. Status MUST be a green Current badge or a muted In use badge with the number of running deployments in its tooltip.
- **FR-034**: View MUST open a version read-only in the editor layout. Restore MUST, after confirmation, save that version's content as a new current version without changing targeting.
- **FR-035**: Each profile MUST keep its most recent versions up to a retention count, plus any older version a running deployment uses. The retention count MUST default to 25, be settable from 5 to 100 by an Administrator on the Configuration page, and be applied at each profile's next save.
- **FR-036**: Duplicate MUST open a new profile in the editor, prefilled with the current version's content, a proposed unique name and no targeting. Nothing MUST be stored until the operator sets targeting and saves (FR-002); the new profile's first version MUST record the profile and version it was duplicated from. Delete MUST be confirmed through the portal's impact overlay stating which locations lose the profile.

#### Devices page and timeline

- **FR-037**: The Start imaging confirmation overlay MUST list, by the location on each device's USB media, which setup profile each group of devices gets, without version numbers. With no profiles it MUST show no profile lines.
- **FR-038**: The deployment timeline MUST show a Prepare setup step with a neutral card naming the profile and version, a summary (registry values written, WinPE scripts succeeded or failed, items queued for Windows setup) and on request one line per item with its outcome. Failures MUST be listed first with the full reason in a tooltip.
- **FR-039**: A step with failures under Complete with warnings MUST show the amber warning marker and "Completed with warnings"; under Fail the deployment it MUST show the failed marker and the session MUST end as failed.
- **FR-040**: The timeline MUST NOT assume a fixed number of steps, so deployments recorded before this feature still display correctly.

#### Access control and audit

- **FR-041**: Only CloudImaging.Operator and CloudImaging.Administrator MUST be able to create, edit, duplicate, delete and restore profiles, including scripts and answer files. Only CloudImaging.Administrator MUST be able to change the retention count. These restrictions MUST be enforced by the server.
- **FR-042**: CloudImaging.Technician MUST be able to view profiles without edit controls. CloudImaging.Reader, CloudImaging.AutopilotApprover and users without a role MUST have no access.
- **FR-043**: Every change in FR-041 MUST be recorded with who made it and when.
- **FR-044**: No part of the feature outside code comments (UI text, messages, logs, API responses, the device log, docs) MUST contain requirement, success criterion, task or story identifiers or pointers to them.

### Key Entities *(include if feature involves data)*

- **Setup Profile**: name (unique, ignoring case), targeting (all devices, or a set of locations), current version, failure handling of the current version, last modified time and author.
- **Profile Version**: profile, version number, registry values, scripts, answer file, failure handling, author, time, generated change summary.
- **Registry Value**: name, hive, key, value name, type, data.
- **Script**: name, runs (In WinPE or During Windows setup), order, timeout, PowerShell text.
- **Answer File**: XML content, passes it contains, size.
- **Targeting Change**: profile, change (location added or removed, all devices on or off), author, time.
- **Retention Setting**: number of versions kept per profile.
- **Session Setup Assignment**: session, resolved profile, pinned version, per-item outcomes reported by the Client.
- **Setup Manifest** (on the device): ordered driver installers and During Windows setup scripts with SHA-256 and timeout, read by the setup runner.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can create a profile with three registry values and one script and target two locations in under 5 minutes.
- **SC-002**: 100% of deployments use the profile version pinned at Start imaging, regardless of edits, restores or deletions during the deployment.
- **SC-003**: 0 answer files containing a refused construct are saved, through the portal or the API.
- **SC-004**: 100% of registry value and In WinPE script failures appear on the deployment timeline with a reason.
- **SC-005**: The Prepare setup step adds no more than 30 seconds to a deployment, excluding the run time of the profile's own scripts.
- **SC-006**: An operator can find and restore the version before a bad change in under 1 minute.
- **SC-007**: On every device that has driver installers or During Windows setup scripts, exactly one setup runner command is present in the answer file at first boot.

## Assumptions

- Technician read access follows the existing catalog pattern (Technician reads OS images and drivers, Operator and Administrator edit).
- Location targeting relies on the location already carried by USB media and reported on the session; no new device identity is needed.
- Timeouts are selectable from 1 to 30 minutes, default 5. A profile holds at most 200 registry values and 20 scripts.
- Scripts are stored in the profile version, not in a separate script catalog; uploading scripts to a catalog is a later extension.
- The setup runner is a PowerShell script, because full Windows always includes PowerShell; it is versioned with the Client and reaches devices through the existing boot image self-update.
- Computer naming, regional settings, built-in app removal, Windows features, domain join and downloading Windows are out of scope.

### Dependencies

- **Operator role (spec 004)**: CloudImaging.Operator must exist. Until it does, Administrator alone holds the FR-041 permissions.
- **OS driver management (spec 005)**: this feature provides the answer file and setup runner that 005's Specialize installers use. Spec 005 must refer to the Prepare setup step and setup runner defined here instead of a separate unattend owner. This feature ships without 005; then the step runs after Apply image and the runner only runs scripts.
- **Media Builder**: boot images must include PowerShell for In WinPE scripts (FR-017).

### Risks and items still to validate

1. Writing to the default user hive of an applied image in WinPE across Windows 11 23H2, 24H2 and 25H2.
2. Network availability during the specialize pass for During Windows setup scripts that need it, which depends on the NIC driver being present in the image or injected by spec 005.
3. Boot image size and start-up time once PowerShell is added to WinPE, against the constitution's start-up budget.

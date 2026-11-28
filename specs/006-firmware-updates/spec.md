# Feature Specification: HP BIOS Updates During Imaging

**Feature Branch**: `006-firmware-updates`

**Created**: 2026-11-14

**Updated**: 2026-11-22 (every device on battery waits before Prepare disk; Start imaging no longer mentions firmware)

**Status**: In implementation

**Input**: User description: "HP system BIOS updates during imaging, upgrade only. The Client stages the update with HP's Client Management Script Library as the last step before recovery, and the BIOS flashes during the restart that already ends imaging. Media Builder adds HP's tools only after the operator accepts HP's licence. Imaging Core reads HP's per-platform BIOS catalog with the driver sync. The portal gets a Drivers & Firmware area: each model page has Drivers and Firmware tabs, the BIOS is a component row with the same details and version history as a driver component, and the Sync tab decides whether newer versions go to a pre-production location first. Approved mockup: docs/design/drivers-firmware-firmware-tab.html. Research: todo/firmware-updates.md."

## Clarifications

### Session 2026-11-14

- Q: What does version 1 update? A: The system BIOS of HP x64 devices, upgrade only. It never downgrades and never applies the installed version again. Dell, Lenovo, ARM64 and other firmware (TPM, Thunderbolt, SSD, dock) are later phases.
- Q: Can the firmware step fail a deployment? A: No. Any skip or failure ends the step as Completed with warnings, because Windows is already applied.
- Q: How are HP Sure Admin devices handled? A: Without payload signing. HP's tools ask the BIOS whether an upgrade needs authorization ("BIOS Update Credential Policy"). When it does, the device is skipped with an outcome that says so.
- Q: How do HP's tools reach the boot media when HP's licence forbids redistributing them? A: Media Builder downloads a pinned, tested version from the PowerShell Gallery on the operator's machine after the operator accepts HP's licence, checks HP's signature on every script and library, and adds it with the WinPE-StorageWMI and WinPE-SecureBootCmdlets optional components. The library never passes through Cloud Imaging's releases or storage.

### Session 2026-11-15

- Q: How is the model page laid out? A: Header with the title, the match key badge, Check for updates and a More menu. The tab row holds only Drivers and Firmware. The Firmware tab's More menu holds only Remove model.
- Q: How does an administrator turn BIOS updates on? A: Per model, with the Action dropdown of the BIOS row: Update at restart or Skip (the default). There are no rules for firmware.
- Q: Who gets a newer BIOS a sync finds? A: A Sync tab setting, Newer versions: Available immediately (every location) or Pre-production first (only devices whose USB media carries the pre-production location, until someone releases it on the model page). The same idea as Wait for testing for driver sets.
- Q: Which vendors? A: HP only.
- Q: What if the device runs on battery? A: AC power is a pre-flight check, required only for a device that gets a BIOS update: it cannot register until the AC adapter is connected (Try again re-registers). If the adapter is removed during imaging, the Client skips the update and imaging completes.
- Q: When is the BIOS decided? A: At registration, so the AC power check runs before imaging and Start imaging can say how many devices update their BIOS. Later changes do not change a registered session.
- Q: Where do release notes come from? A: From the BIOS SoftPaq in HP's driver reference catalog, matched by version.

### Session 2026-11-22

- Q: Does Start imaging still remind the operator about AC power? A: No. The overlay names the OS image and says nothing about firmware; the device makes sure it is on AC power itself.
- Q: What does a device on battery do when imaging starts? A: Every device that reports a battery, with or without a BIOS update, waits before Prepare disk until the AC adapter is connected, then continues by itself. The device shows "Connect the AC adapter" and the portal shows "Waiting for the AC adapter" on the step. The AC power pre-flight check at registration stays for devices that get a BIOS update, and Update firmware still skips the BIOS when the adapter is pulled later.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Update an HP model's BIOS during imaging (Priority: P1)

An Operator opens Drivers & Firmware, the HP EliteBook X G2a model, and its Firmware tab. The row "System BIOS" shows 01.02.04 from HP's catalog, Action Skip, Source Default. They choose Update at restart. The BIOS downloads and is verified. A technician images a device of that model with BIOS 01.01.19 from boot media that has HP's tools. After Configure boot the Client runs Update firmware, stages 01.02.04 and the completion screen says to keep the device on AC power. The BIOS flashes during the restart into Windows setup. The timeline says "Staged 01.01.19 to 01.02.04. It flashes at the restart into Windows setup."

**Independent Test**: One HP model with Update at restart, one device with an older BIOS, boot media with HP's tools: the timeline note and completion screen text appear, and the device runs the new BIOS when Windows setup starts.

**Acceptance Scenarios**:

1. **Given** an onboarded HP x64 model, **When** a sync or Check for updates runs, **Then** HP's BIOS catalog of every platform ID of the model is read and the Firmware tab lists one System BIOS row per platform with Version, Last updated, Action (Skip) and Source (Default). Nothing is downloaded.
2. **Given** the row, **When** an Operator chooses Update at restart, **Then** the newest version is downloaded from HP, verified against HP's SHA-256 and stored under HP's file name, and the row shows it is downloading until it is stored.
3. **Given** Update at restart and a stored file, **When** a device of the model with an older BIOS registers, **Then** the session records the version it gets and the version it has; later changes do not change it.
4. **Given** that session, **When** Configure boot completes, **Then** the Client downloads the file, verifies it, stages it with HP's tools and reports "Staged <from> to <to>. It flashes at the restart into Windows setup."
5. **Given** an update was staged, **Then** the completion screen says "The BIOS updates during the restart. Keep the device on AC power and don't turn it off."
6. **Given** a device whose BIOS is the same or newer, **Then** it gets no update.

### User Story 2 - Pre-production first (Priority: P1)

An Administrator sets Newer versions to Pre-production first and the pre-production location to IT Lab on the Sync tab. A sync finds BIOS 01.02.04 for a model that updates at restart. The model's Firmware tab shows a banner "BIOS 01.02.04 (2026-07-30). In pre-production at IT Lab. Other locations get 01.01.19." with Release. Devices from IT Lab get 01.02.04; others get 01.01.19. After testing, an Operator presses Release and every location gets it.

**Acceptance Scenarios**:

1. **Given** Pre-production first can only be chosen once a pre-production location is set, **When** it is chosen, **Then** newer versions found for models that update at restart wait in pre-production.
2. **Given** a version in pre-production, **Then** devices whose USB media carries the pre-production location get it and other devices get the released version.
3. **Given** Release on the banner, **Then** every location gets the version, and the version history records Release by that user.
4. **Given** versions in pre-production, **When** Newer versions is switched to Available immediately, **Then** a confirmation names how many are released, and they are released.

### User Story 3 - Skip devices that cannot be updated safely, and say why (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a device that gets a BIOS update and runs on battery when it registers, **Then** the AC power pre-flight check fails, the device shows "AC power: On battery" with "Connect the AC adapter", and Try again registers it again.
2. **Given** a device on battery when the step runs, **Then** the outcome is "Skipped: the device runs on battery." and HP's tools are not run.
3. **Given** any device that reports a battery when imaging starts, **Then** it waits before Prepare disk with "Connect the AC adapter" on the device and "Waiting for the AC adapter" on the step in the portal, changes nothing on the disk, and continues by itself once the adapter is connected.
4. **Given** HP's tools refuse, **Then** the outcome names the reason: UEFI mode, BIOS version locked, Sure Admin authorization required, BIOS password set, more than one EFI system partition, EFI system partition not found or full, file does not match the model, BIOS refused the update, tools incomplete, or HP's message.
5. **Given** boot media without HP's tools or without PowerShell, **Then** the outcome says so and how to fix it.
6. **Given** any skip or failure, **Then** the step is Completed with warnings, the session completes, and the completion screen says the BIOS was not updated.

### User Story 4 - Include HP's tools in boot media after accepting HP's licence (Priority: P1)

**Acceptance Scenarios**:

1. **Given** Generate Boot Image for x64, **Then** the HP Firmware Updates card offers "Include HP firmware tools" and "I accept HP's licence terms", both off, with a button that opens HP's licence.
2. **Given** the tools are included and the licence is not accepted, **Then** Generate is disabled.
3. **Given** both, **When** generation runs, **Then** Media Builder downloads HP.Private, HP.Utility, HP.ClientManagement and HP.Firmware at the versions the pinned HPCMSL release requires, refuses any file that is not validly signed by HP, and adds them with WinPE-StorageWMI and WinPE-SecureBootCmdlets.
4. **Given** ARM64, **Then** the option is disabled with a note.

### User Story 5 - Version history and roll back (Priority: P2)

**Acceptance Scenarios**:

1. **Given** the BIOS row's details, **Then** they show Platform ID, Criticality, Size, File and Released, the Release notes link when HP publishes one, and the Version history with Version, From, To, Change, By and Status (no Driver sets column).
2. **Given** Roll back component, **Then** an earlier version is held for every location until HP publishes a newer one or someone undoes it; it is downloaded again when not stored. Devices never get an older BIOS than they have.

### User Story 6 - Start imaging stays free of firmware (Priority: P2)

**Acceptance Scenarios**:

1. **Given** devices that get a BIOS update, **When** the Start imaging overlay opens, **Then** it names the OS image and shows nothing about firmware or AC power, and Coupled Devices is unchanged.

### Edge Cases

- A platform ID without a BIOS catalog at HP is left out silently; one that was read before and fails is a failed sync item.
- Catalog entries without a SHA-256 or without a `.bin` file are left out; versions compare per number, not as text.
- HP pulls a version in pre-production: it stops waiting.
- A file that fails to download: the row shows the reason; devices are not pinned to a version whose file is not stored.
- Removing a model removes its BIOS rows and history; a running deployment pinned to one skips the update.
- Clean-up keeps the versions devices may get, a held roll back, the previous versions to keep, and versions pinned to active sessions.
- The BIOS step runs after Configure boot and before Configure recovery, so a session cannot complete while it runs.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The portal navigation entry MUST read "Drivers & Firmware". A model page MUST have the header Option A (title, match key badge, Check for updates, More menu) and Drivers and Firmware tabs kept in `?view=`.
- **FR-002**: Imaging Core MUST read `ftp.hp.com/pub/pcbios/<id>/<id>.xml` for every platform ID of every onboarded HP x64 model during each sync and model check, keep the newest 30 releases with a SHA-256, and record new versions as sync items.
- **FR-003**: The BIOS action MUST be Skip by default and MUST only change through the Firmware tab: Update at restart or Skip. Choosing the default clears the override.
- **FR-004**: Files MUST be downloaded only for versions devices may get, verified against HP's SHA-256, stored under HP's file name, and kept by clean-up while referenced.
- **FR-005**: The Sync tab MUST offer Newer versions (Available immediately, Pre-production first) and Pre-production location; Pre-production first MUST require a location. Switching back to Available immediately MUST release every version in pre-production after a confirmation.
- **FR-006**: A version in pre-production MUST show a banner on the model's Firmware tab with Release.
- **FR-007**: At registration, Imaging Core MUST pin the BIOS version for a device of an active HP x64 model that updates at restart, when the device reports an older version (or none) and the file is stored, choosing the pre-production version for devices of the pre-production location.
- **FR-008**: The AC power pre-flight check MUST be required only for a device that gets a BIOS update, independent of the administrator's pre-flight switches; only a reported battery fails it. It MUST be listed only for such a device, and a Client that reports no power source (older than BIOS updates) MUST NOT be given a BIOS update, since it can read neither the check nor the step.
- **FR-009**: The Device Gateway MUST serve `GET /api/v1/sessions/{id}/firmware` with a 60-minute read link, or 204 when the session gets no BIOS update.
- **FR-010**: The Client MUST run Update firmware after Configure boot when the session has a BIOS, check AC power and the installed version again, run HP's tools in a restricted PowerShell process that cannot restart the device, map HP's messages to outcomes, and never fail the session.
- **FR-011**: Media Builder MUST add HP's tools only after the operator accepts HP's licence, download them on the operator's machine at a pinned version, and refuse files without HP's valid signature.
- **FR-012**: The BIOS row MUST mirror a driver component row: details, release notes link from HP's driver reference catalog, version history and Roll back component. The history's current version MUST say whether its file is Stored, Downloading, Download failed or Not downloaded (while the action is Skip), never Downloads on roll back.
- **FR-013**: The deployment timeline MUST show Update firmware with the BIOS from and to versions and the device's outcome. Start imaging MUST NOT show firmware or AC power details.
- **FR-014**: Changes MUST be recorded in the change log with the user. Reads need Technician, changes need Operator.
- **FR-015**: The Client MUST hold every device that reports a battery before Format disk, with or without a BIOS update, until the AC adapter is connected, and then continue by itself. It MUST show "Connect the AC adapter" on the device and report "Waiting for the AC adapter" as the Prepare disk step's note until it continues. A power source that cannot be read MUST NOT hold imaging.

### Key Entities

- **BIOS record** (per model and platform ID): newest release, kept releases, action, released version, version in pre-production, roll back.
- **BIOS version history** (per model and platform ID): version, from, to, change (Onboard, Sync, Release, Roll back), by.
- **Session BIOS pin**: model, platform ID, version, installed version.

## Success Criteria *(mandatory)*

- **SC-001**: A device of a model that updates at restart, imaged from boot media with HP's tools on AC power, starts Windows setup on the new BIOS without an extra restart.
- **SC-002**: Every skip or failure has a named outcome on the timeline and the deployment still completes.
- **SC-003**: No Cloud Imaging release or storage account contains HP's tools.

## Assumptions

- HP's tools stage the update with `-Offline` in Windows PE once WinPE-StorageWMI and WinPE-SecureBootCmdlets are present (to confirm on the EliteBook 8 G1a).
- The BIOS checks HP's signature before it flashes, so a partial staging is not applied.

### Out of scope (later phases)

- Dell, Lenovo and ARM64; TPM, Thunderbolt, SSD and dock firmware.
- HP Sure Admin payload signing and BIOS password support.
- Confirming the flash result after the restart.

# TODO: OS driver management (vendor catalogs, offline and deferred install)

Status: **design direction agreed, HP spike (2026-10-21) and Dell and Lenovo spikes (2026-10-22)
done, portal mockup iterated (`docs/design/os-drivers.html`, gitignored), not implemented.**

Priority: **highest open gap**, jointly with [os-customisation-stage.md](os-customisation-stage.md).
The offline phase below is the first consumer of that document's mechanism A (servicing the applied
volume in WinPE), and the deferred phase uses its `specialize` hook. Both must share one owner of
`Panther\unattend.xml`.

Scope: **HP first, then Dell.** Lenovo is deferred to a later version (see "Dell and Lenovo").
Microsoft Surface is out of scope: it has no
machine-readable catalog and we do not build web-site scrapers. Baking drivers into `install.wim`
is rejected (see "Rejected alternatives").

## Context / why this doesn't already exist

Today the OS image pipeline is intentionally minimal end-to-end:

- **Publish** (`OsImageUploadFunctions.PublishUpload`, Imaging Core API): accepts `.wim` as-is, or
  extracts `sources\install.wim`/`install.esd` from an uploaded `.iso`. Nothing else in the ISO
  is inspected or retained (no `$OEM$`, no `Autounattend.xml`, no `sources\$OEM$\$1\Drivers`).
- **Apply** (`ImageApplyService.ApplyAsync`, Client, running in WinPE): downloads the WIM, verifies
  its SHA-256, and runs `dism.exe /Apply-Image /ImageFile:{wim} /Index:1 /ApplyDir:{volume}\`.
  Nothing else happens to the applied volume before the device reboots into the full OS.

The **only** existing driver injection anywhere in this product targets the *boot image* (WinPE),
not the OS image. See `BootImageGenerationService.InjectDriversAsync` (Media Builder, T064/FR-051c):
a technician optionally points at a local "driver root" folder and every `.inf` beneath it is
injected into the mounted WinPE WIM via `dism.exe /Image:{mount} /Add-Driver /Driver:{root}
/Recurse /ForceUnsigned`, purely so WinPE itself can see the disk/NIC on hardware whose storage
controller (e.g. Intel VMD/RST) or NIC isn't in WinPE's in-box driver set. That mechanism is not
reachable from, or wired to, the OS image at all.

Today, any drivers the *installed* OS needs after first boot are expected to come from Windows
Update / PnP at OOBE. This is fine for common/in-box hardware but is a gap for:

1. OEM/vendor drivers that Windows Update doesn't have yet (very new hardware) or that require a
   specific vendor package (docking stations, fingerprint readers, etc.).
2. Fully offline/air-gapped imaging scenarios where the device won't reach Windows Update before
   a driver-dependent step (e.g. a NIC needed for the *next* provisioning step, like Entra
   join/Autopilot).

## How others do it (research, 2026-10-20)

- **Foundry OSD**: per pack, a strategy resolver picks *offline INF* (DISM into the applied volume
  in WinPE, and into WinRE) or *deferred* (installer staged on disk, run from a `specialize`
  `RunSynchronous` hook before OOBE). Deferred exists only for packs WinPE cannot unpack
  (Lenovo `.exe`, Surface `.msi`).
- **OSDCloud**: same split. Deferred packs are installed in `specialize` by pointing
  `HKLM\...\UnattendSettings\PnPUnattend\DriverPaths` at the folder and running
  `pnpunattend.exe AuditSystem /L`.
- **Driver Automation Tool (10.x)**: two build types, the vendor's monthly "SCCM driver pack" or
  "Latest Drivers" built from individual components (HP SoftPaqs, Dell Command | Update catalog,
  Lenovo per-model catalog), leaving out components that are applications. Rebuilds only when the
  component set fingerprint changes, removes duplicate components, validates archive paths and
  catalog values, requires SHA-256 (code signature as fallback where only MD5 exists), keeps N
  versions. Extracts HP SoftPaqs with HP's own `/s /e /f` switches, 7-Zip only as a fallback.
- Nobody bakes drivers into `install.wim`, and nobody relies on unattend `DriverPaths` (only valid
  in `offlineServicing`/`auditSystem`, neither of which runs for a DISM-applied image going to OOBE).

## Design overview

1. **Catalog sync** (scheduled): read vendor catalogs into a model list and, per onboarded model
   and OS release, a component list.
2. **Onboarding** (Portal): an administrator onboards a model from the vendor's list and picks the
   OS releases it is deployed with. Models that are not onboarded get no driver step.
3. **Policy**: global defaults, then vendor, then model. Decides an action per component.
4. **Resolution**: per model and OS release, the resolved component set (component, version,
   action) plus a fingerprint. A new set is only produced when the fingerprint changes; the
   previous set is kept for rollback.
5. **Download**: the original vendor files into blob storage, verified against the vendor's
   SHA-256. No repackaging in v1 (see "Payload format and size reduction").
6. **Assignment**: Device Gateway matches the device to an onboarded model and records the
   resolved set on the session, so a later policy edit cannot change an in-flight deployment.
7. **Offline phase** (Client, WinPE): new step between `ApplyImage` and `ConfigureBoot`.
8. **Deferred phase** (first boot, `specialize`, SYSTEM): vendor installers for the components
   that need a running OS.

## HP: source data

- **Model list**: `https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab`
  (`SystemId`, `SystemName`, `OSName`). `SystemId` can be a list (`8AC3,894F`) and one entry can
  cover several models (EliteBook 840 and 860 G11 share `8C26`), so matching works on any ID in
  the list.
- **Component list**: the HP Image Assistant reference file per platform and OS release,
  `https://hpia.hpcloud.hp.com/ref/<id>/<id>_64_11.0.<release>.cab` (for example
  `8c26_64_11.0.24h2.cab`). Each `ImagePal/Solutions/UpdateInfo` carries `Id`, `Name`, `Category`,
  `Version`, `SSMCompliant`, `DPBCompliant`, `SilentInstall`, `Url`, `SHA256`, `MD5`, `Size`,
  `DateReleased`, `SupportedOS`, optional `ContentTypes` (`Uwp`), and a `Supersedes` attribute.
  Driver pack entries also list their contents (`Content/DPItem`).
- **Device match key**: `Win32_BaseBoard.Product`, already collected by the Client.

## HP spike results (2026-10-21)

Five platforms on Windows 11 24H2: `8C26` (EliteBook 840/860 G11), `8B41` (EliteBook 840/860
G10), `8B7C` (ProBook 450 G10), `8AC3` (Elite Mini 800 G9), `8CD1` (ZBook Firefly 14 G11).
340 SoftPaq entries in total.

- **`DPBCompliant`** ("included in driver pack builds") is `true` for every `Driver - *` entry
  except two Chipset items, plus the dock drivers, one Software item (HP Desktop Support
  Utilities) and one Utility item (Intel Connectivity Performance Suite). It is `false` for all
  BIOS, Firmware, Diagnostic, Manageability and almost all Software.
- **HP Hotkey Support** is `Driver - Keyboard, Mouse and Input Devices`, `DPB = true`, **and**
  `ContentTypes = Uwp`. The INF part is in the driver pack, but the companion app is only installed
  by HP's installer. The same applies to Realtek audio, Intel and NVIDIA video, Intel ME,
  Thunderbolt and Intel ICPS. Offline INF injection alone does not reproduce HP's factory image
  for these.
- **Telemetry and services delivered as drivers**: "HP Services Scan for Commercial" and
  "Seamless Firmware Update Service" are `Driver - Enabling` with `DPB = true`. "HSA Telemetry"
  appears in the driver pack contents but is not published as its own SoftPaq on these platforms,
  so it ships inside another component. Category rules cannot catch these; name or ID rules can,
  and HP's monolithic driver pack cannot exclude them at all.
- **Software that is not in driver packs** (`DPB = false`), for example: HP App, HP Power
  Manager, HP Programmable Key, HP Privacy Settings, HP Easy Clean, HP Auto Lock and Awake, HP
  Notifications, HP Smart Health, HP Sure Run and Sure Recover, Poly Lens, System Default
  Settings. Many are `Uwp`. HP Support Assistant and Wolf Security are `SSM = false`.
- **`SilentInstall`**: 225 entries are plain `HPUP.exe` (HP's own installer), the rest are
  `setup.exe` variants, `install.cmd` and a few tool-specific commands (for example
  `"hptpsmarthealth.exe" -a waitforoobe`, so HP expects some installs to run before OOBE). The
  field also contains free text such as `None`, `NA`, `Do not support`. Three Firmware entries are
  `SSM = true` while their `SilentInstall` says silent install is not supported, so `SSMCompliant`
  alone is not trusted.
- **Architecture**: the PE header of 12 sampled SoftPaq executables (read with an HTTP range
  request), including two full driver packs, is x64 in every case. HP's own extractor can
  therefore run in 64-bit WinPE; behaviour in our WinPE is still to be confirmed.
- **Size**: all `DPB = true` SoftPaqs per platform add up to 2.0 to 3.2 GB, against 1.2 to 2.4 GB
  for HP's driver pack.

## HP: classification rules

"Installable" means `SSMCompliant = true` **and** `SilentInstall` is not `None`, `NA`, empty, or
text containing "not support". In the spike the flag and the text only disagreed on Firmware
entries, which are out of scope, but the text check stays as a cheap guard.

| Condition | Default action | Administrator may change to |
|---|---|---|
| `DPB = true`, no `Uwp` content | Offline | Skip |
| `DPB = true`, `ContentTypes = Uwp` | Offline + companion | Offline only, Skip |
| `Software - *`, `DPB = false`, installable | Skip | Deferred |
| `Driver - *`, `DPB = false`, installable | Deferred | Skip |
| Not installable | Skip (locked) | |
| `Diagnostic`, `Manageability*`, `OS`, `Utility` with `DPB = false` | Skip (locked) | |
| `BIOS*`, `Firmware`, dock firmware | Not shown: firmware is a separate feature | |

Actions:

- **Offline**: INF content injected into the applied volume in WinPE.
- **Offline + companion**: as Offline (so the hardware works even if the deferred run fails), plus
  the component's `SilentInstall` in the deferred phase to add the companion app.
- **Deferred**: only the `SilentInstall` in the deferred phase.
- **Skip**: not downloaded.

Portal names for these actions describe the phase where the work happens: **WinPE** (Offline),
**WinPE + Specialize** (Offline + companion), **Specialize** (Deferred), **Skip**.

Components come from the reference file, not from HP's driver pack SoftPaq. That gives
component-level skip rules, companion handling, and updates that do not wait for HP's monthly
pack. A driver-pack mode can be added later if a baseline is wanted.

## Onboarding and policy

- **Onboarded model**: vendor, match keys (HP `SystemId` list, Dell system SKU list), display
  name, OS releases.
- **Policy layers**: global defaults, vendor policy, model override. The most specific matching
  rule wins, and within a layer an ID rule beats a name rule beats a category rule.
- **Rule kinds**: category, name pattern, component ID. A skip list is just a set of Skip rules
  (for example HP Services Scan, Seamless Firmware Update Service, HP App). Suggested entries are
  offered, never applied silently.
- **New component review**: when a sync adds a component that does not continue a known one (HP
  `Supersedes` attribute, Dell hardware `componentID`), it is flagged as new on the model. A global
  setting ("New components: Wait for approval") holds new components out of the resolved set until
  an administrator approves them. Approving confirms whatever action the component currently has,
  Skip included, so there is no separate Skip button. A new version of a known component never
  waits ("Newer versions: Use automatically") and keeps its action.
- **Withdrawn components**: a component the vendor stops publishing stays in the set at its last
  verified version, marked Withdrawn, until an administrator removes it. The hardware is still in
  the field, so silently dropping its driver would be wrong.
- Roles: `CloudImaging.Administrator` onboards models and edits policy;
  `CloudImaging.PortalAccess` reads.

## Firmware is a separate feature

BIOS, system firmware, TPM, dock and SSD firmware have a different lifecycle from drivers: a
reboot to apply, BitLocker suspension, AC power checks, version comparison against the running
firmware, and often a separate flashing tool. They are excluded from driver policy and the driver
UI, and need their own todo if firmware management is wanted. Category alone is not enough to find
them: on `8C26` the Infineon and Nuvoton TPM firmware SoftPaqs are filed under
`Software - Security`, so the exclusion also matches "Firmware" in the component name.

## Payload format and size reduction

- **No pruning by file type.** An INF can reference any file in its package (executables, DLLs,
  co-installers, software component payloads), so removing setup programs or readmes can break
  installation. DISM already checks each INF's referenced files when staging it.
- **v1: original vendor files.** Each component is one signed vendor file in blob storage, so the
  vendor signature and SHA-256 stay verifiable on the device and the cloud needs no extraction.
- **Later: architecture reduction**, if measurement shows packages carry significant x86 or arm64
  content. The only safe rule is reference-based: keep each INF, its `.cat`, and the files its
  `SourceDisksFiles` sections reference for the target architecture. That is exactly what the
  driver store keeps after staging, and the catalog still validates because it may list more
  files than are present. The reduced set would be stored as one compressed WIM per component,
  which is where a WIM earns its place.
- Reduction needs extraction before download, so it moves HP's extractor into the cloud (Windows
  compute), and it applies only to Offline components. Offline + companion and Deferred components
  need the full original for HP's installer.

## Cloud job

- Scheduled catalog sync plus downloads for onboarded models. Downloads can use a server-side blob
  copy from the vendor URL, then a streamed SHA-256 check against the catalog value; a failed check
  is never published.
- **Admin-configured schedule**: every day, selected weekdays, or manually only, at a chosen time in
  a chosen IANA time zone (default: the browser's), stored in table storage and edited in the
  portal. A timer's CRON expression is fixed at deployment, so the timer ticks frequently (for
  example every 15 minutes) and starts a run only when the stored schedule says one is due and none
  has run for that slot. That keeps schedule edits live without an app restart. "Sync now" queues
  the same run.
- **Vendor enablement**: a stored on/off flag per supported vendor. A run (scheduled or Sync now)
  only reads catalogs for vendors that are on, and only for their onboarded models, so an unused
  vendor costs no requests or compute. Turning a vendor off pauses its updates only: its models
  keep their current resolved set and devices keep getting it. Onboarding a model of a vendor that
  is off turns it on.
- **Sync now is rate limited**: refused while a run is in progress and for 60 minutes after the
  last run started. The API enforces it (429 with the time it becomes available), not only the
  disabled button.
- **Run history**: each run records its start time, a result (`Completed`, `PartlyFailed`,
  `Failed`, `Running`) and one item per change (model, component, old and new version, outcome:
  updated, new and waiting for approval, failed with reason). A component waiting for approval does
  not affect the result. Catalogs are read per vendor, so one vendor's outage does not block the
  other's changes.
- Compute: a timer-triggered Function if the streamed hash of the largest files (around 800 MB)
  fits its limits, otherwise a Container Apps Job.
- Retention: current and previous resolved set per model and OS release. Blobs are shared by
  reference (one blob per vendor component version, used by every set that includes it) and
  deleted at the end of each sync run once no current set, roll-back set or running deployment
  refers to them. No manual clean-up action.
- **Offboarding**: removing a model (or one OS release of it) stops new deployments from getting
  its sets at once; running deployments keep their pinned set. Its sets and overrides are deleted,
  rules and sync history are kept, and its blobs go in the next clean-up unless another model
  uses them. Onboarding it again starts from the current catalog.

## Client: offline phase

- New `ImagingStepName` between `ApplyImage` and `ConfigureBoot`. Skipped with a warning when the
  device matches no onboarded model.
- **Hardware match (Dell)**: components with published PCI IDs are only downloaded when the device
  has matching hardware (`Win32_PnPEntity` in WinPE). Skips optional hardware such as mobile
  broadband (642 MB) and NVIDIA (970 MB) on devices without it. The portal therefore shows the
  maximum per-device download, with a vendor-neutral info tooltip saying devices can download
  less. HP's reference file has no hardware IDs, so HP downloads every included component.
- For each component: download (SAS), verify SHA-256, extract with HP's own switches
  (`spNNNNNN.exe /s /e /f W:\CloudImaging\Drivers\spNNNNNN`, legacy-switch retry, success judged
  by files produced), then `dism /Image:W:\ /Add-Driver /Driver:<folder> /Recurse` without
  `/ForceUnsigned`.
- Extract to the target volume, never to WinPE's RAM disk.
- Keep the folders of components with a deferred action; delete the rest.
- Later: inject the same INFs into WinRE in `ApplyRecoveryImage`.

## Client: deferred phase

- Hook: `specialize` `RunSynchronous` in `Panther\unattend.xml`, written in WinPE only when the
  resolved set has deferred work.
- Runner: per component in resolved order, re-verify the staged files against the manifest, run
  HP's `SilentInstall` from the extracted folder as SYSTEM with a timeout, log the exit code.
  3010 counts as success with a restart pending; no restart inside `specialize`.
- **Decision**: use HP's own installer (`SilentInstall`), not HP Image Assistant or our own logic.
- Results stay in a local log in v1; nothing reports back to the portal after the reboot.
- Delete the staged content when done.

## Dell and Lenovo (after HP)

- **Dell**: match on `Win32_ComputerSystem.SystemSKUNumber` (to be added to the Client hardware
  payload; remember `DeviceGatewayApiClient.SignPayload` rebuilds the payload). Spike on Latitude
  5450 (system SKU `0CB4`, 2026-10-22):
  - Model list from `https://downloads.dell.com/catalog/CatalogIndexPC.cab` (UTF-16 XML). Each
    entry points at a per-SKU Dell Command | Update catalog (for example
    `FOLDER15155719M/1/Latitude_0CB4.cab`) with its SHA-256. One model name can have several SKUs
    (Latitude 5450: `0CB2`, `0CB4`), each with its own catalog.
  - The per-SKU catalog holds the **full history**: 201 entries, 113 of them drivers covering 42
    names. Names change between releases (the Intel Wi-Fi driver has had four), but
    `SupportedDCHDevices/Device@componentID` stays the same, so it is the lineage key (fall back to
    the name when absent). The newest package per lineage gives 39 drivers and applications.
  - `ComponentType`: `DRVR` (driver), `APAC` (application), `FRMW`, `BIOS` (firmware feature, hidden).
    No equivalent of HP's `DPBCompliant`/`SSMCompliant`/`Uwp`. Apps mostly ship as separate `APAC`
    packages (for example Intel Graphics Software Application); a few drivers still bundle one
    ("Driver and Application" in the name, or an `infType="app"` device entry).
  - `packageType`: `LW64` is a 64-bit Dell Update Package, `LWXP` a 32-bit one. 4 of the 39 newest
    are 32-bit (for example the Realtek USB Audio dock driver); those cannot run in WinPE.
  - Every driver lists its hardware as PCI IDs (`PCIInfo`, in `SupportedDCHDevices` or
    `SupportedDevices`), so the Client can skip drivers for hardware the device does not have
    (mobile broadband 642 MB, NVIDIA 970 MB on this model).
  - Supported OS is just "Windows 11", no release. The Intel Thunderbolt driver lists Windows 10
    only and is dropped for Windows 11 models.
  - Also: `dellVersion` (A16), `vendorVersion`, `releaseDate`, `Criticality` (Recommended, Urgent),
    SHA-256, size, description, and a dell.com details URL.
  - Proposed defaults: 64-bit driver → WinPE (Dell installer `/s /drivers=<folder>` to extract,
    then DISM); driver bundling an app → WinPE + Specialize; 32-bit driver → Specialize only;
    application → Skip (can be changed to Specialize, installer `/s`). Still to validate: that
    64-bit DUP extraction works in WinPE, and dock drivers (only match when docked during imaging).
- **Lenovo (deferred to a later version)**: model list from
  `https://download.lenovo.com/cdrt/td/catalogv2.xml` (match on the first four characters of
  `Win32_ComputerSystem.Model`, which the Client already collects). Spike on ThinkPad T14 Gen 5
  (`21ML`, 2026-10-22):
  - Driver packs and every sampled component installer are **32-bit** Inno Setup executables.
    64-bit WinPE has no 32-bit subsystem, so the vendor's own extractor cannot run there. The WinPE
    phase would need cloud-side extraction (`innoextract` or Windows compute), which v1 avoids.
  - The per-model catalog (`/catalog/21ML_Win11.xml`, 47 packages) only points at one descriptor
    XML per package, so a sync costs one request per component instead of HP's single file.
  - Descriptors carry SHA-256, but no equivalent of HP's `DPBCompliant`/`SSMCompliant`: every
    sampled package is `PackageType = 2` (driver) with a command-line install, including items
    in "Software and Utilities". Telling an INF-only driver from one that needs its installer means
    unpacking it first.
  - Package IDs change with every release (for example `n45a127w`) and there is no `Supersedes`, so
    "newer version" against "new component" needs matching by title, which the approval model
    depends on.
  - Driver packs are dated per release and one pack can cover several machine types.
- Same policy model and the same two phases; only the classification rules differ per vendor. The
  vendor-specific parts sit behind one adapter per vendor: catalog reader, match key,
  classification, version lineage, and where unpacking happens.

## Portal UI (mockup decisions, 2026-10-22)

Mockup: `docs/design/os-drivers.html` (gitignored). New **Drivers** page under Imaging, tabs
**Models / Rules / Sync / Storage**. Administrators edit; PortalAccess reads.

- **Action names** (UI, everywhere): **WinPE**, **WinPE + Specialize**, **Specialize**, **Skip**.
  They name the phase where the work happens. An info icon on the Action column header explains
  them; "Specialize" is Windows setup terminology, which suits the audience.
- **Models tab**: one row per model (HP: per `SystemId` set; Dell: per model name with all its
  SKUs). Columns: Model (match keys underneath), Vendor, Windows releases, Included, Skipped,
  Max download (info icon: the most one device downloads; devices without some hardware can download less; never a
  model-specific string), Status (one badge, only what needs attention), open
  chevron. Vendor filter, search, **Onboard model** button.
- **Onboarding dialog** (768px, two steps): step 1 multi-select from the vendor's catalog with a
  vendor dropdown and search, already onboarded models shown disabled; step 2 picks Windows
  releases and shows a per-model table (WinPE, WinPE + Specialize, Specialize, Skip, Size) before
  anything downloads.
- **Model page**: header (vendor badge, match keys, driver set version and build time, release
  dropdown, Roll back, Remove model), approval banner when something waits, tiles per action plus
  Download per device, toolbar (search, action and category filters, "N updated by today's sync").
- **Component table**: chevron, Component (name, ID and category below; amber **New** badge next to
  the name while waiting for approval), Version, Last updated (date only), Action (fixed 176px
  dropdown), Source. Fixed pixel widths for every column except Component, so names wrap only on
  narrow screens. Size moved into the details. Every component is listed (no paging or "show
  more"; 30 to 60 rows per model); search and filters narrow it, the footer shows the count.
- **Source column** (values in precedence order): **Locked** (vendor data allows only Skip, lock
  icon and tooltip on the dropdown), **Override** (this model only, with an undo button), rule name
  over **Rule**, **Default** (vendor classification). The vendor is in the page header, so values
  never repeat it.
- **Expandable details** (same pattern as the Devices table): a three-column grid of labelled
  fields, then a bordered Description section with a link to the vendor's notes. No free prose.
  HP: Offline injection, Contains an app, Silent install / Default action, Released by HP, Release
  type / Added to this model, Previous version, Download size. Dell: Installer (64 or 32-bit),
  Hardware match, Package type / Default action, Released by Dell, Criticality / Added to this
  model, Previous version, Download size. Deliberately left out: install command, SHA-256,
  supported Windows releases (page is already per release).
- **Rules tab**: only the rules table (Name, Applies to, Matches, Action, Hits) and Add rule. The
  info icon explains precedence and withdrawn components.
- **Sync tab**: Sync card first (description; schedule controls Every day / Selected days with
  day toggles / Manually only, time, IANA time zone; **Sync now**; then "Newer versions: Use
  automatically" and "New components: Wait for approval"), no Save button (settings apply on
  change), no next-sync line. **Vendors** card next, same setting-row layout as the Sync card:
  one row per vendor with a short line (onboarded models, match key) and a Sync
  switch; unsupported vendors such as Lenovo are
  listed with a disabled switch; models of a vendor that is off show a muted "Sync off" badge on
  the Models tab. **History** card below: one-line rows with Started as
  `2026-10-22 02:00`, Result badge, and a generic Details summary ("4 updated, 1 failed", "1 new",
  "No changes"). The chevron opens one line per item: Model, Component (links to its row on the
  model page), Change, Outcome. Runs without changes have no chevron.
- **Storage tab**: tiles for total used (info icon: shared components are stored once, so it is
  less than the sum of the models) and per vendor; one-line table per model with Vendor, Windows
  releases, Driver sets (current and roll-back), Stored, and Freed if removed (only what no other
  model uses). The Models tab no longer shows storage.
- **Remove model**: the portal's existing impact overlay (`ConfirmImpactDialog`): what devices will
  experience, what is deleted, the space freed, and what is kept.
- **Deployment timeline**: an "Install drivers" step with a neutral card listing components; a
  partial failure uses an amber warning knob and "Completed with warnings", the deployment itself
  still succeeds (failure semantics still open, see below).

## Rejected alternatives

- **Baking drivers into `install.wim`**: one image per model and OS release, rebuilt on every
  driver or monthly update, and the Linux Imaging Core API cannot run DISM.
- **Unattend `DriverPaths`**: only valid in `offlineServicing` and `auditSystem`.
- **Media Builder as the driver preparation tool**: its name and purpose are boot media.
- **Pruning files by type**: see "Payload format and size reduction".
- **7-Zip extraction on Linux in the cloud**: replaced by HP's own extractor on the device.
- **Surface**: no catalog, scraping rejected.

## Still to validate

1. HP's extractor (`/s /e /f`) running in our WinPE: x64 is confirmed, behaviour is not.
2. HP's `SilentInstall` during `specialize` as SYSTEM, with no user signed in and no Store, on a
   real device: Hotkey Support, Realtek audio, Intel video, HP Power Manager. Confirm the
   companion app is provisioned for all users, not only for SYSTEM.
3. Offline INF followed by the same component's installer (Offline + companion) does not cause a
   second driver install or a reboot request.
4. Which INFs install companion software by themselves at first boot (INF software components),
   to avoid a double install.
5. Dell: 64-bit Dell Update Package extraction (`/s /drivers=<folder>`) in our WinPE, and Dell's
   installer (`/s`) during `specialize` for drivers that bundle an app.
6. Dock drivers with hardware match: a dock only matches when it is connected during imaging.

## Open decisions

Resolved 2026-10-22 in `specs/005-os-driver-management/spec.md` (Clarifications): critical driver
categories chosen centrally per vendor fail the deployment, others end as Completed with warnings;
the Windows release is entered by the administrator per OS image, no fallback; one link per
component; the 15-minute budget risk is accepted. Still open: the roll back model (per component
with automatic move forward, proposed).

- Failure semantics: a component that fails offline injection is a warning, but should a failed
  storage or network driver fail the session? Needs `ImagingStepStatus` modelling either way.
- OS release of an OS image: the reference file is per release, so the `OsImage` entity needs its
  build (read from the WIM metadata at publish), with a rule for falling back to the nearest older
  release.
- SAS for many blobs per session (one per component, or a session-scoped read SAS), and the
  progress-report budget (10 calls per 30 s per session) for a step with around 30 components.
- Storage size per model and release, and whether retention keeps more than one previous set.
- Architecture reduction: measure the x86 and arm64 share in real HP, Dell and Lenovo packages
  before deciding whether the cloud-side extraction it needs is worth it.

# TODO: OS Image driver injection & OEM (non-baked) driver support

Status: **not started — planning only**. This document captures the design/plan for a later
version. It is deliberately separate from the [OS Image ISO extraction](../src/CloudImaging.ImagingCoreApi/Services/IsoExtractionService.cs)
work (which only ever extracts `sources\install.wim`/`install.esd` as-is and never modifies it).

## Context / why this doesn't already exist

Today the OS image pipeline is intentionally minimal end-to-end:

- **Publish** (`OsImageUploadFunctions.PublishUpload`, Imaging Core API): accepts `.wim` as-is, or
  extracts `sources\install.wim`/`install.esd` from an uploaded `.iso` — nothing else in the ISO
  is inspected or retained (no `$OEM$`, no `Autounattend.xml`, no `sources\$OEM$\$1\Drivers`).
- **Apply** (`ImageApplyService.ApplyAsync`, Client, running in WinPE): downloads the WIM, verifies
  its SHA-256, and runs `dism.exe /Apply-Image /ImageFile:{wim} /Index:1 /ApplyDir:{volume}\`.
  Nothing else happens to the applied volume before the device reboots into the full OS.

The **only** existing driver injection anywhere in this product targets the *boot image* (WinPE),
not the OS image — see `BootImageGenerationService.InjectDriversAsync` (Media Builder, T064/FR-051c):
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

## Two distinct features (do not conflate)

### Feature A — OEM driver packages applied post-apply, on-device (RECOMMENDED FIRST)

Drivers are **not** baked into `install.wim` at all. Instead, a new "Driver Package" catalog
(uploaded via the Portal, alongside OS/Boot/Recovery Images) is downloaded by the Client and
serviced directly into the **already-applied, offline OS volume** — before first boot — using:

```
dism.exe /Image:{WindowsVolume}\ /Add-Driver /Driver:{extractedDriverFolder} /Recurse /ForceUnsigned
```

This is the same pattern ConfigMgr/MDT call "Apply Driver Package" and is materially simpler than
re-baking the WIM: no mount/unmount/commit cycle, no server-side DISM requirement, and it reuses
`dism.exe`, which is already present and already invoked by the Client in WinPE (see
`ImageApplyService.ApplyAsync`). This should be the first feature built.

**Design sketch:**

- **Storage/catalog**: new `DriverPackage` entity (Table Storage, mirrors `OsImage`/`BootImage`
  shape): `DriverPackageId`, `Name`, `Version`, `SizeBytes`, `Sha256Hash`, `StoragePath`,
  `UploadedAt`, plus a **targeting** field — most likely `TargetHardwareIds: string[]` (a list of
  values matched against `Win32_ComputerSystem.Model`/`Win32_BaseBoard.Product` or SMBIOS
  model/manufacturer strings the Client already collects for hardware identity — see the
  `UNKNOWN` hardware-identity comment in `BootImageGenerationService` for where that data
  currently gets read). Unlike Boot/Recovery images there is no "5 active entries" cap — an
  operator may need many driver packages, one (or more) per hardware model.
- **Upload format**: a `.zip` (or folder tree staged the same chunked-upload way as WIM uploads)
  containing one or more driver packages, each with a top-level `.inf`. Validate similarly to
  `BootImageValidationService` (magic-byte sniff for zip, SHA-256), but the "allowed extension"
  concept doesn't map cleanly — needs its own validation service rather than reusing
  `BootImageValidationService.WimOnlyExtensions`/`OsImageExtensions`.
- **Publish-time processing**: unlike ISO extraction, no server-side extraction is required —
  the zip is stored as-is; extraction to a local temp folder happens on-device at apply time
  (the Client already downloads to local disk before invoking DISM, so unzip-then-`/Add-Driver`
  fits the existing pattern in `ImageApplyService`).
- **Assignment/selection**: the Device Gateway API / session assignment flow needs to resolve
  "which driver package(s) apply to this device's hardware model" the same way it currently
  resolves which OS/Boot/Recovery image applies to a session — likely a new lookup keyed by the
  hardware identity the Client already reports at session creation (see `CreateSession` request
  in Device Gateway API). Multiple matching packages could be legal (e.g. chipset + NIC as
  separate packages) — needs a decision: single "best match" vs. apply-all-that-match.
- **Client pipeline change**: new `ImagingStepName.ApplyDrivers` (or similar) enum value in
  `CloudImaging.Contracts.Enums.ImagingEnums.ImagingStepName`, inserted between `ApplyImage` and
  `ConfigureBoot` in `ImagingWorkflowViewModel.RunAsync`. Must be **optional/skippable** (like
  boot image driver injection) — a session with no matching driver package for that hardware
  model simply skips the step, exactly like `InjectDriversAsync` returns `0` and skips when no
  `.inf` is found.
- **Portal UI**: new `DriverPackagesPage.tsx` mirroring `BootImagesPage.tsx`/`RecoveryImagesPage.tsx`
  (list, upload dialog with per-package hardware-model tag input, delete). No "latest published"
  single-entry concept needed — multiple active entries are the normal state.
- **Failure handling**: a driver injection failure should very likely be a **warning, not a fatal
  imaging failure** (unlike `ApplyImage`/`ApplyRecoveryImage`, which do fail the whole session) —
  a device that boots without vendor drivers is still usable/recoverable via Windows Update,
  whereas a failed OS apply is not. Needs an explicit product decision either way, and the
  `ImagingStepStatus` semantics (`Failed` vs. some new "skipped/degraded" status) may need
  extending to represent "ran, but non-fatally failed" distinctly from "blocked the whole session".

### Feature B — Baking drivers directly into `install.wim` (a "golden image")

This means literally mounting `install.wim`, servicing it with `/Add-Driver`, and committing —
producing a modified WIM that gets published/applied instead of (or as a variant of) the
original. This is heavier and has a **major infrastructure constraint that does not exist for
Feature A**:

> **Constraint**: `CloudImaging.ImagingCoreApi` runs as a **Linux** Azure Functions app
> (`kind: functionapp,linux`, `linuxFxVersion: DOTNET-ISOLATED|10` — see
> [imaging-core-api.bicep](../src/deploy/bicep/modules/imaging-core-api.bicep)). `dism.exe` is
> Windows-only. There is no pure-managed .NET library equivalent to DISM's offline driver
> servicing (DiscUtils, used for ISO extraction, can *read* WIM/NTFS structures but does not
> implement driver-store/PnP servicing semantics) — so **this cannot run in the existing
> Imaging Core API compute as-is**.

Options to unblock, roughly in order of preference:

1. **Do it on-device instead, at apply time** (before `/Apply-Image`, targeting the *downloaded
   WIM* rather than the applied volume): `dism.exe /Mount-Image /ImageFile:{wim} /Index:1
   /MountDir:{tmp}` → `/Add-Driver` → `/Unmount-Image /Commit` → then `/Apply-Image` as normal.
   WinPE already has `dism.exe` (Feature A relies on this too). Downside: substantially slower
   per-device (mount+commit round-trip on top of apply) and re-does the same servicing work on
   every device instead of once — Feature A (post-apply, direct-to-volume) achieves the same
   end result without this cost and should be preferred unless there's a concrete reason the
   drivers must be present *inside the WIM itself* rather than serviced onto the volume
   afterward (the practical difference is minimal for storage/NIC/chipset drivers — both are
   "offline, pre-first-boot" from Windows' perspective).
2. **Add Windows-hosted compute** for a "bake" operation done once per OS-image+driver-package
   combination, decoupled from the Linux-hosted publish/serve path — e.g. an Azure Container
   Instance running a Windows Server Core image with the ADK/DISM installed, invoked on-demand
   (similar shape to how `BootImageGenerationService` already does WIM mount/service/commit, just
   server-triggered instead of technician-triggered from Media Builder). Adds real infrastructure
   or a manual step and cost; only justified if there's a strong requirement to ship a single
   "golden WIM" per hardware model (e.g., to keep per-device apply time minimal by moving cost to
   publish time instead of every device's apply time).
3. **Extend Media Builder** (already a Windows app with ADK/DISM access) with a "Bake OS Image"
   function analogous to "Generate Boot Image" — a technician selects an OS image + a driver
   root folder, Media Builder mounts/services/commits locally, and the result is re-uploaded to
   the Imaging Core API as a new `OsImage` catalog entry (published normally afterward). This
   reuses 100% of the existing mount/service/commit code already proven in
   `BootImageGenerationService`, at the cost of making it a manual, technician-driven step rather
   than an automatic one triggered by publish.

**Recommendation**: build Feature A first (it fully covers the "drivers not baked into
install.wim" half of this TODO and needs no new infrastructure). Only pursue Feature B if a real
requirement surfaces for a single golden WIM per model — and if so, prefer option 1 (on-device,
before apply) or option 3 (Media Builder-driven bake step) over standing up new Windows-hosted
server compute, unless per-device apply time becomes a measured problem.

## Open questions to resolve before implementation

- Hardware targeting model: exact match on model string, wildcard/prefix match, or an explicit
  admin-curated mapping table (device model → driver package IDs)? Autopilot/ConfigMgr-style
  "hardware ID" matching (PCI\VEN_xxxx&DEV_xxxx) would be more precise than model-string matching
  but requires the Client to enumerate PnP hardware IDs of *unrecognized* devices during imaging
  (WinPE PnP enumeration, not currently done anywhere in this codebase) rather than just a static
  system model string.
- Can multiple driver packages apply to one session, and if so, are they injected in a defined
  order (e.g. chipset before NIC)?
- Should a driver-injection failure fail the whole imaging session, or only warn (see Feature A
  above) — needs a product decision, plus corresponding `ImagingStepStatus` modeling.
- Storage quota/lifecycle for driver packages — unlike Boot/Recovery Images there's no proposed
  cap; needs a retention/cleanup story as the catalog grows across many hardware models.
- Portal RBAC/roles: presumably `CloudImaging.Administrator` for upload/delete (matching
  Boot/Recovery Images), `CloudImaging.PortalAccess` for read — consistent with existing routes.

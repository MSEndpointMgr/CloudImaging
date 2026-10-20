# TODO: Post-apply OS customisation stage

Status: **not started**. Captures the design for a sixth pipeline stage and the bootstrap
mechanism that several other features depend on.

Priority: **highest of the open gaps**, because it is the prerequisite for machine naming,
post-install actions and app/feature removal. Those three are separate features only in the
product sense; in the code they are three consumers of one missing stage.

## Context / why this doesn't already exist

The imaging pipeline is fixed at five stages, declared in
[`OverallProgressCalculator.Steps`](../src/CloudImaging.ImagingCoreApi/Services/OverallProgressCalculator.cs)
and mirrored in [`PIPELINE_STEPS`](../src/cloud-imaging-portal/client/src/lib/imagingProgress.ts):

```
FormatDisk → DownloadImage → ApplyImage → ConfigureBoot → ApplyRecoveryImage
```

Nothing in that sequence touches the applied OS. `ImageApplyService.ApplyAsync` runs
`dism /Apply-Image` and returns; `BootConfigurationService` writes boot files; `RecoveryImageService`
registers WinRE. The device then reboots into whatever the WIM contained.

So every "make the deployed Windows different" request currently has the same answer: bake it into
the WIM before uploading. That is the thing a modern deployment tool exists to avoid, and it is
where this product is furthest behind the standalone alternatives.

## Competitive note

Foundry OSD ships OS selection, unattend, machine naming, OOBE shaping, post-installation actions,
optional features, AppX removals and AI component control. Every one of those is a post-apply
concern. We have none of them, and we cannot have any of them until this stage exists.

## The two mechanisms

Both are needed. They solve different halves of the problem.

### A. Offline servicing, inside WinPE, before reboot

Runs against the applied volume while still in WinPE, where the OS is a mounted file tree and
nothing is running. Suits anything that is a file or registry edit:

- Staging `unattend.xml` into `%WINDIR%\Panther\`, which is how machine naming and OOBE shaping
  are expressed.
- Driver injection (see [os-image-driver-injection.md](os-image-driver-injection.md)).
- `dism /Image:<volume> /Remove-ProvisionedAppxPackage` and `/Disable-Feature`.
- Offline registry edits by loading the hives from the applied volume.

### B. A bootstrap action set, first boot, inside Windows

Runs after the restart, where a real OS and network exist. Suits anything that cannot work offline:

- Installing applications.
- Anything needing the device's identity or network.
- Operator-supplied scripts.

The usual carrier is `SetupComplete.cmd` or a RunOnce entry written during A. It runs before first
sign-in, which is the property that makes it useful.

## What has to be decided

**Where do actions come from?** Three options, in increasing order of effort and power:

1. **Portal-defined only.** Administrators author a named action set in the portal, the same way
   partitioning schemes and Autopilot group tags already work. It is snapshotted onto the session
   at assignment, so an in-flight deployment is unaffected by a later edit. Fits every existing
   pattern in the product.
2. **Scripts uploaded to a catalog**, with content hashing and the same publish/verify lifecycle as
   OS and boot images. More capable, significantly more surface: storage, retention, integrity.
3. **Scripts on the USB media.** Cheapest, and wrong for this product. It takes customisation back
   out of the portal, breaks the audit trail, and means media must be rebuilt to change behaviour.

Option 1 is the right first move, with 2 as the natural extension once the stage exists.

**How is it reported?** A new stage changes `PipelineStepCount`, which the completion check in
`ReportProgressFunction` compares against, and the portal timeline renders a fixed five-node
stepper. Both need to handle a sixth step. Existing session history rows will have five, so the
portal must not assume the count.

**What does failure mean?** A failed customisation action is not the same as a failed disk format.
Windows is installed and bootable. Options: fail the session, or complete it with a warning. Likely
needs a per-action "continue on error" flag, which argues for option 1 above, where that flag is
part of the definition.

**Security.** This executes operator-authored code with SYSTEM rights on every imaged device. It
needs the same seriousness as the boot media certificate: who may author, who may approve, and
whether an action set is signed. Worth deciding before the first line of code, not after.

## Scope boundary

Explicitly **not** in this work:

- **Domain Join.** Ruled out as a product direction. Cloud Imaging targets Entra-joined and
  Autopilot-registered devices; adding AD join would pull in credential storage, OU selection and
  an offline-join blob for a shrinking market.
- Downloading Windows from a Microsoft catalog. Images stay administrator-supplied and governed.

## Features this unblocks

| Feature | Mechanism | Notes |
|---|---|---|
| Machine naming templates | A, via `unattend.xml` | Location and serial already reach the session. The group-tag template engine is a working precedent for the token syntax |
| OOBE shaping | A, via `unattend.xml` | Same file, same stage |
| Post-install actions | B | The general escape hatch |
| AppX removal | A | `/Remove-ProvisionedAppxPackage` |
| Optional features | A | `/Enable-Feature`, `/Disable-Feature` |
| AI component control | A | Same mechanism as AppX and features |

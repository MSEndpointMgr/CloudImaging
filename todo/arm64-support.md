# TODO: ARM64 boot media and imaging support

Status: **Milestone 1, sections 1-8 implemented except the items listed below**
(contracts/manifest, Media Builder generation UI, architecture-aware WinPE staging, the Client
release matrix, architecture-aware download with PE validation, the boot-image catalog/Portal
round trip with per-architecture latest and capacity, architecture-aware USB/ISO preparation, and
per-architecture self-update, and the pre-production/promote workflow). Still outstanding in
Milestone 1: driver-package architecture validation (§3). Milestone 0 (physical/VM ARM64 runtime
validation) has **not** been run, so the ARM64 `bcdboot /f UEFI` path is unverified on hardware.
Do not advertise ARM64 as supported.

### What's implemented so far

- `MachineArchitecture` enum + `MachineArchitecturePlatform` mapping helper added to
  `CloudImaging.Contracts` (§1).
- `Architecture` field added to `BootImageManifest` (schema bumped to 1.1), `BootImage`,
  `LatestBootImageInfo`, `UsbPreparationManifest`, and Media Builder's `BootImageDto` (§1).
- `GenerateBootImageView`/`GenerateBootImageViewModel` has an x64/ARM64 selector, defaulting to
  x64, threaded through `BootImageGenerationService.GenerateElevatedAsync` (§2).
- `BootImageGenerationService` and `IsoGenerationService` no longer hardcode `amd64`; the ADK
  WinPE/Oscdimg/DISM/optional-component paths and the `copype.cmd` argument all use
  `MachineArchitecturePlatform.AdkArchitectureName`, and generated WIM filenames include the
  architecture slug (§3, partially: driver-package architecture validation is still outstanding).
  Local-Client PE architecture validation is done: `PeArchitectureInspector` reads the exe's COFF
  machine type and `EnsureClientBinariesAreSelfContained` rejects a mismatch before mounting.
  `Dism.exe`/`oscdimg.exe` (executables invoked by `copype.cmd`/`MakeWinPEMedia.cmd`) are resolved
  from `MachineArchitecturePlatform.HostArchitecture()`, not the selected target: those binaries
  are native code that can only run on a matching host, unlike the target-arch boot WIM/boot-sector
  data files they stage. Getting this wrong is what produced the "Machine Type Mismatch" dialog
  (host x64, target ARM64: `Deployment Tools\arm64\DISM\dism.exe` cannot execute on an x64 host).
- `release-client.yml` builds and publishes both `win-x64` and `win-arm64` Client artifacts on the
  version release (`cloud-imaging-client-v<version>-win-x64.zip` /
  `-win-arm64.zip`) and on the `mse-ci-client-latest` alias release
  (`cloud-imaging-client-x64.zip` / `-arm64.zip`), while keeping the legacy unversioned
  `cloud-imaging-client.zip` (x64) on the alias release indefinitely for Media Builder versions
  released before this change (§4).
- `GitHubReleasesClient.DownloadLatestClientAsync` now requires a `MachineArchitecture` and
  resolves the matching alias asset by name, falling back to the legacy `cloud-imaging-client.zip`
  only when x64 is requested and no `cloud-imaging-client-x64.zip` asset exists yet (a release cut
  before this change). It also validates the extracted exe's actual PE machine type against the
  requested architecture after the checksum check (§5).
- `UsbPreparationService`/`PrepareStorageDeviceViewModel` thread the selected boot image's
  architecture into the USB preparation manifest and the ISO generation call (§7, partially — the
  ARM64-only UEFI/no-BIOS-files enforcement in §7 is still outstanding).
- `BootImageSelfUpdateService` compares `MachineArchitecture` enums (via the manifest field) and
  aborts on mismatch (§8, partially — the Device Gateway API now reports the actual persisted
  architecture of the global-latest catalog entry instead of hardcoding x64, but it is still the
  single global latest across both architectures; true per-architecture "latest" resolution still
  needs Milestone 2's catalog work, §6).
- Boot-image catalog persistence and the Portal round trip (§6, partially):
  `BootImageRepository`/`UploadJobRepository` now persist and read back `Architecture` instead of
  silently dropping it, `BootImageFunctions.MapToDto` includes it in the JSON contract Operator
  API and Media Builder consume, the upload publish endpoint accepts an optional `architecture`
  field, and the Portal's Boot Images page has an architecture selector (defaulting to x64, not a
  forced choice) on upload plus an architecture badge in the table. Still outstanding: one latest
  image per architecture, the decoupled publish/promote-to-latest workflow, per-architecture
  catalog capacity, and an architecture column on the image-inventory report.

Everything above defaults to `MachineArchitecture.X64` wherever a value is absent, so existing x64
behavior, on-disk manifests, and callers are unaffected.

### Added in the end-to-end verification pass (2026-09-24)

- `WimMetadataReader` (Contracts) and its TypeScript twin `lib/wimMetadata.ts` read a WIM's real
  architecture from its XML metadata (`IMAGE/WINDOWS/ARCH`: 9 = x64, 12 = arm64) without DISM.
- Portal upload dialog: architecture has **no default**. It is read from the selected WIM and
  locked; when the image records none, the operator must choose. x86 images are refused.
- Imaging Core publish: an invalid `architecture` value is a 400 (was silently x64). The WIM's own
  metadata is authoritative: a contradicting selection or an x86 image is a 422 and the staged blob
  is deleted; a missing selection takes the detected value.
- Per-architecture catalog: `BootImageRepository.PlanPublish` scopes both the 5-entry capacity
  and the latest flag to the published image's architecture. Portal shows capacity per
  architecture.
- Device Gateway `GET /api/v1/boot-image/latest?architecture=x64|arm64` resolves latest for the
  requested architecture (x64 when omitted, for older Clients). The Client sends its USB
  manifest's architecture.
- Media Builder Prepare Storage Device: architecture shown in the boot image list, x64 latest
  preselected, architecture in the default ISO name, and the downloaded WIM's real architecture is
  checked against the catalog label before partitioning or ISO packaging.
- `BootImageDeploymentService`: ARM64 uses `bcdboot /f UEFI` (no BIOS files) and verifies
  `\efi\boot\bootaa64.efi` (copied from the image's `bootmgfw.efi` if bcdboot named it after the
  host) and `\efi\microsoft\boot\BCD` before reporting success.

## Goal

Allow an administrator to generate, publish, prepare, and use ARM64 Cloud Imaging boot media while
preserving the current x64 workflow as the default. Complete support means more than producing an
ARM64 WinPE WIM: the Client, boot files, boot-image catalog, OS image, recovery image, session, and
assignment must all agree on architecture before a target disk is modified.

Supported choices for the first release:

- `x64` (existing behavior and default)
- `arm64`

`x86` is deliberately out of scope. The current Windows 11 ADK installation includes `amd64` and
`arm64` WinPE payloads but no x86 WinPE payload. Supporting x86 would also create a third release
artifact and compatibility path for a declining platform.

## Feasibility evidence

The high-risk prerequisites were checked on 2026-09-12:

- `CloudImaging.Client` restores and publishes successfully as a self-contained `win-arm64` WPF
  application on .NET 10.
- The installed Windows ADK contains both `Windows Preinstallation Environment\amd64` and
  `Windows Preinstallation Environment\arm64`.
- Both WinPE architectures contain the required `WinPE-WMI.cab` and `WinPE-NetFx.cab` optional
  components.
- The ARM64 ADK deployment-tool directory is present.

These checks prove build-time feasibility. They do not prove that the published WPF Client starts,
authenticates, enumerates hardware, downloads an image, or completes imaging in ARM64 WinPE. That
runtime proof is the first implementation gate.

## Current x64 assumptions

Architecture is currently implicit throughout the product:

- Boot-image and recovery-image catalog records have no architecture field.
- OS-image catalog records have no architecture field.
- Device registration and session records have no processor-architecture field.
- The boot-image manifest does not record architecture.
- USB deployment configures BIOS and UEFI together and documents the x64 fallback loader
  `bootx64.efi`.
- Exactly one boot image is marked latest across the entire catalog. ARM64 requires one latest
  image per architecture.
- Publishing a boot image or recovery image immediately marks it as the latest published image.
  There is no review step and no separate action to promote a specific upload to latest.
- Assignment does not prevent an ARM64 device from receiving an x64 Windows or recovery image.

Adding only a dropdown would therefore create media that can be mislabeled, receive incompatible
artifacts, or fail after the target disk has already been erased.

## Architecture model

Add a shared enum in `CloudImaging.Contracts`, serialized as a string:

```csharp
public enum MachineArchitecture
{
    X64,
    Arm64,
}
```

Use one model across boot images, OS images, recovery images, sessions, manifests, and API
contracts. Do not use free-form architecture strings at individual boundaries.

Centralize platform mappings in Media Builder and Client code:

| Product value | ADK architecture | .NET RID | UEFI fallback loader |
|---|---|---|---|
| `X64` | `amd64` | `win-x64` | `bootx64.efi` |
| `Arm64` | `arm64` | `win-arm64` | `bootaa64.efi` |

Compatibility rule: architecture values must match exactly. There is no x64-on-ARM64 fallback in
WinPE imaging workflows.

Upload rule: every image type an operator can upload — boot image, recovery image, and OS image —
requires an explicit architecture selection with no default. Defaulting silently to x64 would hide
operator mistakes across all three, not just one of them.

## Delivery strategy

Deliver in two milestones. Milestone 1 proves and publishes architecture-correct boot media.
Milestone 2 prevents incompatible operating-system deployment and completes product support.

Do not advertise ARM64 as supported until both milestones and the physical validation matrix pass.

## Milestone 0: runtime proof spike

This gate should happen before broad contract and storage changes.

1. Publish the current Client for `win-arm64` using the existing self-contained settings.
2. Run `copype.cmd arm64` with the installed ADK.
3. Add ARM64 WMI and NetFx optional components.
4. Embed the ARM64 Client and current configuration into the WIM.
5. Produce an ISO with ARM64 ADK tooling.
6. Boot it on an ARM64 VM or physical Windows-on-ARM device.
7. Verify:
   - WPF renders correctly.
   - `CloudImaging.Client.exe` starts without missing native dependencies.
   - WMI hardware inventory succeeds.
   - network adapters initialize.
   - mTLS registration reaches Device Gateway.
   - local logs can be retrieved after failure.
8. Build ARM64 USB media and verify UEFI discovers `\efi\boot\bootaa64.efi`.
9. Prove the chosen `bcdboot` invocation creates an ARM64 UEFI BCD store from the mounted ARM64
   WinPE source. ARM64 must use `/f UEFI`; do not request legacy BIOS boot files.

Exit criteria: Client reaches the normal waiting-for-coupling screen on ARM64 hardware from both
ISO and USB media. If this fails, stop and resolve the runtime or boot-chain blocker before adding
catalog fields.

Estimated effort: **1 to 2 engineering days**, assuming ARM64 test hardware is available.

## Milestone 1: architecture-correct boot media

### 1. Shared contracts and manifests

- Add `MachineArchitecture` to shared contracts.
- Add required `Architecture` fields to:
  - `BootImageManifest`
  - `BootImage`
  - boot-image upload and publish payloads
  - Media Builder `BootImageDto`
  - `UsbPreparationManifest`
- Increment the embedded boot-image manifest schema version.
- Treat legacy boot-image rows and manifest version 1.0 as `X64` during migration.
- Include architecture in generated output names, for example
  `cloud-imaging-boot-arm64-v2026.09.12.wim`, to reduce operator mistakes outside the product.

### 2. Media Builder generation interface

- Add an architecture selector to `GenerateBootImageView`, with x64 selected by default.
- Add a typed selected-architecture property to `GenerateBootImageViewModel`.
- Carry architecture through `GenerateElevatedAsync`, elevated-worker JSON parameters, and
  `GenerateAsync`.
- Keep the Media Builder application itself x64. It only generates target-architecture artifacts;
  there is no requirement to ship an ARM64 Media Builder installer.
- Disable generation before elevation when the selected ADK WinPE directory or required optional
  components are missing.
- Include selected architecture in progress text, completion text, and failure diagnostics.

### 3. Architecture-aware WinPE generation

- Replace the hardcoded `WinPeArch` constant in `BootImageGenerationService` with the centralized
  architecture mapping.
- Select matching paths for `copype.cmd`, Oscdimg, DISM, WinPE optional components, and language
  packs.
- Record architecture in the embedded manifest and deployment metadata.
- [x] Validate local Client source architecture before mounting the WIM.
  `EnsureClientBinariesAreSelfContained` reads the exe's PE machine type via
  `PeArchitectureInspector` and throws a clear mismatch error before any DISM work starts.
- Validate injected driver packages against selected architecture where DISM exposes that
  metadata. Surface skipped or rejected INF paths clearly.
- Never silently inject x64 drivers into ARM64 WinPE.

### 4. Client release artifacts

- [x] Change `release-client.yml` to restore and publish a matrix for `win-x64` and `win-arm64`.
- [x] Publish these stable assets:
  - `cloud-imaging-client-v<version>-win-x64.zip`
  - `cloud-imaging-client-v<version>-win-arm64.zip`
  - `SHA256SUMS`
- [x] Keep `cloud-imaging-client.zip` on the alias release as an x64 compatibility asset
  indefinitely so already-released Media Builder versions continue to work. That name is
  hardcoded in every Media Builder build released before this change, so it can never be retired.
- [x] Put both architecture assets on the immutable version release and the
  `mse-ci-client-latest` alias release (as `cloud-imaging-client-x64.zip` /
  `cloud-imaging-client-arm64.zip` on the alias release).
- [x] Extend CI to restore, publish, and smoke-check both RIDs on every Client change. `ci.yml`'s
  Client job publishes self-contained win-x64 and win-arm64 and checks each exe's PE machine type.

### 5. Architecture-aware Client download

- [x] Change `GitHubReleasesClient.DownloadLatestClientAsync` to require `MachineArchitecture`.
- [x] Resolve the corresponding release asset and checksum line, falling back to the legacy
  x64-only asset name when the alias release predates architecture-specific assets.
- [x] Validate PE machine type after extraction, in addition to the current self-contained-runtime
  check.
- [x] Include expected and actual architectures in mismatch errors.
- [x] For local-source mode, perform the same PE architecture validation before generation begins
  (`EnsureClientBinariesAreSelfContained`, §3).

### 6. Boot-image catalog and Portal

- [x] Persist architecture in Imaging Core boot-image table entities
  (`BootImageRepository`/`UploadJobRepository` `ToEntity`/`FromEntity`).
- [x] Carry it through Imaging Core, Operator API, Portal backend, and Portal TypeScript
  contracts. Operator API and the Portal Express backend are opaque JSON proxies for this payload,
  so no change was needed in either — only Imaging Core's DTO mapping and the Portal React client's
  typed contracts needed updating.
- [x] Require architecture when starting a boot-image upload with **no default**. The Portal reads
  it from the WIM's XML metadata and locks the selector; the operator only chooses when the image
  records no architecture. Imaging Core re-reads the metadata at publish and rejects a mismatch.
- [x] Display an architecture badge or column in Boot Images. The image-inventory report appends
  the architecture to the boot image name rather than adding a column, since OS and recovery
  images carry no architecture yet (Milestone 2).
- [x] Change `IsLatestPublished` behavior from one global latest image to one latest image per
  architecture.
- [x] Decouple publish from latest. Uploads land in **pre-production** (`IsProduction = false`,
  not latest): Administrators can prepare test media with them in Media Builder, Technicians and
  devices never see them. `POST /api/boot-images/{id}/promote` (Portal, Administrator) sets
  `IsProduction` and makes the image latest for its own architecture only; promoting an older
  production image is the rollback path. The capacity rotation never demotes the latest image, and
  a test stick (identified by `UsbPreparationManifest.BootImageId`) is not self-updated back to
  production while its pre-production image exists. `POST /api/boot-images/{id}/demote` reverts a
  promote, restoring the previously promoted image as latest for that architecture.
- [x] Decide catalog capacity by architecture: five active images per architecture.
- [x] Migrate existing catalog rows to x64 and preserve the current x64 latest image. No explicit
  migration script was needed: `BootImageRepository.FromEntity`/`UploadJobRepository.FromEntity`
  already treat a row with no `Architecture` column as x64, and existing `IsLatestPublished` rows
  are untouched by this change.

### 7. Prepare USB and ISO

- Include architecture in Media Builder boot-image choices and selected-image state.
- Pass architecture through `UsbPreparationService`, elevated-worker parameters,
  `BootImageDeploymentService`, and `IsoGenerationService`.
- Parameterize ARM64 `copype.cmd` and Oscdimg paths during ISO generation.
- For x64 USB media, preserve current behavior.
- For ARM64 USB media:
  - configure UEFI only;
  - do not request legacy BIOS files;
  - verify `\efi\boot\bootaa64.efi` and the UEFI BCD store exist before reporting success.
- Record architecture in the USB preparation manifest.
- Include architecture in default ISO names and completion messages.

### 8. Boot-image self-update

- Carry current boot architecture into Client boot-image update requests.
- Return the latest boot image for the requested architecture, not the global latest image.
- Reject an update response with mismatched architecture before downloading or replacing media.
- Preserve legacy behavior as x64 when older USB manifests do not contain architecture.

Milestone 1 exit criteria:

- x64 generation, publish, ISO, USB, and self-update behavior remains unchanged.
- ARM64 generation produces a cataloged WIM with matching ARM64 Client binaries.
- Media Builder produces bootable ARM64 ISO and USB media.
- The latest image is resolved independently for x64 and ARM64.
- Publishing a boot image never implicitly makes it the latest; promotion is a separate, explicit
  action per architecture.
- No path can prepare ARM64 media from an image labeled x64 or vice versa.

Estimated effort: **6 to 8 engineering days after the runtime spike**.

## Milestone 2: safe ARM64 operating-system imaging

Status (2026-09-24): implemented except the physical-device exit criterion and §6 (installed-OS
boot configuration on real ARM64 hardware). Summary:

- Client reports `RuntimeInformation.OSArchitecture` at registration; sessions and session history
  persist it (missing = x64) and the Portal shows it on coupled devices and in session details.
- OS and recovery images carry an architecture, read from the WIM XML metadata in the Portal and
  again in Imaging Core (inline for WIM/ESD and recovery WIMs, after extraction for ISOs). The
  upload dialogs have no default; the operator chooses only when the image records none (ISOs).
- Recovery images use per-architecture latest and capacity; the Device Gateway resolves
  `recovery-image/latest?architecture=`. When no compatible WinRE is published, the Client applies
  the WinRE embedded in the applied OS image, which always matches.
- Imaging Core rejects incompatible single/bulk assignment and SAS refresh with a 409
  `architecture-mismatch` ProblemDetails before issuing any SAS. The Portal only offers images
  matching the coupled devices and bulk-assigns one architecture at a time.
- The Client re-checks the assigned image's architecture (from the status response) before
  formatting the disk and fails with support code `ARC` without touching it.
- Not done: independent WIM inspection of ESD files whose XML resource is compressed (the
  operator's selection is then trusted).

### 1. Device architecture inventory

- Add architecture to `DeviceRegistrationPayload`, `DeviceSession`, session history, persistence
  entities, and Portal session DTOs.
- Derive it in Client from the running process/OS architecture using a platform API rather than a
  localized WMI display string.
- Treat missing architecture on legacy sessions as x64.
- Display architecture in session details and reports.

### 2. OS-image architecture

- Add required architecture to `OsImage`, upload-start requests, upload jobs, persistence, APIs,
  and Portal types.
- Require explicit architecture in the OS-image upload dialog.
- Show architecture in OS Images and inventory reports.
- Migrate existing OS-image rows to x64.
- Client's `ImageCacheService` already keys cache entries by image ID rather than a single slot,
  so x64 and ARM64 images naturally cache side by side once their IDs differ. Keep it that way:
  do not add logic that evicts one architecture's cached images when a device couples to the
  other architecture. Both must stay cached simultaneously, subject only to the existing
  TTL/size-based eviction.

The first release may trust the explicit upload selection because Imaging Core runs on Linux and
does not have DISM available to inspect WIM metadata. A later hardening step can add independent
WIM architecture inspection. Until then, upload UI must warn that incorrect labeling can produce
unbootable media.

### 3. Recovery-image architecture

- Add architecture to `RecoveryImage` and every corresponding upload, persistence, API, and Portal
  contract.
- Require explicit architecture in the recovery-image upload dialog, matching the boot-image and
  OS-image upload flows. Defaulting to x64 would hide mistakes here the same way it would there.
- Change latest recovery-image resolution to be per architecture.
- Apply the same promotion workflow as boot images (Milestone 1 §6): publishing a recovery image
  must not automatically make it latest; a separate promote action does, scoped to that image's
  own architecture.
- Ensure an ARM64 session receives ARM64 WinRE or explicitly skips recovery when no compatible
  image exists, according to a documented product decision.
- Migrate existing recovery images to x64.

### 4. Assignment compatibility enforcement

- Filter the Portal OS-image picker to images compatible with every selected/coupled session.
- For mixed-architecture bulk selection, disable assignment and explain that devices must be split
  by architecture.
- Enforce compatibility again in Imaging Core for single and bulk assignment. UI filtering is not
  a security or correctness boundary.
- Return a typed conflict response containing session architecture and image architecture.
- Never issue a SAS URL for an incompatible OS or recovery image.
- Re-check compatibility when a session resumes or refreshes assignment state.

### 5. Client pre-destructive validation

- Include assigned OS-image and recovery-image architectures in device status responses.
- Before disk partitioning or formatting, Client must verify:
  - session architecture matches the running Client architecture;
  - OS-image architecture matches the session;
  - recovery-image architecture matches when recovery is enabled.
- Fail before destructive work with a clear terminal error when any value mismatches.
- Preserve diagnostics in Client logs and session failure details.

### 6. Installed-OS boot configuration

- Exercise the existing Client boot-configuration stage against an applied ARM64 Windows image.
- Verify it creates ARM64 UEFI boot files and does not request BIOS support.
- Verify recovery configuration uses the ARM64 WinRE artifact.
- Add architecture-specific error messages for missing EFI firmware files.

Milestone 2 exit criteria:

- ARM64 sessions can only receive ARM64 OS and recovery images.
- Incompatible assignments are blocked in both Portal and Imaging Core.
- Client validates all architecture metadata before erasing the target disk.
- A physical ARM64 device completes imaging and boots into the deployed ARM64 Windows image.

Estimated effort: **4 to 7 engineering days**, excluding delays obtaining hardware, drivers, and
licensed ARM64 Windows media.

## Data migration and compatibility

Existing deployments have no architecture values. Use an explicit, idempotent migration strategy:

1. Readers interpret missing architecture as `X64`.
2. Repository writes always persist architecture after this feature ships.
3. Upgrade script or lazy repository migration backfills existing boot, OS, and recovery image
   rows as `X64`.
4. Existing sessions and history remain readable as x64.
5. Existing `cloud-imaging-client.zip` remains available during the compatibility window.
6. API additions are initially additive; do not rename or remove existing JSON properties.

After at least one stable release, required-field validation can reject new writes that omit
architecture. Legacy reads must remain tolerant indefinitely unless a mandatory migration is
introduced.

## Testing matrix

### Automated

- Enum JSON serialization and legacy defaulting tests.
- Architecture mapping tests for ADK name, RID, and EFI loader.
- [x] Client release asset and checksum selection tests
  (`GitHubReleasesClientTests`: arch-specific resolution, legacy x64 fallback, arm64-with-no-
  fallback error, and post-extraction PE mismatch detection).
- [x] PE machine-type validation tests for x64 and ARM64 executables (`PeTestFileFactory` +
  `BootImageGenerationTests`/`GitHubReleasesClientTests`).
- Parameterized boot generation command tests for `amd64` and `arm64`.
- Elevated IPC round-trip tests preserving architecture.
- Manifest serialization and schema-version tests.
- Table entity mapping and migration tests for all three image catalogs and sessions.
- Latest boot/recovery image selection tests per architecture.
- Promotion workflow tests: publish never sets latest; an explicit promote action sets latest for
  the promoted image's architecture and demotes only the prior holder of that same architecture.
- OS-image cache retention tests proving x64 and ARM64 entries coexist without evicting each
  other.
- Single and bulk assignment mismatch tests.
- Client pre-destructive validation tests proving no partition command runs after mismatch.
- ISO command tests using the selected ADK architecture.
- USB deployment tests for `bootx64.efi` and `bootaa64.efi`.
- Portal tests for upload selection, badges, filtering, mixed-architecture bulk assignment, empty
  compatible-image states, and error messages.
- x64 regression tests across all touched workflows.

### Manual and hardware

| Scenario | x64 | ARM64 |
|---|---:|---:|
| Generate WIM | Required | Required |
| Boot ISO | Required | Required |
| Boot USB via UEFI | Required | Required |
| Client WPF startup | Required | Required |
| Network and mTLS registration | Required | Required |
| Couple and assign compatible OS image | Required | Required |
| Reject incompatible OS image | Required | Required |
| Download and apply OS image | Required | Required |
| Configure UEFI boot | Required | Required |
| Apply compatible recovery image | Required | Required |
| First boot into Windows | Required | Required |
| Boot-image self-update | Required | Required |

## Documentation and release changes

- Document ARM64 ADK prerequisites and how to verify the ARM64 WinPE component is installed.
- Document architecture-specific Client release assets.
- Add architecture selection to Media Builder generation and preparation guides.
- Document ARM64 driver requirements and UEFI-only behavior.
- Update operations troubleshooting for missing `bootaa64.efi`, wrong-architecture drivers,
  mismatched image assignments, and ARM64 network initialization.
- Add release notes stating that existing artifacts are treated as x64.
- Update VS Code publish tasks only if local Client publishing gains an architecture input;
  otherwise keep explicit x64 and ARM64 tasks to prevent accidental overwrites.

## Recommended PR sequence

1. **ARM64 runtime spike and architecture mapping**
   - Proof artifacts and automated mapping tests only.
2. **Dual-architecture Client release**
   - Release assets, checksums, downloader selection, PE validation.
3. **Architecture-aware boot generation**
   - Media Builder UI, ADK paths, elevation IPC, manifest.
4. **Boot catalog and per-architecture latest semantics**
   - Contracts, persistence, APIs, Portal, migration, and the explicit promote-to-latest action
     that replaces auto-latest-on-publish for boot and recovery images.
5. **Architecture-aware ISO, USB, and self-update**
   - UEFI-only ARM64 boot chain and fallback loader validation.
6. **Device and OS/recovery architecture contracts**
   - Registration, sessions, all image catalogs, Portal metadata.
7. **Assignment enforcement and Client safety gate**
   - Single/bulk API checks and pre-destructive Client validation.
8. **End-to-end validation and documentation**
   - Physical ARM64 completion, x64 regression, operational guidance.

Each PR must leave x64 functional and independently deployable. Do not merge schema producers
before their consumers tolerate the new field.

## Effort estimate

| Workstream | Estimate |
|---|---:|
| Runtime proof spike | 1 to 2 days |
| Dual Client release and selection | 1 to 2 days |
| Media Builder generation and manifest | 2 to 3 days |
| Boot catalog, Portal, and migration | 2 to 3 days |
| ISO, USB, and self-update | 2 to 3 days |
| Device and OS/recovery metadata | 2 to 3 days |
| Assignment enforcement and Client safety | 2 to 3 days |
| Automated and physical validation, docs | 2 to 4 days |

Expected total: **14 to 23 engineering days** for production-ready, end-to-end ARM64 imaging.
The lower end assumes existing ARM64 hardware, network/storage drivers, and licensed ARM64 Windows
media are immediately available. A boot-image-only prototype remains approximately **4 to 6 days**
but must not be presented as complete ARM64 imaging support.

## Open decisions

1. Which ARM64 physical devices and VM platform form the supported validation baseline?
2. Where will licensed ARM64 Windows and WinRE source images come from?
3. Should missing compatible recovery media block assignment, or allow imaging without recovery?
4. Should boot and recovery catalog capacity be five entries per architecture or a larger shared
   limit? Recommendation: five per architecture.
5. How long should the legacy `cloud-imaging-client.zip` x64 compatibility asset remain published?
   Recommendation: at least one stable Client release after dual-asset support ships.
6. Should Portal trust operator-selected WIM architecture for the first release, or must an
   independent WIM metadata inspection service ship before ARM64 is enabled?
7. Is ARM64 support permitted only when every selected session has known architecture, or may
   legacy unknown sessions be treated as x64? Recommendation: legacy unknown is x64, but all new
   registrations must provide an explicit value.

## Definition of done

ARM64 support is complete only when all of the following are true:

- Media Builder produces x64 and ARM64 boot WIMs from one x64 technician workstation.
- Published Client assets and boot-image catalogs identify architecture unambiguously.
- ISO, USB, and self-update paths preserve architecture.
- Device sessions and OS/recovery image catalogs carry architecture.
- Portal and Imaging Core prevent incompatible assignment.
- Client validates compatibility before destructive disk work.
- ARM64 USB media boots on physical hardware, completes imaging, and boots the installed OS.
- Existing x64 workflows pass their full regression suite without behavior changes.
- Upgrade and operations documentation covers migration, prerequisites, and failure recovery.

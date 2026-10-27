# Contract: Driver sets on the USB drive (Media Builder and Client cache)

**Feature**: [../spec.md](../spec.md) (User Story 11, FR-093 to FR-098) | **Research**: R30 | **Layout**: [../data-model.md](../data-model.md) section 4.1

## 1. Operator API and Core

Two new Operator API functions, added to `MediaBuilderAllowedFunctions` and
`MediaBuilderOnlyFunctions` in `AppRoleAuthorizationMiddleware` (role
`CloudImaging.MediaBuilderAccess`; `PortalAccess` may not call them), each proxying to Core like
`GetBootImageSasUrl`:

| Operator API function | Route | Core route | Response |
|---|---|---|---|
| `GetDriverSetsForMediaBuilder` | `GET /api/media-builder/driver-sets?architecture=X64` | `GET internal/drivers/media-builder/sets?architecture=` | `[{ modelId, vendor, modelName, release, setVersion, components, bytes }]`: the current set of every onboarded model and release for that architecture. 400 on an unknown architecture. |
| `GetDriverSetLinksForMediaBuilder` | `POST /api/media-builder/driver-sets/links` `{ sets: [{ modelId, release }] }` | `POST internal/drivers/media-builder/links` | `{ sets: [{ modelId, release, setVersion }], files: [{ sha256, fileName, sizeBytes, url, expiresAt }] }`. Files shared by several sets appear once. One user-delegation read SAS per file, 60 minutes, current sets only. At most 20 sets (400 above); 404 when a model or release is not onboarded or has no current set. |

Both are reads; neither pins nor changes anything. Custom component packages in a set are included
like vendor files. OpenApiConfig entries and contract tests in
`tests/CloudImaging.OperatorApi.Tests/Contracts/MediaBuilderDriverSetContractTests.cs` (role
allowlist: MediaBuilderAccess allowed, PortalAccess refused).

## 2. Media Builder: Prepare USB Storage Device

New optional section "Driver sets" in `PrepareStorageDeviceView.xaml`, below the location and Wi-Fi
choices, loaded after the boot image is chosen (its architecture filters the list):

- Description: "Copies drivers to the USB drive, so devices of these models do not download them."
- One checkbox row per model and Windows release, grouped by vendor (vendor names from the
  response, never written into the view): model name, "Windows 11, version 24H2", size. Nothing is
  selected by default.
- Summary line under the list: "2 driver sets, 4.8 GB. 19.2 GB stays free for OS images." Shared
  files are counted once.
- Less than 20 GB would stay free for OS images: warning InfoBar "Less than 20 GB stays free for
  OS images. Larger images are then downloaded every time instead of cached."
- The selection does not fit the CACHE partition: error InfoBar "The selected driver sets do not
  fit on this USB drive. Clear some of them or use a larger drive." and Prepare is disabled.
- No models onboarded for the architecture: the section shows "No driver sets are available for
  ARM64 boot images." and nothing else.

Preparation order (the elevated worker has no network access):

1. Non-elevated: `DriverSetLinksAsync`, then download each file not already in
   `%LOCALAPPDATA%\Cloud Imaging Media Builder\Cache\drivers\<sha256>\` with the boot image
   download's retry (3 attempts, 2 s and 4 s), verifying SHA-256. Progress: "Downloading driver
   sets (12 of 58 files, 1.1 of 4.8 GB)". Any file still failing stops before partitioning:
   "2 driver files could not be downloaded. Try again, or clear the driver sets."
2. `PreparationParams` gains `DriverFiles: [{ sha256, fileName, sizeBytes, sourcePath }]` and
   `DriverSets` (manifest entries).
3. Elevated worker, after formatting and the boot image: copy each file to
   `CACHE:\CloudImagingCache\drivers\<sha256>\<fileName>` while hashing, write `metadata.json`
   last, delete the entry on a mismatch and fail with "A driver file was damaged while copying to
   the USB drive. Prepare the drive again."
4. The manifest records `DriverSets`; the result dialog adds a line per set ("HP EliteBook 840/860
   G11, Windows 11, version 24H2").

The local Media Builder cache keeps driver files for 30 days after last use, like the boot image
cache, so preparing several drives downloads once.

## 3. Client

See [client-install-drivers.md](client-install-drivers.md) section 2, steps 2 and 3:
`DriverFileCache` (new, `Services/Drivers/DriverFileCache.cs`) finds the CACHE volume the way
`ImagingWorkflowViewModel.FindCacheVolumeDriveLetter` does, copies hits to the Windows volume with
a SHA-256 check, removes entries that fail, writes verified downloads (metadata last), removes
entries without `metadata.json` when the step starts, and reads even when writes are disabled for
lack of space. `ImageCacheMaintenanceService` applies its 30-day idle purge to `drivers\` as well.

## 4. Portal

The Install drivers card (portal-client-surfaces.md section 9) adds "24 from the USB drive, 2
downloaded" to its summary when any item has `fromUsbCache`, and the item list shows "USB drive"
under the outcome for those items.

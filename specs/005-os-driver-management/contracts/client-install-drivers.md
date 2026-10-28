# Contract: Client Install drivers step

**Feature**: [../spec.md](../spec.md) | **Descriptor**: [device-gateway.md](device-gateway.md) | **Hand-off**: spec 003 `contracts/setup-runner-and-unattend.md`

## 1. Placement

`ImagingWorkflowViewModel.RunAsync`: Format disk, Download image, Apply image, **Install
drivers**, Prepare setup, Configure boot, Apply recovery image. The descriptor is fetched right
after Apply image. The step is shown when the descriptor returns 200 (with or without
`skipReason`) and not shown on 404 (older backend). `ProgressViewModel.ConfigureSteps` gains
`includeInstallDrivers`, inserted after Apply image. `ShouldRunPrepareSetup` already returns true
when the installer list is not empty.

## 2. Per component (in descriptor order)

New service `Services/Drivers/DriverInstallService.cs` with vendor extractors behind
`IDriverExtractor` (`HpSoftPaqExtractor`, `DellPackageExtractor`):

1. **Hardware match** (Dell components with `pciIds`): skip with outcome `Skipped`, reason "No
   matching hardware in this device." when no `Win32_PnPEntity` hardware ID matches (inventory read
   once per step, `Services/Drivers/PciInventory.cs`).
2. **From the USB drive** (when a CACHE volume exists): `DriverFileCache.TryCopyAsync(sha256,
   destination)` looks up `CACHE:\CloudImagingCache\drivers\<sha256>\`, copies the file to
   `<W>:\CloudImaging\Drivers\<index:000>-<componentId>\<fileName>` while hashing the copy, and
   returns a hit only when the copy's SHA-256 matches. A mismatch deletes the copy and the cache
   entry and falls through to the download. Lookups run even when cache writes are disabled for
   lack of space. The item records `fromUsbCache: true` on a hit.
3. **Download** (no hit) to the same path with the existing retry (3 attempts, 2 s and 4 s),
   streaming SHA-256 while writing. Mismatch: delete, retry; still wrong after 3 attempts:
   `Failed`, "The downloaded file does not match its checksum.". A 403 or near-expiry URL: refresh
   the link once (device-gateway section 2). After a verified download, `DriverFileCache.AddAsync`
   copies the file into the cache (file first, `metadata.json` last) unless less than 500 MB would
   stay free or there is no CACHE volume; a failed cache write is logged and never fails the
   component. Nothing is evicted during the step; the 30-day idle purge of `ImageCacheMaintenanceService`
   covers `drivers\` too.
4. **Extract** (actions other than Dell 32-bit Specialize and custom installers) into `...\<folder>\x` through
   `RestrictedProcessRunner`, timeout 10 minutes: HP `"<file>" /s /e /f "<x>"`, on no output
   retry `"<file>" -pdf -e -s -f"<x>"`; Dell `"<file>" /s /drivers="<x>"`; custom ZIP (`extract`
   `Zip`): unpacked in process with `System.IO.Compression.ZipFile`, every entry's full path
   checked to stay inside `x` (an entry outside it fails the component). Success: Offline actions
   need at least one `.inf` under `x`; Specialize actions need the install command's executable.
   Otherwise `Failed`, "The package could not be extracted.". The volume has too little space:
   `Failed`, "The Windows volume does not have enough free space to extract this package.".
5. **Inject** (Offline, Offline + Specialize): `dism.exe /Image:<W>:\ /Add-Driver /Driver:"<x>"
   /Recurse` (no `/ForceUnsigned`), output captured to the log; non-zero exit: `Failed`, "Windows
   could not add the drivers (DISM exit code N).".
6. **Hand off** (Offline + Specialize, Specialize): build a `SpecializeInstaller`: `Name` the
   component name; `Files` every file under the staged folder with its SHA-256, relative to the
   volume root; `Command` from `installCommand`, working directory the staged folder; timeout 1200
   seconds; success exit codes 0 and 3010; cleanup path the component folder. Dell 32-bit
   Specialize stages the verified package itself with `"<file>" /s`.
7. **Clean up**: delete the component folder unless it was handed off.

Downloads, hashing, extraction and DISM run off the UI thread; the download buffer is 1 MB and
nothing is held in memory beyond it (working set under 512 MB).

## 3. Outcomes (step items, kind `DriverComponent`)

Every item carries `componentVersion` and `driverOrigin` (`Vendor`, `Custom`, `RolledBack`) from the
descriptor, so the timeline shows what was installed after the set is gone, and `fromUsbCache`
(true when the file came from the USB drive). Skipped components carry no `fromUsbCache`.

| Situation | Outcome | `driverAction` | Reason |
|---|---|---|---|
| Offline injected | `Succeeded` | `Offline` | |
| Offline injected and app staged | `Succeeded` | `OfflineAndSpecialize` | |
| Specialize staged | `Queued` | `Specialize` | |
| No matching hardware | `Skipped` | action | "No matching hardware in this device." |
| Any failure | `Failed` | action | One sentence, see section 2 |

## 4. Step result

| Case | Status | Warning count | `statusNote` |
|---|---|---|---|
| `skipReason` set | `Completed` | 1 (0 for NoneChosen) | NoMatchingModel "No onboarded model matches this device (system ID 8D41)." ("system SKU" for Dell; without a `matchKey` the parenthesis is left out); NoWindowsRelease "The OS image has no Windows release set."; ReleaseNotOnboarded "This model is not onboarded for the image's Windows release."; NoDriverSet "This model has no driver set yet. Run a sync."; ArchitectureMismatch "The OS image's architecture does not match this model."; NoneChosen "No drivers were chosen for this device when imaging started." |
| A component in a critical category failed | `Failed` | failed count | Error detail lists the failed critical components (at most 500 chars). Prepare setup and Configure boot do not run; the existing retry path applies. |
| Other failures | `Completed` | failed count | |
| All succeeded | `Completed` | 0 | |

Status lines: "Copying drivers from the USB drive (3 of 31)", "Downloading drivers (3 of 31)",
"Adding drivers (3 of 31)", "Staging Specialize installers". Percent reports use the existing
throttle; one final report carries all items.

## 5. Specialize (spec 003, unchanged)

Prepare setup lists the handed-off installers first in the setup manifest and adds the setup
runner command to the answer file. The setup runner verifies each listed file, runs the command as
SYSTEM with the timeout and restart guard, logs the exit code to
`C:\Windows\Logs\CloudImaging\SetupRunner.log`, continues after failures and deletes the staged
folders when it ends. Nothing is reported to the portal after the restart.

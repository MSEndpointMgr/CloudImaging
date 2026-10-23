# Contract: Prepare Setup Step, Setup Runner Manifest and Answer File Merge

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) | **Research**: [../research.md](../research.md) R5 to R8, R10

This contract is shared with spec 005 (OS driver management): its Install drivers step hands
Specialize work to Prepare setup through section 2, and its installers run through the runner of
section 4.

`<W>` is the applied Windows volume's drive letter in WinPE (for example `W`). On the deployed
device the same volume is the system drive (`C:`), which `OfflineDriveLetterService` guarantees.

---

## 1. Step position and phases

```text
Format disk > Download image > Apply image > [Install drivers, spec 005] > Prepare setup > Configure boot > Apply recovery image
```

Prepare setup runs when the session has a pinned profile **or** Install drivers handed over at
least one Specialize item. Otherwise it is skipped and not reported.

Phases, in order (FR-025):

| # | Phase | Items reported | Notes |
|---|---|---|---|
| 1 | Write registry values | one `RegistryValue` item per value | Hives loaded only as needed and unloaded before phase 2. The SYSTEM hive load also reads `GeneralizationState` and clears `Setup\UnattendFile`. |
| 2 | Run In WinPE scripts | one `WinPEScript` item per script | Profile order; each in a restricted, kill-on-close job with its timeout. |
| 3 | Stage Windows setup work | one `WindowsSetupScript` item per script (`Queued` or `Failed`), one `DriverInstaller` item per handed-over installer (`Queued` or `Failed`) | Copies runner and scripts, writes the manifest. Skipped when there is no Windows setup work. |
| 4 | Write the answer file | one `AnswerFile` item | Always when the step runs. |

Under Fail the deployment the step stops at the first failed item; every later item is reported
`NotRun` with reason "Not run because an earlier item failed and this profile fails the deployment."

---

## 2. Hand-off from Install drivers (spec 005)

Install drivers passes a list of `SpecializeInstaller` records (Client in-memory type,
`Client/Services/Setup/SpecializeInstaller.cs`), already staged on `<W>:`, in resolved order:

| Field | Type | Rules |
|---|---|---|
| `name` | string | Component name shown in logs and timeline items. |
| `files` | `{ path, sha256 }[]` | `path` relative to the volume root (for example `CloudImaging\Drivers\sp151234\setup.exe`); every file the installer needs verified. |
| `command` | `{ executable, arguments, workingDirectory }` | `executable` relative to the volume root or a system tool name (`msiexec.exe`); arguments must include the vendor's silent and no-restart switches. |
| `timeoutSeconds` | int | 60 to 3600. |
| `successExitCodes` | int[] | `[0, 3010]` for driver installers. |
| `cleanupPaths` | string[] | Relative folders the runner deletes after the run. |

Prepare setup does not copy driver payloads; it verifies each listed file exists, lists the
installers first in the manifest, and reports each as a `DriverInstaller` item.

---

## 3. Staging layout on `<W>:`

```text
<W>:\Windows\Setup\CloudImaging\
    Invoke-CloudImagingSetup.ps1      setup runner, copied from the Client folder (X:\CloudImaging\SetupRunner\)
    manifest.json                     section 4
    Scripts\
        01.ps1                        During Windows setup scripts, profile order, UTF-8 with BOM
        02.ps1
<W>:\Windows\Panther\unattend.xml     section 5
<W>:\Windows\Logs\CloudImaging\       created by the runner on first write
```

`Windows\Setup` is writable only by administrators and SYSTEM on the deployed OS, so users cannot
tamper with staged items before specialize (there are no users before OOBE in any case).

---

## 4. Setup manifest (`manifest.json`, UTF-8, no BOM)

```json
{
  "schemaVersion": 1,
  "createdAt": "2026-10-23T10:02:11Z",
  "sessionId": "5b7e0f0e-6a0c-4b8e-9a0d-1c2b3d4e5f60",
  "profile": { "name": "Stockholm and Gothenburg", "version": 8 },
  "items": [
    {
      "order": 1,
      "kind": "DriverInstaller",
      "name": "HP Hotkey Support",
      "files": [ { "path": "CloudImaging\\Drivers\\sp151234\\setup.exe", "sha256": "a3c1...0f" } ],
      "command": { "executable": "CloudImaging\\Drivers\\sp151234\\setup.exe", "arguments": "/s /norestart", "workingDirectory": "CloudImaging\\Drivers\\sp151234" },
      "timeoutSeconds": 1200,
      "successExitCodes": [0, 3010],
      "cleanupPaths": [ "CloudImaging\\Drivers\\sp151234" ]
    },
    {
      "order": 2,
      "kind": "Script",
      "name": "Wired 802.1X for the office LAN",
      "files": [ { "path": "Windows\\Setup\\CloudImaging\\Scripts\\01.ps1", "sha256": "77de...4a" } ],
      "command": { "executable": "Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "arguments": "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\"", "workingDirectory": "Windows\\Setup\\CloudImaging\\Scripts" },
      "timeoutSeconds": 300,
      "successExitCodes": [0],
      "cleanupPaths": []
    }
  ],
  "cleanupPaths": [ "Windows\\Setup\\CloudImaging" ]
}
```

Rules:
- All paths are relative to the system drive root; the runner prefixes `$env:SystemDrive\`. No
  path may be rooted or contain `..`; the runner skips an item whose resolved path leaves the
  system drive.
- `{0}` in `arguments` is replaced with the quoted absolute path of the item's first file.
- `sha256` is computed by the Client over the bytes it wrote (scripts include the BOM).
- `profile` is null for a device with driver work only.
- Driver installers precede scripts; within each group the order is resolved order or profile order.
- Built by a pure `SetupRunnerManifestBuilder` (unit-tested); serialized with string enums.

### Runner behaviour (`Invoke-CloudImagingSetup.ps1`)

| Concern | Behaviour |
|---|---|
| Start | Runs as SYSTEM from RunSynchronous in specialize. Parameters `-ManifestPath` (default `$env:WINDIR\Setup\CloudImaging\manifest.json`) and `-LogPath` (default `$env:WINDIR\Logs\CloudImaging\SetupRunner.log`). |
| Missing or unreadable manifest | Logs one line, cleans up, exits 0. |
| Per item | Verify every file's SHA-256. Mismatch or missing file: log `Failed` with "File does not match the manifest", skip the item. |
| Launch | Restricted token without `SeShutdownPrivilege` and `SeRemoteShutdownPrivilege`, process created suspended, assigned to a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and `JOB_OBJECT_UILIMIT_EXITWINDOWS`, resumed. Implemented in an `Add-Type` C# block compiled once per run. If the block cannot compile or the restricted token cannot be created, no item runs: each is logged `Failed` with "Not run because restarts could not be blocked on this device", because a restart inside specialize would break Windows setup. |
| Timeout | Terminate the job (whole tree), log `Failed` with "Stopped after N minutes", continue. |
| Result | Exit code in `successExitCodes`: `Succeeded`; otherwise `Failed`. 1641 and 3010 for scripts are `Failed` (a script must not request a restart). |
| Continue | Always continues with the next item. |
| Log line | `2026-10-23T10:15:02Z  2/3  Script  "Wired 802.1X for the office LAN"  Succeeded  exit 0  12.4 s` (ASCII; two spaces between fields). Start and end lines carry the profile name and version or "No setup profile". |
| Cleanup | In `End` and in the top-level `catch`: delete every item's `cleanupPaths`, then the manifest's `cleanupPaths`. Each delete is best effort and logged on failure. The log folder is never deleted. |
| Exit | Always 0. |
| Restarts | Never restarts the device and never returns 3010 or 1641 to Windows setup. |

The script follows `.github/copilot-instructions.md` General PowerShell Practices (help block with
`.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER`, `.EXAMPLE`, `.NOTES`; Begin/Process/End; `[PSCustomObject]`;
generic `List[System.Object]`; `catch [System.Exception]` with `Write-Warning`; no `Write-Host`;
no backtick continuation; `HelpMessage` and `[ValidateNotNullOrEmpty()]` on parameters).

---

## 5. Answer file merge (`UnattendComposer`, pure, in the Client)

Input: the profile's answer file (or none), whether there is Windows setup work, the device
architecture. Output: the XML written to `<W>:\Windows\Panther\unattend.xml`.

1. Start document: the profile's `answerFile.content`, or
   `<?xml version="1.0" encoding="utf-8"?><unattend xmlns="urn:schemas-microsoft-com:unattend"/>`.
   Parse with `DtdProcessing.Prohibit` and `XmlResolver = null`.
2. Without Windows setup work: write the start document unchanged.
3. With Windows setup work, in namespace `urn:schemas-microsoft-com:unattend`:
   1. `settings[@pass='specialize']`: use the first, or append one.
   2. In it, `component[@name='Microsoft-Windows-Deployment'][@processorArchitecture=A]` where A is
      `amd64` (x64) or `arm64` (ARM64): use the first, or append one with
      `publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"`,
      `xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"` and
      `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"`.
   3. In it, `RunSynchronous`: use the first, or append one.
   4. Existing `RunSynchronousCommand` children: stable sort by numeric `Order` (missing or invalid
      `Order` sorts last in document order), set `Order` to 2, 3, 4, and so on.
   5. Insert as the first child:

```xml
<RunSynchronousCommand wcm:action="add">
  <Order>1</Order>
  <Description>Cloud Imaging setup runner</Description>
  <Path>%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%WINDIR%\Setup\CloudImaging\Invoke-CloudImagingSetup.ps1"</Path>
</RunSynchronousCommand>
```

   6. Components for other architectures and every other element are left byte-for-byte equivalent
      (same elements, attributes and text; whitespace may be normalized).
4. Serialize UTF-8 with declaration, indented.
5. Post-condition (asserted by the step after writing and by tests): with Windows setup work,
   exactly one `RunSynchronousCommand` whose `Path` ends with `Invoke-CloudImagingSetup.ps1"`
   exists in the whole document, with `Order` 1, in the device-architecture component; without
   Windows setup work, none.

Write rules (`PrepareSetupService`):
- Create `<W>:\Windows\Panther` if missing; overwrite `unattend.xml` without a warning (FR-021).
- Delete `<W>:\Windows\Panther\Unattend\unattend.xml` and `...\autounattend.xml` if present.
- `HKLM\SYSTEM\Setup\UnattendFile` in the offline hive was cleared in phase 1.
- With Windows setup work and offline `Setup\Status\SysprepStatus\GeneralizationState` not 7:
  the `AnswerFile` item fails with "The OS image is not generalized, so Windows setup will not run
  the setup runner. Capture the image after Sysprep /generalize."
- Without a profile (driver work only), a write failure's reason names the installers that will
  not run: "The answer file could not be written, so these driver installers will not run: HP
  Hotkey Support, Realtek Audio Console."

---

## 6. Client-side text (user-facing, ASCII)

| Where | Text |
|---|---|
| On-device step label | Prepare Setup (title case, like the Client's other step labels; the portal uses "Prepare setup") |
| On-device status line | "Writing registry values", "Running scripts (2 of 3)", "Staging Windows setup work", "Writing the answer file" |
| Results screen, warnings | "Imaging completed with warnings. 1 setup item failed; see the deployment timeline in the portal." |
| No PowerShell | "This boot image has no PowerShell, so the script cannot run. Generate a new boot image with Media Builder and update the USB media." |
| Timeout | "Stopped after 5 minutes." |
| Hive missing | "The default user profile was not found in the applied image." |
| Script exit | "Exited with code 1." |

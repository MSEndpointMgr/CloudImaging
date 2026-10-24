# Setup profiles

A setup profile prepares the installed Windows before it starts for the first time, so the settings
Autopilot and Intune depend on are already in place when the device reaches OOBE. You manage
profiles in the portal under **Imaging** > **Setup Profiles**.

A profile contains:

- **Registry values**, written to the installed Windows from the boot image.
- **PowerShell scripts**, each run either **Offline** (in the boot image right after the image is
  applied) or during **Specialize** (in Windows setup before OOBE), at all of the profile's
  locations or only at the ones you choose.
- An optional **answer file** (`unattend.xml`) for Windows setup settings the profile has no
  control for.
- A **failure handling** setting that decides what a failed item does to the deployment.

Operators and Administrators create, edit, duplicate, delete and restore profiles, scripts and
answer files included. Technicians can open profiles and their history without edit controls.
See [Roles and access](roles-and-access.md).

---

## How a device gets its profile

A profile targets either **All devices** or one or more **Selected locations**. The device's
location is the one set on the USB media it started from (Media Builder, **Prepare USB Storage
Device**).

1. A profile that targets the device's location wins.
2. Otherwise the All devices profile applies. That includes media prepared without a location and
   media prepared for a location added after the profile was saved.
3. Otherwise the device gets no profile, and imaging runs exactly as it did before setup profiles.

A location belongs to at most one profile, and only one profile can target All devices. The
location picker disables a location another profile already targets and names that profile. A
profile always targets something: Save stays disabled until you pick a location or All devices.

Deleting a location removes it from the profile that targets it, and the change is recorded in the
profile's history. When the location is the profile's only location, the delete is refused: add
another location to the profile, or delete the profile, then try again.

The Start imaging overlay on the **Devices** page lists which profile each group of selected
devices will get, for example "3 devices: Stockholm and Gothenburg" or "1 device started from media
without a location: Nordics standard". Check it to catch media prepared for the wrong location
before the disk is wiped.

---

## Registry values

Values are written to the applied Windows volume from the boot image, before Windows starts, so they are in
place before Autopilot and Intune policy.

| Hive | Written to |
|---|---|
| HKLM SOFTWARE | `Windows\System32\config\SOFTWARE` of the installed Windows |
| HKLM SYSTEM | `Windows\System32\config\SYSTEM` of the installed Windows |
| Default user | `Users\Default\NTUSER.DAT`, so every user profile created on the device starts with it |

- **Key** is relative to the hive, for example `Policies\Microsoft\FVE`. Do not start it with
  `HKLM\` or `HKEY_`.
- **CurrentControlSet**: an offline SYSTEM hive has no `CurrentControlSet`. A key under HKLM SYSTEM
  that starts with `CurrentControlSet\` is written to the image's current control set
  (`ControlSet001` on almost every image).
- **WOW6432Node**: there is no 32-bit redirection offline. A value meant for 32-bit software must
  name the `WOW6432Node` key explicitly, for example `WOW6432Node\Contoso\Agent`.
- Types are String, Expandable string, Multi-string (one string per line), DWORD and QWORD. DWORD
  and QWORD data accept decimal or `0x` hexadecimal.
- A profile holds at most 200 values, and each hive, key and value name combination appears once.

---

## Scripts

Scripts are Windows PowerShell 5.1 and run as SYSTEM in the order shown, within each phase. A
profile holds at most 1000 scripts of up to 24 MB each, with no total limit.

| Runs | When | Result |
|---|---|---|
| **Offline** | Right after the image is applied, in the boot image, before the device restarts. | Shown on the deployment timeline with the exit code. A failure follows the profile's failure handling. |
| **Specialize** | During Windows setup, after the restart and before OOBE and Autopilot. | Logged on the device only, in `C:\Windows\Logs\CloudImaging\SetupRunner.log`. Never changes the deployment result. |

### Locations

Each script runs either at **All locations of this profile** or at **Selected locations**, chosen
from the profile's own targeting (any location for an All devices profile). A device runs the
scripts for all locations plus the ones for the location on its USB media; media without a location
runs only the scripts for all locations. The device downloads only the scripts it runs, and the
deployment timeline lists only those.

In the scripts table, the **Locations** column shows where each script runs, and the location filter
above the table shows exactly what a device from that location runs, in run order, for example
"4 of 186 scripts run on devices from Oslo Office". **Search scripts** narrows the list by name.

Removing a location from the profile's targeting, or deleting the location, takes it off every
script. A script left with no locations shows **No locations**, never runs, and blocks Save with
""<name>" has no locations left. Choose a location or delete the script." until you choose a
location for it or delete it.

### Order

The order is the run order within each phase and always runs 1, 2, 3 without gaps. To move a
script, click its number, type the new place and press Enter (Escape cancels), or drag the row by
its handle. A focused handle also moves with the up and down arrow keys. The script is inserted at
the new place and the scripts after it move down one. With the location filter on, a move places
the script among the scripts on screen.

### Editing and uploading

The script dialog shows and edits every script, whatever its size; **Upload file** replaces the
editor's text with a file. Opening a script loads its text first ("Loading the script (12.4 MB)").
**Done** uploads the text on its own, with a progress bar, then closes the dialog; the profile's
Save then records the version. A script over 24 MB keeps Done disabled with "This script is 25.3 MB.
A script can be at most 24 MB." When an upload fails, the dialog shows "The script was not
uploaded." and **Retry**, and keeps everything you typed. **Cancel** stops an upload and changes
nothing in the profile. Uploaded text that no kept version uses is removed after a day.

### Script environment

The info icon next to **PowerShell** in the script dialog opens this table.

| | Offline | Specialize |
|---|---|---|
| When | Right after the image is applied, before the device restarts | During Windows setup, after the restart and before OOBE and Autopilot |
| Runs in | The boot image (Windows PE or Windows RE), as SYSTEM | The installed Windows, as SYSTEM, with nobody signed in |
| PowerShell | Windows PowerShell 5.1 from the boot image | Windows PowerShell 5.1 from the installed Windows |
| Installed Windows | `$env:CI_WINDOWS_DRIVE`, for example `W:` (no trailing backslash) | `$env:SystemDrive` (`C:`), `$env:WINDIR` |
| Native variables | `$env:SystemDrive`, `$env:WINDIR` and `$env:TEMP` point at the boot image (`X:`), not the installed Windows | Point at the installed Windows, as usual |
| Registry | The installed Windows' hives are not loaded. Load them with `reg load` and unload them before the script ends | Live registry of the installed Windows; HKCU is SYSTEM's |
| Network | The connection the device used to reach Cloud Imaging | Only if the network adapter's driver is in the image or was added during imaging |
| Result | Exit code 0 is success; anything else fails, including 3010 and 1641. Shown on the deployment timeline with the last output line | Exit code 0 is success. Written to `C:\Windows\Logs\CloudImaging\SetupRunner.log` only, never changes the deployment result |
| Timeout | The script and every process it started are stopped, and the next script runs | Same |
| Restarts | Blocked. `Restart-Computer`, `shutdown.exe` and `wpeutil reboot` fail inside the script | Same |
| Secrets | Everyone who can open the profile can read the script. Fetch secrets at run time instead | Same |

### Rules

- **Exit code 0 is success.** Any other exit code is a failure, including 3010 and 1641: a script
  must not ask for a restart.
- **Timeout**: 1 to 30 minutes per script, 5 by default. At the timeout the script and every process
  it started are stopped, the script is recorded as failed ("Stopped after 5 minutes."), and the
  next script runs.
- **No restarts**: scripts run without the privilege to shut down or restart the device, so
  `Restart-Computer`, `shutdown.exe` and `wpeutil reboot` fail inside the script and imaging goes
  on. If a device cannot start a script that way, the script is not run and fails with "Not run
  because restarts could not be blocked on this device." This guard protects the imaging step and
  Windows setup from an accidental restart. It is a safety net, not a security boundary: scripts
  run as SYSTEM and are trusted code.
- **No secrets**: everyone who can open the profile can read its scripts. Fetch secrets at run time
  from a service that authenticates the device instead.
- **Network during Specialize**: a Specialize script that needs the network only has it when the
  network adapter's driver is in the OS image or injected during imaging. Test such scripts on each
  hardware model.
- **Offline scripts need PowerShell**: boot images generated by the current Media Builder include
  PowerShell. On an older boot image every Offline script fails with "This boot image has no
  PowerShell, so the script cannot run. Generate a new boot image with Media Builder and update the
  USB media."

---

## Answer file

Cloud Imaging owns the answer file on every device that has profile or driver work. It writes one
`C:\Windows\Panther\unattend.xml`:

- It starts from the profile's answer file, or an empty one.
- When the device has Specialize work (Specialize scripts or driver installers), it
  adds one command to the specialize pass that starts the Cloud Imaging setup runner, ordered before
  the profile's own commands.
- An answer file already inside the OS image is overwritten without a warning.

Windows setup only reads that file on an image captured after `Sysprep /generalize`. When the
device has Specialize work and the image is not generalized, the answer file item fails with
"The OS image is not generalized, so Windows setup will not run the setup runner. Capture the image
after Sysprep /generalize."

The editor checks the file as you type and lists every problem with its line. The profile cannot be
saved, through the portal or the API, while any of these is present:

| Refused | Why |
|---|---|
| A `windowsPE` or `offlineServicing` pass | Neither pass runs, because the image is already applied. Use specialize or oobeSystem, or registry values and scripts. |
| The `Microsoft-Windows-UnattendedJoin` component | Domain join is not supported. Devices join Entra ID through Autopilot. |
| `SkipMachineOOBE` or `SkipUserOOBE` | Skipping OOBE means Autopilot never starts. |
| `AutoLogon` | It signs in a local account before Autopilot can run. |
| Plain-text passwords | Everyone who can read the profile could read them. Windows SIM's hidden passwords (`PlainText` false) are accepted, but they are encoded, not encrypted. |
| A document type declaration (`DOCTYPE`) | Not allowed in an answer file. |
| The Cloud Imaging setup runner command | Cloud Imaging adds it to each device itself. |
| More than 499 specialize commands | Cloud Imaging adds its own command. |

The file must be well-formed XML, use the `unattend` root element in the
`urn:schemas-microsoft-com:unattend` namespace, and stay within 2 MB. **Done** uploads the file like
a script, with progress and **Retry**, and shows any problem the server still finds.

---

## Failure handling

Failure handling applies to registry values and Offline scripts. Specialize scripts run after the
portal stops tracking the device, so they never change the result.

| Setting | A failed item |
|---|---|
| **Complete with warnings** (default) | Is recorded, the rest of the step runs, and the deployment completes with an amber warning marker on Prepare setup. |
| **Fail the deployment** | Stops the step. Later items are reported as not run, Configure boot does not run, and the device does not start a half-prepared Windows. Fix the profile or the OS image and retry the deployment. |

---

## Versions and history

Every save that changes the registry values, scripts, answer file or failure handling creates a new
version. Renaming the profile or changing its targeting does not.

- **History** shows two tabs. **Versions** lists every kept version with who saved it, how many
  changes it made and which version is current; expand a row to see the changed items by name.
  **Targeting** lists the profile's creation, renames and every targeting change, including
  locations removed because they were deleted.
- **View** opens a version read-only.
- **Restore** (Operators and Administrators) saves an earlier version's content as a new version.
  Targeting and the name are not changed.
- **Duplicate** opens a new profile with the current content and no targeting.
- **Retention**: each profile keeps its 25 most recent versions, plus any older version a running
  deployment still uses. Administrators change the number (5 to 100) under **Configuration** >
  **Miscellaneous** > **Setup profiles**. Older versions are deleted at the profile's next save.

---

## What the device does

**Pinning.** When a technician starts imaging, the device's profile and its current version are
recorded on the session. Edits saved after that do not change what the device receives; the next
deployment gets the new version.

**Download.** The Client downloads the pinned version, the scripts it runs and the answer file
before it formats the disk, and checks the size and SHA-256 of each. If any of them cannot be
downloaded, imaging stops before the disk is touched with "The setup profile for this device could
not be downloaded. Check the device's network connection and start imaging again." The scripts
are only checked at this point, not kept, so large scripts do not fill the Client's memory.

**Prepare setup step.** After the image is applied (and after Install drivers, when present) and
before Configure boot, the Client downloads the scripts again ("Downloading scripts (2 of 12)"), straight to
`Windows\Temp\CloudImaging\Texts` on the installed Windows, checking each one again. A script that
cannot be downloaded at this point fails with "The script could not be downloaded. Check the
device's network connection." and the profile's failure handling decides the result. Then it:

1. Writes the registry values.
2. Runs the Offline scripts ("Running Offline scripts (2 of 3)"). Each is copied to
   `Windows\Temp\CloudImaging\Scripts` on the installed Windows, run, and deleted.
3. Stages the Specialize work in `C:\Windows\Setup\CloudImaging` ("Staging Specialize work"): the
   setup runner, a manifest and the Specialize scripts. `Windows\Temp\CloudImaging` is then
   removed.
4. Writes the answer file.

The deployment timeline shows the step with the profile name and version, a summary of what was
written, run and queued, for example "2 Offline scripts succeeded" or "1 script and 5 driver
installers queued for Specialize", and every item under **Show details**. Failed items are listed
first.

**Specialize.** In the specialize pass the setup runner checks each staged file against
the manifest's SHA-256, runs the driver installers and scripts in order with their timeouts and
the restart guard, logs every item, then deletes `C:\Windows\Setup\CloudImaging`. The log stays at
`C:\Windows\Logs\CloudImaging\SetupRunner.log`, one line per item:

```text
2026-10-23T10:15:02Z  2/3  Script  "Wired 802.1X for the office LAN"  Succeeded  exit 0  12.4 s
```

The runner never restarts the device and never fails Windows setup.

**Boot image requirement.** Setup profiles need a boot image generated by the current Media Builder
with the matching Client. A device started from an older boot image images without its profile,
and the timeline shows Prepare setup as **Not run**. Generate a new boot image and update the USB
media.

# Troubleshooting index

Start with the symptom visible to the technician or administrator. The linked runbook section contains likely causes and corrective steps.

| Symptom | Start here |
|---|---|
| Deployment fails with "No available instances to satisfy this request" (`03029`) | [Deploy via Template Spec Wizard](setup-instructions.md#step-2-deploy-via-template-spec-wizard) |
| Device displays a passcode, but Portal coupling returns 404 | [Device displays passcode but coupling fails with 404](operations-runbook.md#symptom-device-displays-passcode-but-coupling-fails-with-404) |
| Portal coupling returns 409 | [Portal shows 409 when coupling](operations-runbook.md#symptom-portal-shows-409-when-coupling) |
| Device shows **Device Blocked** | [Device shows Device Blocked after registration](operations-runbook.md#symptom-device-shows-device-blocked-after-registration) |
| Imaging fails at Prepare disk with stage code `FWM` | [Disk layout and firmware mode](session-lifecycle.md#disk-layout-and-firmware-mode) |
| Client receives HTTP 401 after coupling | [Client receives HTTP 401 after coupling](operations-runbook.md#symptom-client-receives-http-401-after-coupling) |
| Image download returns HTTP 403 or its link expired | [Image download link returns 403](operations-runbook.md#symptom-image-download-link-returns-403) |
| Progress stops during download or another imaging stage | [Progress reporting stops during imaging](operations-runbook.md#symptom-progress-reporting-stops-during-imaging) |
| Waiting or imaging session remains stale in Portal | [Session remains stale in Portal](operations-runbook.md#symptom-session-remains-stale-in-portal) |
| Operation Selection is disabled with a network or service banner | [Operation Selection is disabled](operations-runbook.md#symptom-operation-selection-is-disabled-with-no-network-connection-or-a-similar-banner) |
| Imaging card says no local disk, or several disks, were found | [Imaging card reports a disk problem](operations-runbook.md#symptom-imaging-card-says-no-local-disk-detected-or-local-disks-detected) |
| Device needs Wi-Fi before registration | [Device has no Ethernet](operations-runbook.md#symptom-device-has-no-ethernet-or-needs-wi-fi-to-reach-the-device-gateway) |
| Device does not join the saved Wi-Fi network, or says the Wi-Fi password was rejected | [Device does not join the managed Wi-Fi network](operations-runbook.md#symptom-device-does-not-join-the-managed-wi-fi-network) |
| Media Builder fails USB preparation with stage code `WFI` | The location's Wi-Fi networks could not be fetched or encrypted. Check the Operator API is reachable and an active boot media certificate exists, then retry. [Rotating a Wi-Fi password](operations-runbook.md#rotating-a-wi-fi-password) |
| Session creation takes longer than five seconds | [Session creation takes more than five seconds](operations-runbook.md#symptom-session-creation-takes-more-than-5-seconds) |
| Bulk assignment times out | [Bulk assignment times out for 20 or more sessions](operations-runbook.md#symptom-bulk-assignment-times-out-for-20-or-more-sessions) |
| **Register with Autopilot** is missing or disabled on the device | [Register with Autopilot is missing or disabled](operations-runbook.md#symptom-register-with-autopilot-is-missing-or-disabled-on-the-device) |
| Autopilot hash capture fails on the device | [Hash capture fails on the device](operations-runbook.md#symptom-hash-capture-fails-on-the-device) |
| Autopilot import fails with "Microsoft Graph denied the request" | [Graph denied the request](operations-runbook.md#symptom-autopilot-import-fails-with-microsoft-graph-denied-the-request) |
| Autopilot import fails with an Intune error such as `ZtdDeviceAssignedToOtherTenant` | [Intune rejected the import](operations-runbook.md#symptom-autopilot-import-fails-with-an-intune-error-such-as-ztddeviceassignedtoothertenant) |
| A template group tag is unavailable when approving | [Template group tag unavailable](operations-runbook.md#symptom-a-template-group-tag-is-unavailable-for-a-device) |
| Imaging fails before Prepare disk with "The setup profile for this device could not be downloaded" | The device could not fetch or verify its pinned setup profile. Check the device's network connection and start imaging again. [What the device does](setup-profiles.md#what-the-device-does) |
| Imaging fails with "This setup profile needs a newer Cloud Imaging Client" | Generate a new boot image with the current Media Builder, update the USB media and start imaging again. |
| A script on the timeline failed with "The script could not be downloaded. Check the device's network connection." | The device lost its connection after the image was applied, when the scripts are downloaded again to the installed Windows. Check the network and image the device again. [What the device does](setup-profiles.md#what-the-device-does) |
| Prepare setup shows **Not run** on a finished deployment | The device's boot image predates setup profiles, so the profile was not applied. Generate a new boot image and update the USB media. [Prepare setup](session-lifecycle.md#prepare-setup) |
| Prepare setup shows warnings, or fails the deployment | Open the timeline card; failed items are listed first with their reason. See the table below and [Failure handling](setup-profiles.md#failure-handling). |
| A Specialize script or driver installer did not run or failed | Read `C:\Windows\Logs\CloudImaging\SetupRunner.log` on the device. See the table below. |
| A script dialog shows "This script is 25.3 MB. A script can be at most 24 MB." | Split the script, or move large payloads out of it and download them at run time. [Scripts](setup-profiles.md#scripts) |
| A script or answer file dialog shows "The script was not uploaded." or "The answer file was not uploaded." | The upload did not reach the portal. Check the network connection and press **Retry**; the dialog keeps what you typed. |
| Saving a profile fails with "The text of ... was not uploaded. Open the script and press Done again." | The script's text is missing on the server, for example after the dialog stayed open for more than a day. Open the script and press **Done** again, then save. |
| Save is disabled with "... has no locations left. Choose a location or delete the script." | Every location the script runs at was removed from the profile's targeting or deleted. Choose a location for the script or delete it. [Locations](setup-profiles.md#locations) |
| A device did not run a script it should have | The script runs only at selected locations that do not include the location on the device's USB media. Use the location filter on the scripts card to see what a device from that location runs. |
| Deleting a location fails with "... is the only location of setup profile ..." | Add another location to the profile or delete the profile, then try again. [How a device gets its profile](setup-profiles.md#how-a-device-gets-its-profile) |
| Deleting a location fails with "A setup profile that targets ... is being changed" | Someone is saving the profile that targets the location at the same moment. Wait for their save to finish, then delete the location again. |

When investigating session behavior, record the session ID and current state first. Use [Session lifecycle](session-lifecycle.md) to identify the expected next transition, then correlate the same session ID across Client logs and Application Insights. Log locations and starter queries are listed in the [Operations runbook](operations-runbook.md#4-log-locations).

## Setup profile item failures

The same reasons appear on the deployment timeline (Prepare setup) and in the setup runner log.

| Reason | Cause and fix |
|---|---|
| "This boot image has no PowerShell, so the script cannot run." | The boot image was generated by an older Media Builder. Generate a new boot image with Media Builder and update the USB media. |
| "Stopped after N minutes." | The script ran past its timeout and was stopped with every process it started. Fix the script or raise its timeout (at most 30 minutes). |
| "Exited with code N." | The script returned a non-zero exit code. 3010 and 1641 also fail: scripts must not ask for a restart. |
| "The default user profile was not found in the applied image." | The OS image has no `Users\Default\NTUSER.DAT`, so Default user values cannot be written. Check the OS image. |
| "The OS image is not generalized, so Windows setup will not run the setup runner." | The image was captured without `Sysprep /generalize`. Capture the image after Sysprep /generalize. |
| "File does not match the manifest" (setup runner log) | A staged script or installer changed or went missing between the imaging step and Specialize, so the runner skipped it. Re-image the device. |
| "Not run because restarts could not be blocked on this device." | The device could not start the item without the right to restart, so it was not started: a restart inside imaging or Windows setup would break the deployment. Report it with the device's logs. |
| "Not run because an earlier item failed and this profile fails the deployment." | Fail the deployment stopped the step at the first failure. Fix that item and retry. |

`SetupRunner.log` has one line per item: time (UTC), position, kind, quoted name, result, exit code and duration, separated by two spaces. The first and last lines name the profile and its version, or "No setup profile" for a device with driver installers only:

```text
2026-10-23T10:15:02Z  2/3  Script  "Wired 802.1X for the office LAN"  Succeeded  exit 0  12.4 s
```
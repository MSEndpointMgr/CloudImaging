# Cloud Imaging Operations Runbook

**Audience**: IT administrators and operations engineers running Cloud Imaging in production

---

## 1. Session, Token, and Download Link Troubleshooting

### Symptom: Device displays passcode but coupling fails with 404

**Cause**: Session left `SessionAllowed`, or it expired after 60 minutes without a heartbeat.

**Resolution**:
1. A passcode stays valid for as long as its session does. The device heartbeats every 30 seconds
   while it waits on the "Awaiting Session Coupling" screen, so a powered-on device holds its
   passcode open indefinitely. Expiry only follows 60 minutes of silence, which in practice means
   the device was shut down, rebooted or lost its network.
2. Use **Start Over** on the device's "Awaiting Session Coupling" screen to request a new passcode
   without rebooting. Restarting the Cloud Imaging Client has the same effect.

---

### Symptom: Portal shows 409 when coupling

**Cause**: Passcode already consumed; session was previously coupled.

**Resolution**: Check session state in Table Storage (`DeviceSessions` table, `active` partition). If state is `SessionAssigned` the session is already coupled. Assign an image or restart the device to begin a new session.

---

### Symptom: Device shows Device Blocked after registration

**Cause**: Device pre-flight authorization is enabled and the device failed at least one selected
requirement. The device screen and **Devices › Blocked** show which check failed and what the
device reported.

**Resolution**:
1. Open **View Log** on the device. Each failed check has a log line with its fix, for example
   `Pre-flight check failed: Secure Boot: Not enabled. Enable Secure Boot in firmware settings.`
2. **Autopilot presence**: confirm the serial number shown by the Client matches the device record in
   Autopilot or Corporate Identifiers, and that the Imaging Core managed identity has the Microsoft
   Graph permission (rerun `post-install.ps1`). Application Insights traces for the session ID
   distinguish a genuine no-match from a Graph request failure.
3. **Firmware mode, Secure Boot, TPM version**: change the setting in the device firmware.
4. **Not reported**: the boot media predates posture reporting. Rebuild it with the current Client.
5. **Could not be detected**: the Client log has the Win32 error from reading the value.
6. If the device must be imaged as it is, an Administrator can approve it under **Devices › Blocked**.
   The approval lets its next session within 7 days skip the checks that failed.

`SessionNotAuthorized` is terminal. After fixing the device or approving it, select **Try again** on
the device (or restart it) to register a new session; the blocked session cannot be resumed.

---

### Symptom: Client receives HTTP 401 after coupling

**Cause**: Device-session token expired or mTLS certificate mismatch.

**Resolution**:
1. **Token expiry**: Restart the device; the Client re-registers and gets a new token.
2. **mTLS mismatch**: The boot media certificate has been rotated since the USB was prepared. Regenerate the boot image (Media Builder → Generate Boot Image) and re-prepare the USB drive.

---

### Symptom: Image download link returns 403

**Cause**: The shared access signature (SAS) on the download link expired before the download completed.

**Resolution**: The refresh coordinator renews the link automatically whenever it is within 15
minutes of expiring. If 403s persist:
1. In the portal, raise **Configuration → OS image download link expiry (minutes)** above its
   240-minute (4-hour) default. Devices on slow links downloading very large images are the
   usual reason to need more.
2. Verify the Device Gateway API can reach the Imaging Core API over Private Link (check the
   network security group rules on the Function App subnets).

---

### Symptom: Progress reporting stops during imaging

**Cause**: Network interruption or inactivity timeout.

**Resolution**:
1. Record the session ID and the last visible stage: Prepare disk, Download image, Apply Windows
   image, Configure boot, or Configure recovery.
2. Verify network connectivity from the device to the Device Gateway API.
3. Correlate the session ID in Device Gateway and Imaging Core Application Insights traces. A
   successful Client request should reach both APIs and return HTTP 204.
4. Review the Client log for the last `Progress reported` entry and any following HTTP error.
5. If the session has become terminal, restart the Client to create a new session; terminal
   sessions cannot be resumed.

Sessions are expired by a timer that runs every 5 minutes, using two different thresholds: a
session that is **actively imaging** is given **2 hours**
since its last heartbeat, while a session sitting idle at any other stage is given **30 minutes**.
A long-running download is therefore not at risk of being expired at 30 minutes.

---

### Symptom: Session remains stale in Portal

**Cause**: The Client stopped sending status polls and progress reports, or the Imaging Core
lifecycle timer is not running successfully.

**Resolution**:
1. Check the session's state and `LastHeartbeatAt` value in the `DeviceSessions` table.
2. Apply the expected threshold: `SessionInit`, `SessionAllowed`, and `SessionAssigned` use 30
   minutes; `SessionStarted` and `SessionInProgress` use 2 hours.
3. Allow for the lifecycle timer's five-minute schedule after the threshold has elapsed.
4. If the session still does not transition, inspect Imaging Core Application Insights for the
   `SessionLifecycleTimer` execution and storage-access failures.
5. Verify the Imaging Core Function App has `AzureWebJobsStorage__credential=managedidentity` and
   `AzureWebJobsStorage__clientId` set to its user-assigned managed identity client ID, then verify
   that identity has the required host-storage role assignments.

Never-coupled sessions become `SessionExpired`. Coupled sessions become `SessionFailed`, preserving
the interruption as a diagnosable outcome. See [Session lifecycle](session-lifecycle.md#heartbeats-and-timeouts).

---

### Symptom: Operation Selection is disabled with "No network connection" or a similar banner

**Cause**: When the Client starts, the **Getting ready** screen checks for a local disk, a network
connection and the Device Gateway API. Every operation needs the Device Gateway, so if it cannot be
used, Operation Selection opens with every option disabled and a banner naming the reason:

| Banner | Meaning |
|---|---|
| No network connection | No adapter got a routable address and the gateway could not be reached. Either Ethernet is not connected, or the boot image has no driver for this device's network adapter. On a Windows RE image the text says whether no saved Wi-Fi network was in range, or no Wi-Fi adapter was found. |
| Wi-Fi password rejected | Windows RE only: a saved network was in range but refused its password, usually because it changed after the USB drive was prepared. |
| Cloud Imaging service unreachable | The device is online, but DNS, routing, a proxy or a firewall stops it reaching the Device Gateway. |
| Cloud Imaging service error | The Device Gateway host answered with a server error. |
| Boot media certificate rejected | The Device Gateway rejected the boot-media certificate. |
| Service URL missing | The boot image has no Device Gateway URL. |

**Resolution**: Fix the cause, then select **Check again** to repeat the checks without restarting.
On a Windows RE image with no network, the banner offers **Connect to Wi-Fi** instead, which rechecks
by itself after a successful connect.
For a missing network driver, add the driver package under **Drivers** in Media Builder → Generate
Boot Image, regenerate the boot image and prepare the USB drive again. The Client log (**View Log**)
records each check's outcome on a `Startup checks:` line.

---

### Symptom: Imaging card says "No local disk detected" or "local disks detected"

**Cause**: Imaging formats exactly one non-USB disk. No disk usually means the boot image has no
driver for the storage controller (common with Intel VMD/RST NVMe). Several disks are refused
because the Client will not guess which one to wipe.

**Resolution**: For no disk, add the storage driver under **Drivers** in Media Builder and
regenerate the boot image, or switch the controller from RAID/VMD to AHCI in firmware. For several
disks, remove or disable the extra disks. **Register with Autopilot** stays available either way,
since it never touches the disk.

---

### Symptom: Device has no Ethernet or needs Wi-Fi to reach the Device Gateway

**Cause**: Windows PE boot images support Ethernet only. They have no WLAN service, so the
**Connect to Wi-Fi** button is not shown. Wi-Fi needs a boot image built on **Windows RE**.

**Resolution**: Use Ethernet (a USB Ethernet adapter works if its driver is in the boot image), or
build a Windows RE boot image (Media Builder → Generate Boot Image → **Boot Image Base**), add the
Wi-Fi adapter driver, and add the site's network under **Wi-Fi Networks** in the portal. See
[setup-instructions.md](setup-instructions.md#client-support-tools).

---

### Symptom: Device does not join the managed Wi-Fi network

The startup screen's network line says what happened.

| Startup text | Resolution |
|---|---|
| No Wi-Fi networks are saved for this location | The USB drive carries no networks for its location. Check the network's locations under **Wi-Fi Networks**, then prepare the USB drive again or connect to Ethernet once so the Client fetches the current list. |
| None of the saved networks are in range | The network is out of range, or its SSID differs from the portal. Hidden networks are always tried. |
| *SSID* rejected the saved password | The password changed after the USB drive was prepared. Update it under **Wi-Fi Networks**, then connect to Ethernet once (the Client then rewrites the USB copy), or prepare the USB drive again. A password typed in **Connect to Wi-Fi** works for that session only. |
| No Wi-Fi adapter | Add the adapter's driver under **Drivers** and regenerate the boot image. |
| *SSID* gave no IP address | The network accepted the device but DHCP did not answer. Check the network's DHCP scope and client isolation. |

The Client log records each attempt; passwords are never logged.

### Rotating a Wi-Fi password

1. Under **Wi-Fi Networks**, edit the network, select **Replace** next to the password, and save.
2. USB drives that come online (Ethernet, or another managed network) fetch the new password at
   startup and rewrite their copy.
3. Prepare the USB drive again for drives that never get another network.
4. Rotating the boot media certificate also means preparing every USB drive again, which writes a
   fresh Wi-Fi file encrypted to the new certificate.

---

### Symptom: Need an interactive shell on the device for advanced troubleshooting

**Cause**: The **Command Prompt** button on the Operation Selection screen only appears when the
boot image was built with **Enable command prompt access** checked in Media Builder (off by
default, per boot image).

**Resolution**: Regenerate the boot image with that checkbox enabled (Media Builder → Generate
Boot Image → Support Tools) and re-prepare the USB drive. See
[setup-instructions.md](setup-instructions.md#client-support-tools) for the security
considerations before enabling it broadly.

---

### Symptom: Register with Autopilot is missing or disabled on the device

**Cause**: The card only appears when the boot image was built with **Include Autopilot hardware
hash tooling**. It is shown but disabled with "Turned off in the Cloud Imaging portal" when the
feature is off.

**Resolution**: Turn on **Allow Autopilot registration** under Configuration → Autopilot, or
regenerate the boot image with the tooling included. See
[Autopilot registration](setup-instructions.md#optional-autopilot-registration).

---

### Symptom: Hash capture fails on the device

**Cause**: OA3Tool could not read the hardware or the TPM. The Client shows OA3Tool's exit code and
output. Common causes are a disabled or uninitialised TPM, and virtual machines without a virtual
TPM.

**Resolution**: Enable the TPM in firmware and retry. A device without a TPM can still be
registered, but the hash lacks TPM data, which self-deploying and pre-provisioning modes need.

---

### Symptom: Autopilot import fails with "Microsoft Graph denied the request"

**Cause**: The Imaging Core API's managed identity lacks
`DeviceManagementServiceConfig.ReadWrite.All`.

**Resolution**: Rerun `post-install.ps1` with `-EnableAutopilotRegistration`, wait several minutes
for replication, then click **Retry import** on the request.

---

### Symptom: Autopilot import fails with an Intune error such as `ZtdDeviceAssignedToOtherTenant`

**Cause**: Intune rejected the import and the request shows its error. `ZtdDeviceAssignedToOtherTenant`
means another tenant, or an OEM or reseller on its behalf, already registered the device. Other
errors, such as a malformed hash or an invalid group tag, are shown the same way. A device already
registered in this tenant (`ZtdDeviceAlreadyAssigned`) is not a failure: the request is recorded as
**Already registered**.

**Resolution**: For devices assigned to another tenant, ask the previous owner or the reseller to
deregister the device. For other errors, correct the cause and click **Retry import**, or reject
the request and have the technician submit the device again. Requests that are not decided expire
after the configured number of days.

---

## 2. Certificate Management

### Rotating the Boot Media Certificate

```
Portal → Configuration → Certificates → Rotate…
```

⚠️ **There is no way to stage a certificate in advance.** Both **Generate/Regenerate** and
**Rotate** issue the new certificate *and activate it immediately*, retiring the previous one in
the same operation. The moment either completes, every USB drive built with the old certificate
stops authenticating against the Device Gateway API. Plan for that, rather than expecting to
prepare new media ahead of the switch.

**Rotation procedure**:
1. Schedule a maintenance window. Imaging cannot run between the rotation and the point where
   new media is in technicians' hands.
2. Confirm you have a technician workstation ready with Media Builder and the Windows ADK, so
   new boot images can be built immediately after rotating.
3. Rotate the certificate in the portal and confirm the impact dialog.
4. Regenerate boot images in Media Builder; they pick up the newly activated certificate
   automatically.
5. Test the new media on at least one device before wider distribution.
6. Re-prepare and redistribute USB media to every imaging station.

If you need to validate a rotation without disrupting production, do it in a separate
(for example `dev`) deployment first; a single deployment only ever has one active certificate.

---

### Certificate Expiry Monitoring

The portal is the authoritative source: **Configuration → Certificates** shows the active
certificate's expiry date and raises a warning banner as it approaches, and again once expired.
Check it as part of routine operations.

Do **not** rely on Key Vault's built-in certificate near-expiry events for this. The boot media
certificate is stored as a Key Vault *secret* (a base64 PKCS#12 blob), not as a Key Vault managed
certificate object, so those events never fire for it. If you want an external alert, build it
from the expiry date reported by the portal rather than from Key Vault.

The certificate's validity period is fixed when it is issued, from the **Boot Media Certificate
Validity (days)** deployment parameter (default 365).

---

## 3. Performance Issues

### Symptom: Session creation takes more than 5 seconds

**Resolution**:
1. Check Device Gateway API Function App plan: must be Premium EP1 or higher for VNet integration.
2. Check Private Link connectivity between Device Gateway and Imaging Core.
3. Review Application Insights for dependency failures.

### Symptom: Bulk assignment times out for 20 or more sessions

**Resolution**:
1. `BulkAssignmentService` processes sessions sequentially; large batches take proportionally longer.
2. For fleets > 50 devices, stagger bulk assignments into groups of 20.

---

## 4. Log Locations

| Component | Log location |
|---|---|
| Device Gateway API | Application Insights → Traces |
| Operator API | Application Insights → Traces |
| Imaging Core API | Application Insights → Traces |
| Portal backend | Application Insights → Traces |
| Cloud Imaging Client | `%LOCALAPPDATA%\CloudImaging\Client\Logs\cloud-imaging-client-*.log` |
| Cloud Imaging Media Builder | `%LOCALAPPDATA%\CloudImaging\MediaBuilder\Logs\cloud-imaging-mediabuilder-*.log` |

---

## 5. Azure Resource Health

### Table Storage Partitions

| Table | Partitions | Description |
|---|---|---|
| `DeviceSessions` | `active`, `terminal` | Sessions move from `active` to `terminal` on completion; `terminal` rows are purged after 24 hours by the lifecycle timer |
| `OSImages` | `catalog` | All uploaded OS image metadata |
| `BootImages` | `catalog` | Boot image entries, max 5 active per architecture |
| `BootMediaCertificate` | `cert` | Exactly one row with `IsActive=true` |
| `BrandingConfiguration` | `branding` | Single row |
| `PortalConfiguration` | `config` | Single row |

### Diagnostic Queries (Application Insights)

```kql
// Failed sessions in the last 24 hours
customEvents
| where timestamp > ago(24h)
| where name == "SessionFailed"
| project timestamp, sessionId=tostring(customDimensions.SessionId)
| order by timestamp desc

// mTLS rejections (cert mismatch)
traces
| where timestamp > ago(1h)
| where message contains "mTLS"
| project timestamp, message
```

---

## 6. Disaster Recovery

### Recovery Point Objective (RPO)

Azure Table Storage is replicated according to the storage account's redundancy setting. For
production, use geo-redundant storage (GRS) rather than locally redundant (LRS) or
zone-redundant (ZRS), so session and catalog state survives the loss of a region.

### Recovery Time Objective (RTO)

All components are stateless (state in Table Storage + Blob Storage + Key Vault). Re-deploy from latest release bundle with `upgrade.ps1`, about 15 minutes. See [upgrade-instructions.md](upgrade-instructions.md).

### Key Vault Backup

```powershell
# Back up the boot media certificate (stored as a Key Vault secret holding a base64 PKCS#12 blob)
# Key Vault name follows the naming convention {prefix}-{env}-kv (e.g. corp-prod-kv)
$secret = Get-AzKeyVaultSecret -VaultName <prefix>-<env>-kv -Name boot-media-cert-<thumbprint>
$secret | ConvertTo-Json | Out-File cert-backup.json
```

---

## 7. Escalation

- **GitHub Issues**: [github.com/MSEndpointMgr/CloudImaging/issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
- **Community Discord**: MSEndpointMgr community channels
- **Security vulnerabilities**: Report privately via GitHub Security Advisories

# Cloud Imaging Operations Runbook

**Audience**: IT administrators and operations engineers running Cloud Imaging in production

---

## 1. Session, Token, and Download Link Troubleshooting

### Symptom: Device displays passcode but coupling fails with 404

**Cause**: Passcode expired or session transitioned out of `SessionAllowed` state.

**Resolution**:
1. Restart the Cloud Imaging Client on the device: this creates a new session with a fresh passcode.
2. Couple within 30 minutes of the passcode appearing (the default lifetime).
3. If the problem recurs frequently, raise **Session Passcode TTL (minutes)**. This is a
   *deployment* parameter, not a portal setting: it's set in the deployment wizard and stored as
   the `Security__PasscodeTtlMinutes` application setting on the Imaging Core API Function App,
   so changing it means either redeploying or editing that application setting directly.

---

### Symptom: Portal shows 409 when coupling

**Cause**: Passcode already consumed; session was previously coupled.

**Resolution**: Check session state in Table Storage (`DeviceSessions` table, `active` partition). If state is `SessionAssigned` the session is already coupled. Assign an image or restart the device to begin a new session.

---

### Symptom: Device shows Not Authorized after registration

**Cause**: Device pre-flight authorization is enabled and Imaging Core did not find the device in
Windows Autopilot or Intune Corporate Identifiers through Microsoft Graph.

**Resolution**:
1. Confirm whether **Configuration → Device pre-flight authorization** is enabled.
2. Confirm the serial number shown by the Client matches the device record in Autopilot or
   Corporate Identifiers.
3. Confirm the Imaging Core managed identity has the required Microsoft Graph application
   permission by rerunning `grant-graph-permissions.ps1` from the deployment package.
4. Review Imaging Core Application Insights traces for the session ID to distinguish a genuine
   no-match result from a Microsoft Graph request failure.

`SessionNotAuthorized` is terminal. After correcting the device record or permission, restart the
Client to register a new session; the denied session cannot be resumed.

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

### Symptom: Device has no wired network or needs Wi-Fi to reach the Device Gateway

**Resolution**: On the Operation Selection screen, click **Connect to Wi-Fi** (always available,
no opt-in required) to scan and connect to an Open or WPA2/WPA3-Personal network via `netsh wlan`.
Enterprise/802.1X networks are listed but cannot be connected to via this flow. No credential is
persisted; see [setup-instructions.md](setup-instructions.md#client-support-tools).

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
| `BootImages` | `catalog` | Boot image entries, max 5 active |
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

All components are stateless (state in Table Storage + Blob Storage + Key Vault). Re-deploy from latest release archive with `update.ps1`, about 15 minutes. See [upgrade-instructions.md](upgrade-instructions.md).

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

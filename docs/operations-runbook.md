# Cloud Imaging Operations Runbook

**Spec reference**: T109  
**Audience**: IT administrators and SREs operating Cloud Imaging in production

---

## 1. Session / Token / SAS Troubleshooting

### Symptom: Device displays passcode but coupling fails with 404

**Cause**: Passcode expired or session transitioned out of `SessionAllowed` state.

**Resolution**:
1. Restart the Cloud Imaging Client on the device — this creates a new session with a fresh passcode.
2. Couple within 10 minutes of the passcode appearing (default TTL).
3. If the problem recurs frequently, check `PortalConfiguration.passcodeTtlMinutes` (default 10).

---

### Symptom: Portal shows 409 when coupling

**Cause**: Passcode already consumed — session was previously coupled.

**Resolution**: Check session state in Table Storage (`DeviceSessions` table, `active` partition). If state is `SessionAssigned` the session is already coupled. Assign an image or restart the device to begin a new session.

---

### Symptom: Client receives HTTP 401 after coupling

**Cause**: Device-session token expired or mTLS certificate mismatch.

**Resolution**:
1. **Token expiry**: Restart the device — the Client re-registers and gets a new token.
2. **mTLS mismatch**: The boot media certificate has been rotated since the USB was prepared. Regenerate the boot image (Media Builder → Generate Boot Image) and re-prepare the USB drive.

---

### Symptom: SAS download URL returns 403

**Cause**: SAS token expired before the download completed.

**Resolution**: The SAS refresh coordinator should handle this automatically. If it persists:
1. Check `PortalConfiguration.sasTokenUrlExpiryMinutes` — increase from default 60 to 240 if downloads take longer.
2. Verify the Device Gateway API can reach the Imaging Core API over Private Link (check NSG rules).

---

### Symptom: Progress reporting stops mid-download

**Cause**: Network interruption or inactivity timeout.

**Resolution**:
1. The session lifecycle timer expires inactive sessions after 30 minutes.
2. Verify network connectivity on the device.
3. Restart the device to create a new session.

---

## 2. Certificate Management

### Rotating the Boot Media Certificate

```
Portal → Configuration → Boot Media Certificate → Rotate Certificate
```

⚠️ **Warning**: All boot media using the current certificate will stop authenticating immediately. Regenerate the boot image and redistribute USB drives **before** rotating in production.

**Safe rotation procedure**:
1. Generate a new certificate (Portal → Configuration → Generate Certificate) — this does NOT activate it yet.
2. Regenerate boot images with the new cert embedded (Media Builder).
3. Test the new USB media on at least one device.
4. Perform the rotation during a maintenance window.
5. Redistribute new USB media to all imaging stations.

---

### Certificate Expiry Monitoring

Check the **Boot Media Certificate** panel in Portal → Configuration for `expiresAt`. Set up an Azure Monitor alert on Key Vault certificate expiry (90 days before expiry recommended).

---

## 3. Performance Issues

### Symptom: Session creation takes > 5 s

**Resolution**:
1. Check Device Gateway API Function App plan — must be Premium EP1 or higher for VNet integration.
2. Check Private Link connectivity between Device Gateway and Imaging Core.
3. Review Application Insights for dependency failures.

### Symptom: Bulk assignment times out for 20+ sessions

**Resolution**:
1. `BulkAssignmentService` processes sessions sequentially — large batches take proportionally longer.
2. For fleets > 50 devices, stagger bulk assignments into groups of 20.

---

## 4. Log Locations

| Component | Log location |
|---|---|
| Device Gateway API | Application Insights → Traces (APPLICATIONINSIGHTS_CONNECTION_STRING) |
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
| `DeviceSessions` | `active`, `terminal` | Active sessions moved to `terminal` on completion |
| `DeviceSessions` | `terminal` | Purged after 24 hours by lifecycle timer |
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

Azure Table Storage has built-in geo-redundancy with LRS/ZRS/GRS options. Set Storage Account replication to GRS for production.

### Recovery Time Objective (RTO)

All components are stateless (state in Table Storage + Blob Storage + Key Vault). Re-deploy from latest release archive with `update.ps1` — estimated 15 minutes.

### Key Vault Backup

```powershell
# Back up the boot media certificate PFX
# Key Vault name follows the naming convention {prefix}-{env}-kv (e.g. corp-prod-kv)
$secret = Get-AzKeyVaultSecret -VaultName <prefix>-<env>-kv -Name boot-media-cert-<thumbprint>
$secret | ConvertTo-Json | Out-File cert-backup.json
```

---

## 7. Escalation

- **GitHub Issues**: [github.com/MSEndpointMgr/CloudImaging/issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
- **Community Discord**: MSEndpointMgr community channels
- **Security vulnerabilities**: Report privately via GitHub Security Advisories

# Data Model: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging  
**Date**: 2026-06-16  
**Storage Baseline**: Azure Table Storage (metadata/state) + Azure Blob Storage (artifacts)

## Entity: DeviceSession

Represents one imaging workflow instance for one device.

**Suggested table**: `DeviceSessions`
- `PartitionKey`: state bucket or date bucket (implementation choice)
- `RowKey`: `sessionId` (UUID)

| Field | Type | Description |
|------|------|-------------|
| sessionId | string (UUID) | Unique identifier for session. |
| passcodeHash | string | Hash of one-time pairing passcode; passcode plaintext is never persisted. |
| passcodeExpiresAt | datetime | Expiry for one-time passcode. |
| passcodeConsumedAt | datetime? | Timestamp when passcode was successfully used for coupling. |
| deviceSessionTokenRef | string | Reference to issued device-session token material/claims id. |
| status | enum | `SessionInit`, `SessionAllowed`, `SessionAssigned`, `SessionStarted`, `SessionInProgress`, `SessionCompleted`, `SessionFailed`, `SessionNotAuthorized` (terminal). |
| assignedImageId | string? | OS image assignment reference. |
| currentSasTokenUrl | string? | Current SAS token URL issued for the assigned image. |
| currentSasTokenUrlExpiresAt | datetime? | Expiry for current SAS token URL. |
| lastHeartbeatAt | datetime? | Last client polling/progress heartbeat timestamp. |
| currentStepName | string? | Last known imaging step name. |
| overallProgressPercent | int? | Session-level imaging completion percentage for portal display. |
| errorDetail | string? | Terminal or current error detail. |
| createdAt | datetime | Session creation timestamp. |
| terminalAt | datetime? | Timestamp when session became completed/failed. |
| purgeAt | datetime? | Retention boundary for terminal state purge. |
| deviceInfoJson | string? | Optional model/serial/mac payload. |

### Lifecycle Rules

1. `SessionAssigned -> SessionStarted` is automatic on next poll when assignment details are requested.
2. Pre-imaging states expire after 30 minutes of inactivity.
3. In-progress states fail after heartbeat timeout policy (> 4 hours without heartbeat).
4. Terminal states are purged after 24 hours.
5. Pairing passcode has its own configurable TTL (deployment parameter; default: 30 minutes) independent of the session inactivity timeout; invalidated on TTL expiry or successful coupling, whichever occurs first. Passcode uniqueness is scoped to currently active (non-terminal, non-expired) sessions only; passcodes from terminal or expired sessions are considered released and MAY be reused in new sessions.

## Entity: ImagingStep

Tracks per-step execution progress for a device session.

**Suggested table**: `ImagingSteps`
- `PartitionKey`: `sessionId`
- `RowKey`: step identifier

| Field | Type | Description |
|------|------|-------------|
| sessionId | string (UUID) | Parent session id. |
| stepName | string | Step key (`DownloadStarted`, `ApplyStarted`, etc.). |
| status | enum | `Pending`, `InProgress`, `Completed`, `Failed`. |
| startedAt | datetime? | Step start timestamp. |
| completedAt | datetime? | Step completion timestamp. |
| errorDetail | string? | Step-level failure detail. |
| progressPercent | int? | Optional per-step progress percentage. |

## Entity: OSImage

Catalog metadata for Windows deployment images.

**Suggested table**: `OSImages`
- `PartitionKey`: `catalog`
- `RowKey`: `imageId`

| Field | Type | Description |
|------|------|-------------|
| imageId | string (UUID) | Unique image identifier. |
| name | string | Display name. |
| version | string | Version label. |
| description | string | Admin description. |
| blobContainerName | string | Storage container. |
| blobName | string | Blob path/name. |
| fileSizeBytes | long | Artifact size. |
| isActive | bool | Assignment visibility flag. |
| activeSessionCount | int | Active usage guard for deletion policy. |
| uploadedAt | datetime | Upload timestamp. |
| uploadedBy | string | Operator identity reference. |

## Entity: BootImage

Catalog metadata for generated WinPE+Client boot artifacts.

**Suggested table**: `BootImages`
- `PartitionKey`: `catalog`
- `RowKey`: `bootImageId`

| Field | Type | Description |
|------|------|-------------|
| bootImageId | string (UUID) | Unique boot image identifier. |
| version | string | Boot image version label. |
| manifestVersion | string | Embedded manifest schema version. |
| blobContainerName | string | Storage container. |
| blobName | string | Blob path/name. |
| fileSizeBytes | long | Artifact size. |
| checksum | string | Integrity checksum. |
| isActive | bool | Availability flag. |
| createdAt | datetime | Artifact creation timestamp. |
| createdBy | string | Operator identity reference. |
| notes | string? | Optional metadata notes. |

## Entity: PortalConfiguration

Deployment-wide portal configuration settings. Singleton per deployment.

**Suggested table**: `Configuration`
- `PartitionKey`: `portalconfig`
- `RowKey`: `default`

| Field | Type | Description |
|------|------|-------------|
| devicePreFlightAuthorizationEnabled | bool | Global on/off toggle for device pre-flight authorization check. Default: `true`. |
| sasTokenUrlExpiryMinutes | int | Expiry window in minutes for newly issued OS image SAS token URLs. Default: `240` (4 hours). Changes take immediate effect for newly issued tokens; existing tokens are not retroactively affected. Modifiable by `CloudImagingAdministrator` role from Portal deployment configuration section. |
| lastModifiedAt | datetime | Timestamp of last configuration change. |

## Entity: BrandingConfiguration

Runtime UI branding configuration.

**Suggested table**: `Configuration`
- `PartitionKey`: `branding`
- `RowKey`: `default`

| Field | Type | Description |
|------|------|-------------|
| applicationName | string | App name shown in UI. |
| logoUrl | string? | Logo blob URL. |
| primaryColorHsl | string | Primary color token. |
| accentColorHsl | string | Accent color token. |
| updatedAt | datetime | Last update timestamp. |
| updatedBy | string | Operator identity reference. |

## Value Object: SupportReferenceCode (Transient)

Structured error code produced at failure events in Cloud Imaging Client and Cloud Imaging Media Builder.

| Field | Type | Description |
|------|------|-------------|
| componentCode | string | 3-letter prefix: `CIC` (Cloud Imaging Client) or `CMB` (Cloud Imaging Media Builder). |
| sessionRef | string | First 8 characters of the associated session ID for Client operations; short operation-stage identifier for standalone Media Builder operations without session context. |
| stageCode | string | Abbreviated step identifier. Client codes: `REG` (session-registration), `FMT` (format-disk), `DWN` (download-image), `APL` (apply-image). Media Builder codes: `DVI` (disk-validation), `PRT` (partitioning), `BID` (boot-image-download), `BCF` (boot-config). |
| epochSeconds | long | Unix epoch at time of error (seconds since 1970-01-01 UTC). |

Rendered format: `{componentCode}-{sessionRef}-{stageCode}-{epochSeconds}`  
Example: `CIC-A1B2C3D4-DWN-1750000000`

## Value Object: SASTokenUrl (Transient)

Returned to clients and not persisted as a standalone table row.

| Field | Type | Description |
|------|------|-------------|
| downloadUrl | string | Pre-signed, time-limited SAS token URL for blob download. |
| expiresAt | datetime | SAS token URL expiry. |
| targetArtifactId | string | Related `imageId` or `bootImageId`. |
| sessionId | string? | Session context when applicable. |

## Value Object: BootImageManifest

Manifest embedded in generated boot image payloads.

| Field | Type | Description |
|------|------|-------------|
| manifestVersion | string | Manifest schema version. |
| imageVersion | string | Boot image version. |
| createdAt | datetime | Manifest creation timestamp. |
| winPeVersion | string | WinPE baseline version. |
| clientVersion | string | Cloud Imaging Client version. |
| componentChecksums | object | Integrity checksums per component. |
| deploymentMetadata | object | Additional deployment metadata. |

## Value Object: UsbPreparationManifest

Manifest written to prepared USB media.

| Field | Type | Description |
|------|------|-------------|
| manifestVersion | string | Manifest schema version. |
| preparedAt | datetime | Preparation timestamp. |
| toolVersion | string | Media Builder app version. |
| bootImageVersion | string | Deployed boot image version. |
| selectedDiskId | string | Target disk identifier. |
| partitionSchema | object | Two-partition layout details. |
| validationResults | object | Disk/operation validation output. |
| autoStartConfigured | bool | WinPE auto-start configuration flag. |

## Relationship Summary

- `DeviceSession` 1..* `ImagingStep`
- `DeviceSession` *..1 `OSImage`
- `BootImage` is managed separately from `OSImage`
- `BrandingConfiguration` is singleton configuration

## Contract Alignment

The model aligns to these contracts:
- `contracts/device-gateway-api.md`
- `contracts/operator-api.md`
- `contracts/imaging-core-api.md`
- `contracts/cloud-imaging-portal-api.md`

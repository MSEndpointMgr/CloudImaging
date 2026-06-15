# Data Model: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging
**Date**: 2026-06-14
**Storage**: Azure Table Storage (entities) + Azure Blob Storage (files)

---

## Entities

### DeviceSession

Represents a single imaging session for one bare-metal device, from initial
WPF client registration through imaging completion.

**Table Storage**: table `DeviceSessions`
- PartitionKey: `status` (enables efficient list-by-status queries)
- RowKey: `sessionId` (UUID, globally unique)

| Field | Type | Notes |
|-------|------|-------|
| sessionId | string (UUID) | Primary identifier. RowKey. |
| passcode | string (6 chars, alphanumeric) | Displayed on WPF client; entered by technician in portal to couple session. Uppercase only, excludes ambiguous chars (0/O, 1/I/L). |
| status | enum | `Waiting`, `Coupled`, `Imaging`, `Completed`, `Failed` |
| assignedImageId | string (UUID) or null | Set when technician assigns an OS image. References OSImageMetadata.imageId. |
| sessionToken | string | Signed JWT issued by SessionBroker to the WPF client. Not returned to the Admin Portal. |
| sessionTokenExpiry | DateTime (UTC) | When the session token expires (default: 8 hours from creation). |
| createdAt | DateTime (UTC) | When WPF client registered the session. |
| coupledAt | DateTime (UTC) or null | When technician coupled the session via passcode. |
| imagingStartedAt | DateTime (UTC) or null | When the WPF client began downloading the image. |
| completedAt | DateTime (UTC) or null | When imaging completed (success or failure). |
| lastProgressAt | DateTime (UTC) or null | Timestamp of most recent ImagingStep update. |
| errorDetail | string or null | Set on failure; included in portal display and WPF error screen. |
| deviceInfo | string (JSON) | Optional hardware info reported by WPF client at startup (model, serial, MAC). |

**State transitions**:
```
[WPF registers] --> Waiting
[Technician enters passcode + assigns image] --> Coupled
[WPF begins download] --> Imaging
[Imaging pipeline completes] --> Completed
[Any unrecoverable error] --> Failed
```

**Passcode uniqueness**: Passcode MUST be unique among all sessions in
`Waiting` or `Coupled` status. SessionHandler enforces this at creation time
with a conditional write. Passcode is freed when a session reaches `Completed`
or `Failed`.

---

### ImagingStep

Represents one discrete step in the imaging pipeline for a device session.
Used to drive the real-time per-step progress display in both the WPF client
and the Admin Portal.

**Table Storage**: table `ImagingSteps`
- PartitionKey: `sessionId`
- RowKey: `stepName`

| Field | Type | Notes |
|-------|------|-------|
| sessionId | string (UUID) | Foreign key to DeviceSession. PartitionKey. |
| stepName | string | RowKey. One of the standard step names listed below. |
| status | enum | `Pending`, `InProgress`, `Completed`, `Failed` |
| startedAt | DateTime (UTC) or null | When the WPF client reported this step as started. |
| completedAt | DateTime (UTC) or null | When the WPF client reported this step as finished. |
| errorDetail | string or null | Error message if status is Failed. |

**Standard step names** (in order):
1. `SessionRegistered`
2. `SessionCoupled`
3. `CredentialIssued`
4. `DownloadStarted`
5. `DownloadCompleted`
6. `ApplyStarted`
7. `ApplyCompleted`
8. `Finalizing`
9. `Complete`

WPF client reports steps sequentially. Admin Portal shows steps as a progress
timeline per device.

---

### OSImageMetadata

Describes a Windows OS image file stored in Blob Storage. Used to build the
assignment catalog in the Admin Portal and to resolve the blob path when
issuing a SAS token.

**Table Storage**: table `OSImages`
- PartitionKey: `"catalog"` (all images in one partition for simple list queries)
- RowKey: `imageId` (UUID)

| Field | Type | Notes |
|-------|------|-------|
| imageId | string (UUID) | RowKey. Referenced by DeviceSession.assignedImageId. |
| name | string | Human-readable display name, e.g., "Windows 11 24H2 Enterprise". |
| version | string | Version tag, e.g., "24H2" or "1.0.3". |
| description | string | Optional notes for technicians. |
| blobContainerName | string | Blob Storage container name where the file lives. |
| blobName | string | Blob name (path within container), e.g., "win11-24h2-ent.wim". |
| fileSizeBytes | long | Used to display download size estimate in WPF UI. |
| uploadedAt | DateTime (UTC) | When the image was uploaded via the Admin Portal. |
| uploadedBy | string | Entra ID object ID of the admin who uploaded. |
| activeSessionCount | int | Count of sessions currently in Imaging status assigned to this image. Prevents deletion when > 0. Updated transactionally by SessionHandler. |
| isActive | bool | Soft-enable/disable; inactive images do not appear in the assignment catalog. |

---

### BrandingConfiguration

Stores the portal's configurable branding settings. Single row (one branding
config per deployment).

**Table Storage**: table `Configuration`
- PartitionKey: `"branding"`
- RowKey: `"default"`

| Field | Type | Notes |
|-------|------|-------|
| applicationName | string | Display name shown in the portal header and browser title. |
| logoUrl | string or null | URL of logo blob in Blob Storage. Null uses the default placeholder. |
| primaryColorHsl | string | HSL value, e.g., `"210 100% 50%"`. Maps to `--primary` CSS variable. |
| accentColorHsl | string | HSL value. Maps to `--accent` CSS variable. |
| updatedAt | DateTime (UTC) | Last modification timestamp. |
| updatedBy | string | Entra ID object ID of the admin who last updated. |

Branding values are served by the Admin Portal Express backend at startup and
injected as CSS custom properties into `client/src/index.css` at runtime.
Changes take effect on next page load without redeployment.

---

## Transient Types (not persisted)

### SASCredential

Returned to the WPF client upon session coupling and on refresh requests.
Not stored; generated on demand by SessionHandler.

| Field | Type | Notes |
|-------|------|-------|
| downloadUrl | string | Full SAS URL for the assigned blob (includes SAS query parameters). |
| expiresAt | DateTime (UTC) | When the SAS token expires (configurable, default: 4 hours from issue). |
| fileSizeBytes | long | Duplicated from OSImageMetadata for WPF client display. |

### SessionToken

Issued by SessionBroker to the WPF client at session registration. Signed JWT.

| Claim | Value |
|-------|-------|
| `sub` | sessionId |
| `jti` | unique token ID |
| `iat` | issued-at (Unix timestamp) |
| `exp` | expiry (Unix timestamp, default: 8 hours) |
| `iss` | `"cloudimaging/session-broker"` |

---

## Storage Layout

```
Azure Blob Storage
  Container: os-images/
    {imageId}/{blobName}          # OS image files (.wim / .esd)
  Container: branding/
    logo/{filename}               # Uploaded branding logos

Azure Table Storage
  Table: DeviceSessions           # DeviceSession entities
  Table: ImagingSteps             # ImagingStep entities
  Table: OSImages                 # OSImageMetadata entities
  Table: Configuration            # BrandingConfiguration + future config rows
```

---

## Entity Relationships

```
DeviceSession (1) ----< (N) ImagingStep
DeviceSession (N) >---- (1) OSImageMetadata
BrandingConfiguration     (singleton, no FK)
```

All foreign key references are by ID only (no joins). Referential integrity is
enforced by SessionHandler application logic, not by Table Storage.

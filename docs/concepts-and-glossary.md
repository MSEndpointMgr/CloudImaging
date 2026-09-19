# Concepts and glossary

## Boot-media certificate

An X.509 certificate used for mutual TLS between the Cloud Imaging Client and Device Gateway API. Imaging Core stores the active certificate as a PKCS#12 secret in Azure Key Vault. Media Builder retrieves it through the Operator API and includes it in generated boot media; the Client loads it when calling Device Gateway.

A deployment has one active boot-media certificate. Generating or rotating it activates the replacement immediately, so media containing the previous certificate must be rebuilt. See [Certificate management](operations-runbook.md#2-certificate-management).

## Coupling

The one-time action that associates the physical device displaying a passcode with the technician's Portal workflow. A successful coupling consumes the passcode and moves the session from `SessionAllowed` to `SessionAssigned`.

Coupling does not assign an OS image. The technician selects an image separately, which moves the session to `SessionStarted`. See [Session lifecycle](session-lifecycle.md).

## Device-session token

A high-entropy bearer credential issued to the Client when an authorized session is registered. It is bound to one session, never shown to the technician, and distinct from both the coupling passcode and a Microsoft Entra ID token.

The Client uses it for authenticated Device Gateway operations such as status polling, progress reporting, download-link refresh, and log upload. The plain token is returned only to the Client; a hash is retained for validation. Its issued lifetime is 24 hours.

## Image lifecycle

Cloud Imaging has three image catalogs:

| Catalog | Purpose | Selection behavior |
|---|---|---|
| **OS images** | Windows WIM, ESD, or ISO content applied to target devices | Technician assigns an active image to each session |
| **Boot images** | WinPE WIM produced by Media Builder | Media Builder preselects the latest published image when preparing USB media; other active entries remain selectable |
| **Recovery images** | WinRE WIM applied to the recovery partition | Client uses the latest published recovery image; no per-session selection |

Portal uploads go directly to a staging area in Azure Blob Storage through a scoped upload SAS URL. Imaging Core processes the upload asynchronously through queued, verifying, optional extracting, and publishing stages. It verifies integrity before creating the catalog entry. For an OS ISO, the publish job extracts `sources/install.wim` or `sources/install.esd` and publishes that deployable image rather than the ISO.

Deleting an OS image is blocked while an active `SessionStarted` or `SessionInProgress` session references it. The latest published boot or recovery image cannot be deleted until a replacement is published.

## Managed identity

An identity assigned to an Azure resource, allowing it to authenticate to another Azure service without an application secret or storage account key in configuration.

Cloud Imaging uses managed identities for service-to-service and Azure resource access. For example, the Portal backend calls Operator API with its managed identity, and Imaging Core accesses Storage, Key Vault, and Microsoft Graph under assigned roles and permissions. See [Architecture overview](architecture-overview.md) and [Roles and access](roles-and-access.md).

## SAS URL

A time-limited Azure Storage URL carrying a shared access signature (SAS). It grants narrowly scoped access to one storage operation without giving the caller an account key or general storage access.

The Client downloads its assigned OS image directly from Blob Storage through a read-only SAS URL. OS image URLs default to four hours and the Client requests a replacement when less than 15 minutes remain. Changing the configured lifetime affects newly issued URLs, not existing ones.

Portal image uploads use separate write-scoped SAS URLs for their staging blobs. Media Builder also receives time-limited URLs when downloading boot images.

## Terminal session state

A state from which a session cannot continue:

| State | Meaning |
|---|---|
| `SessionCompleted` | All imaging stages completed successfully |
| `SessionFailed` | A stage failed or an active/coupled session timed out |
| `SessionNotAuthorized` | Enabled pre-flight authorization denied the device |
| `SessionExpired` | An uncoupled session timed out; this is a benign expiry, not an imaging failure |

Terminal live-session records are retained for 24 hours before lifecycle cleanup. Reporting history is stored separately for the configured retention period.
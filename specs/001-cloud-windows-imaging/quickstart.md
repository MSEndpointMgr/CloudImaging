# Quickstart Validation Guide: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging  
**Date**: 2026-06-16  
**Purpose**: Execute runnable validation scenarios for the current architecture.

## References

- Data model: `data-model.md`
- API contracts:
  - `contracts/device-gateway-api.md`
  - `contracts/operator-api.md`
  - `contracts/imaging-core-api.md`
  - `contracts/cloud-imaging-portal-api.md`

## Prerequisites

### Required Azure resources

- Storage Account (blob + table)
- Azure Functions: Device Gateway API (public)
- Azure Functions: Operator API (public, Entra-authenticated)
- Azure Functions: Imaging Core API (Private Link only)
- Cloud Imaging Portal backend (Node.js, Entra-authenticated)
- Cloud Imaging Portal frontend (Static Web Apps)
- VNet + Private Endpoint + Private DNS for Imaging Core API
- Entra app registrations and app-role assignments for portal and media builder

### Local/staging setup checks

1. Build and test .NET projects succeed with zero warnings.
2. Portal backend and frontend run with valid environment configuration.
3. Private routing from Device Gateway/Operator APIs to Imaging Core API is verified.

## Scenario 1: Device Session Bootstrap

**Validates**: FR-001, FR-002, FR-010, US1

1. Boot Cloud Imaging Client in WinPE.
2. Client calls `POST /api/sessions` on Device Gateway API.
3. Verify response includes:
   - `sessionId`
   - one-time `passcode`
   - `deviceSessionToken`
4. Verify passcode is displayed in UI within 10 seconds.

Expected result:
- Session exists with `SessionInit` or `SessionAllowed`.
- Passcode is one-time pairing code; ongoing calls use device-session token.

## Scenario 2: Couple and Assign from Portal

**Validates**: FR-032, FR-033, FR-061, US2

1. Authenticate to portal (Entra ID).
2. Enter passcode and assign OS image from catalog.
3. Portal backend calls Operator API coupling endpoint.
4. Operator API brokers to Imaging Core API.
5. Client polling receives `SessionAssigned` and assignment details.

Expected result:
- Coupling succeeds once.
- Invalid/expired passcodes fail with clear errors.

## Scenario 3: Imaging Progress and SAS Refresh

**Validates**: FR-003 to FR-009, FR-014 to FR-016, US3

1. Client polls Device Gateway API every 30 seconds.
2. Client downloads image with SAS URL and reports progress steps.
3. Force nearing SAS expiry (<15 minutes remaining).
4. Client requests SAS refresh via Device Gateway API.

Expected result:
## Scenario 4: Bulk Assignment

**Validates**: FR-035, SC-005, US4

1. Create multiple waiting sessions.
2. Use portal bulk assign to apply one image to all selected sessions.
3. Verify initiation completes within 5 seconds for 20+ sessions.

Expected result:
- Bulk operation is successful.
- Per-session progress remains independently trackable.

## Scenario 5: OS Image CRUD

**Validates**: FR-036, FR-037, SC-008, US5

1. Upload/register a new OS image.
2. Edit metadata.
3. Attempt delete while image is in active use.
4. Delete when no active sessions reference image.

Expected result:
- Active-use delete is blocked.
- Non-active image delete succeeds.

## Scenario 6: Branding Runtime Update

**Validates**: FR-038, FR-039, SC-006, US6

1. Update application name, logo, and color settings.
2. Reload portal page.

Expected result:
- Branding appears on reload without redeployment.

## Scenario 7: Media Builder Generate Boot Image

**Validates**: FR-050, FR-051, SC-012, US7

1. Run Media Builder Generate Boot Image workflow.
2. Verify output artifact + signed manifest.

Expected result:
- Boot image artifact and manifest are produced with expected metadata.

## Scenario 8: Media Builder Prepare USB Device

**Validates**: FR-052 to FR-059, SC-013, SC-014, US7

1. Sign in via Entra ID.
2. Query boot images through Operator API.
3. Select removable USB target and confirm destructive action.
4. Prepare two partitions (cache + bootable).
5. Download boot image via SAS (off UI thread).
6. Deploy boot payload and configure WinPE auto-start.

Expected result:
- Non-removable/system disks are blocked.
- USB is bootable and auto-starts Cloud Imaging Client.

## Scenario 9: Session Lifecycle Enforcement

**Validates**: FR-021, T112 coverage intent

1. Let pre-imaging session idle > 30 minutes.
2. Verify session expires.
3. Let in-progress session miss heartbeat > 4 hours.
4. Verify transition to failed state.
5. Verify terminal sessions purge after 24 hours.

Expected result:
- Lifecycle rules are enforced consistently by Imaging Core API.

## CI Gate Execution Order

1. Build gate (.NET + Node.js)
2. Unit test gate
3. Integration test gate
4. Contract tests for all APIs
5. Accessibility and lint gates
6. Performance and responsiveness gates

All gates must pass before merge.

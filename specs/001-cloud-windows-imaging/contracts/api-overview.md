# Cloud Imaging API Overview

This document defines the purpose and responsibility split between the three APIs in the Cloud Imaging architecture.

## API Responsibilities at a Glance

| API | Primary Consumers | Network Exposure | Auth Model | Core Responsibility |
|-----|-------------------|------------------|------------|---------------------|
| Device Gateway API | Cloud Imaging Client (WinPE) | Public HTTPS | Unauthenticated bootstrap, then device-session bearer token | Device-facing session bootstrap, polling, progress relay, SAS refresh relay |
| Operator API | Cloud Imaging Portal backend, Cloud Imaging Media Builder | Public HTTPS | Entra ID bearer token + app-role authorization | Operator-facing authenticated operations (sessions, image catalogs, branding, boot images) |
| Imaging Core API | Device Gateway API, Operator API | Private Link only | Trusted service-to-service calls + app roles | Source of truth for session state, SAS issuance, catalog metadata, lifecycle enforcement |

## Why Three APIs Exist

1. Device clients in WinPE need a minimal, resilient public entry point that does not require Entra ID interactive sign-in.
2. Human/operator tools must use Entra ID and RBAC, with stricter authorization boundaries.
3. Core orchestration and storage permissions must stay private and unreachable from public clients.

## Responsibility Boundaries

### Device Gateway API

- Handles Cloud Imaging Client startup and session bootstrap.
- Issues device-session tokens for ongoing client calls.
- Relays client status and progress to Imaging Core API.
- Returns assignment and SAS details to clients from Imaging Core API.
- Never holds direct Storage Account read/write rights.

### Operator API

- Handles all operator-facing authenticated operations.
- Separates Cloud Imaging Portal and Cloud Imaging Media Builder by app role.
- Exposes session management, OS image management, branding, and boot image operations.
- Brokers all orchestration calls to Imaging Core API over private connectivity.

### Imaging Core API

- Owns the authoritative device session lifecycle and transition rules.
- Generates and refreshes SAS tokens for OS image and boot image downloads.
- Stores and serves OS image metadata, branding metadata, and boot image metadata.
- Enforces passcode consume semantics and inactivity/heartbeat/purge policies.

## High-Level Call Flows

### Device Imaging Flow

1. Cloud Imaging Client -> Device Gateway API: create session.
2. Device Gateway API -> Imaging Core API: create session and passcode/hash metadata.
3. Cloud Imaging Portal backend -> Operator API: couple/assign image.
4. Operator API -> Imaging Core API: couple session and assign image.
5. Cloud Imaging Client -> Device Gateway API: poll status/progress.
6. Device Gateway API -> Imaging Core API: state/progress/SAS refresh.

### Media Builder Flow

1. Cloud Imaging Media Builder -> Operator API: list boot images and retrieve SAS for the latest published boot image (Entra token).
2. Cloud Imaging Media Builder downloads boot image via SAS URL from Storage.

### Boot Image Upload Flow

1. Cloud Imaging Portal backend -> Operator API: create boot image upload session for a WIM artifact.
2. Operator API -> Imaging Core API: issue staged upload authorization (write SAS or equivalent) for a blob that is not yet published.
3. Cloud Imaging Portal browser -> Storage: upload the WIM in chunks with progress and retry within the same upload session.
4. Cloud Imaging Portal backend -> Operator API: finalize publish after the upload commits and checksum/manifest validation succeed.
5. Operator API -> Imaging Core API: persist the blob reference, mark the boot image as published, and make it visible to Media Builder catalog queries.

## Endpoint Specifications

Detailed endpoint contracts live in:

- device-gateway-api.md
- operator-api.md
- imaging-core-api.md
- cloud-imaging-portal-api.md

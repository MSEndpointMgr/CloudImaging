# Research: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging  
**Date**: 2026-06-16  
**Status**: Complete

## Decision 1: Three-API topology is the canonical boundary model

**Decision**: Use three explicit API boundaries:
- Device Gateway API (public, device-facing)
- Operator API (public, Entra-authenticated, operator-facing)
- Imaging Core API (private, Private Link only)

**Rationale**:
- WinPE device workflow needs bootstrap access without interactive Entra sign-in.
- Operator workflows require Entra ID + app-role authorization.
- Core orchestration and storage permissions must remain private.

**Alternatives considered**:
- Single public API for all consumers: rejected due to larger attack surface and mixed auth semantics.
- Device clients calling core APIs directly: rejected due to private network and security boundary violations.

## Decision 2: Device auth model separates pairing and ongoing API auth

**Decision**: Pairing passcode is one-time only; ongoing calls use a high-entropy device-session token.

**Rationale**:
- Passcode is user-entered and low entropy compared to bearer tokens.
- One-time passcode semantics reduce replay risk.
- Device-session token aligns with polling/progress/refresh flows.

**Alternatives considered**:
- Reusing passcode for API authentication: rejected by security model and requirement clarifications.
- Anonymous polling after bootstrap: rejected due to lack of session-bound authorization.

## Decision 3: Imaging Core API enforces lifecycle and retention policy

**Decision**: Imaging Core API is the single owner of lifecycle state transitions and expiry policies.

**Rationale**:
- Prevents divergent lifecycle behavior across external callers.
- Centralizes SessionAssigned -> SessionStarted auto-transition and heartbeat policies.
- Simplifies operational diagnostics and auditability.

**Alternatives considered**:
- Duplicating lifecycle logic in Device Gateway and Operator APIs: rejected due to drift risk.
- Client-owned transition logic: rejected due to trust and consistency concerns.

## Decision 4: Operator API is the only operator ingress

**Decision**: Cloud Imaging Portal backend and Cloud Imaging Media Builder call only Operator API for operator-facing operations.

**Rationale**:
- Enforces one authorization and audit boundary.
- Supports app-role separation between portal and media builder.
- Avoids exposing private orchestration details to operator clients.

**Alternatives considered**:
- Portal backend calling Imaging Core API directly: rejected by private boundary model.
- Independent APIs per operator tool: rejected due to duplicated authorization and contract maintenance.

## Decision 5: Boot image lifecycle is distinct from OS image lifecycle

**Decision**: Keep boot image operations as a distinct endpoint set and metadata model from OS image operations.

**Rationale**:
- Different producers/consumers and validation workflows.
- Media Builder requires read + SAS retrieval; portal backend requires CRUD.
- Supports FR-063 separation requirement.

**Alternatives considered**:
- Single unified image catalog for all artifacts: rejected due to policy and workflow ambiguity.

## Decision 6: Table Storage + Blob Storage remains cost-effective baseline

**Decision**: Persist metadata/state in Azure Table Storage and binary artifacts in Blob Storage.

**Rationale**:
- Fits key-value access patterns for sessions and catalogs.
- Lower operational cost for self-hosting/community deployment.
- Works with managed identities and SAS issuance model.

**Alternatives considered**:
- Cosmos DB: rejected due to unnecessary cost/complexity at v1 target scale.
- Relational database as primary state store: rejected due to overhead not required by access patterns.

## Decision 7: Contract-first cross-runtime compatibility

**Decision**: Keep API contracts in `specs/.../contracts/` and generate/validate client-side types against API specs where possible.

**Rationale**:
- Reduces DTO drift across .NET and TypeScript boundaries.
- Keeps implementation behavior aligned with documented endpoint contracts.

**Alternatives considered**:
- Hand-maintained client interfaces only: rejected due to drift risk.

## Decision 8: Quality and release gates remain constitution-driven

**Decision**: Keep zero-warning .NET policy, test-first sequencing, integration/e2e gates, and UI responsiveness gating as mandatory for implementation planning.

**Rationale**:
- Directly required by constitution.
- Necessary for reliable WinPE and operator workflows.

**Alternatives considered**:
- Relaxed warning/test gating during early delivery: rejected due to constitutional non-negotiables.

## Decision 9: GitHub Actions with OIDC Workload Identity Federation for internal dev deployments

**Decision**: Use GitHub Actions workflows with OIDC-based Azure Workload Identity Federation for all core developer deployments to the shared Azure dev environment. Two workflows are provided: `deploy-dev.yml` (full-stack IaC + all component deployment, manual trigger for initial setup or complete refresh) and `deploy-components.yml` (per-component redeployment via `workflow_dispatch` with a component name input for iterative development). Both are stored in `.github/workflows/` but are explicitly excluded from community release bundles. VS Code tasks (`.vscode/tasks.json`) invoke `deploy-components.yml` via `gh workflow run` to provide an in-editor deployment trigger.

**Rationale**:
- OIDC Workload Identity Federation eliminates long-lived Azure credentials from GitHub repository secrets; access tokens are short-lived, scoped to the workflow run, and auto-rotated by the Azure/GitHub trust relationship.
- Two-workflow model balances initial full-stack provisioning needs (deploy-dev.yml) with iterative development efficiency (deploy-components.yml targeting only the changed component).
- Keeping these workflows inside `.github/workflows/` but explicitly excluding them from the community release bundle cleanly separates internal team tooling from the self-hoster deliverable; community deployment is performed exclusively via `deploy.ps1` and `update.ps1`.
- The VS Code task layer provides a low-friction in-editor trigger without bypassing the GitHub Actions audit trail.

**Alternatives considered**:
- Long-lived service principal client secret in GitHub repository secrets: rejected -- credential exposure risk on secret compromise, rotation burden, and counter to current Azure best-practice guidance.
- Self-hosted runner with managed identity: rejected -- requires a persistent Azure VM or ACI instance, which is disproportionate overhead for a two-contributor project sharing a single subscription.
- Per-developer `az login` from local workstations: rejected -- not reproducible as a team process, no audit trail, and diverges from the GitHub Actions deployment path used for release validation.

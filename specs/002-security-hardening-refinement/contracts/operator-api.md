# Contract Delta: Operator API

**Feature**: 002-security-hardening-refinement

## Affected Endpoints

- Session coupling endpoint(s) invoked by portal/media builder workflows
- Administrative observability endpoint(s) for abuse/replay counters and outcomes

## Security Behavior Requirements

1. Operator-initiated coupling follows passcode abuse controls and one-time semantics.
2. Safe error contract is preserved for invalid, expired, consumed, or blocked passcodes.
3. Administrative observability exposes aggregate blocked/replay counts without sensitive payloads.
4. Correlation identifiers are propagated across Operator API -> Imaging Core API boundaries.

## Response Contract Additions

| Field | Type | Required | Notes |
|------|------|----------|-------|
| correlationId | string | Yes | Traceability |
| errorCode | string | Yes on non-2xx | Uses shared taxonomy |
| retryAfterSeconds | int | Conditional | Included when lockout/backoff applies |

## Authorization Contract

- Existing Entra/app-role enforcement remains unchanged.
- New observability endpoints must require administrative role access.
- Security telemetry views return only non-sensitive aggregates and reason categories.

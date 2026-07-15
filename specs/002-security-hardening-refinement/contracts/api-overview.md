# API Overview: Security Hardening Contract Deltas

**Feature**: 002-security-hardening-refinement  
**Scope**: Passcode abuse controls, token replay prevention, and logging redaction guarantees

## Purpose

This document defines the contract-level security deltas for existing APIs. It is
additive to current endpoint contracts and does not introduce a new external API
surface.

## Cross-Cutting Contract Rules

1. Safe error responses only: no secret-bearing values in response payloads.
2. Security decisions must emit structured telemetry with correlation identifiers.
3. Device-session token validation applies to all device-authenticated operations.
4. Replay rejection must not mutate session state.
5. Logging/telemetry payloads must apply prohibited-sensitive-field policy before persistence.

## Standard Security Error Taxonomy

| Code | HTTP | Meaning |
|------|------|---------|
| `PASSCODE_ATTEMPT_BLOCKED` | 429 | Threshold exceeded; temporary lockout/backoff active. |
| `PASSCODE_INVALID` | 401 | Provided passcode invalid/expired/consumed. |
| `TOKEN_REPLAY_REJECTED` | 401 | Token or token+nonce replay detected. |
| `TOKEN_CONTEXT_MISMATCH` | 403 | Token presented for wrong session context. |
| `TOKEN_REVOKED_OR_EXPIRED` | 401 | Token no longer valid due to revocation or expiry. |
| `SECURITY_POLICY_VIOLATION` | 400 | Request violates required security contract semantics. |

## Required Response Metadata

All security-relevant responses include:
- `correlationId` (required)
- `errorCode` (required when non-2xx)
- `retryAfterSeconds` (required for lockout/backoff responses)

No response body may include passcodes, device-session token values, SAS token
URLs, private key material, or equivalent secrets.

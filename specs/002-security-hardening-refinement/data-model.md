# Data Model: Security Hardening Refinement

**Feature**: 002-security-hardening-refinement  
**Date**: 2026-07-15  
**Storage Baseline**: Azure Table Storage for security state and audit metadata

## Entity: PasscodeAbusePolicy

Defines how coupling passcode abuse is evaluated.

**Suggested table**: `SecurityPolicies`  
- `PartitionKey`: `passcodeAbuse`  
- `RowKey`: policy name (for example `default`)

| Field | Type | Description |
|------|------|-------------|
| policyName | string | Unique policy identifier. |
| maxAttempts | int | Maximum invalid attempts allowed within evaluation window. |
| windowSeconds | int | Sliding/fixed window used for attempt counting. |
| lockoutSeconds | int | Temporary lockout duration after threshold breach. |
| responseMode | enum | `Backoff` or `Lockout` behavior. |
| sourceDimensions | string[] | Dimensions used for fingerprinting (for example sessionId + source hash). |
| isEnabled | bool | Runtime enable/disable flag. |
| updatedAt | datetime | Last policy update timestamp. |
| updatedBy | string | Operator/system identity that changed policy. |

## Entity: PasscodeAttemptCounter

Tracks invalid attempts and lockout state for a source/session scope.

**Suggested table**: `PasscodeAttemptCounters`  
- `PartitionKey`: `sessionId`  
- `RowKey`: source fingerprint hash

| Field | Type | Description |
|------|------|-------------|
| sessionId | string (UUID) | Session under coupling attempt. |
| sourceFingerprintHash | string | Hashed source context key. |
| invalidAttemptCount | int | Current invalid attempt count in active window. |
| windowStartedAt | datetime | Start of active evaluation window. |
| lockoutUntil | datetime? | Active lockout expiry. |
| lastAttemptAt | datetime | Timestamp of most recent attempt. |
| lastOutcome | enum | `InvalidPasscode`, `ExpiredPasscode`, `ConsumedPasscode`, `BlockedByPolicy`, `Success`. |
| correlationId | string | Last correlated request id. |

## Entity: PasscodeAbuseEvent

Immutable security audit event for passcode decisions.

**Suggested table**: `SecurityEvents`  
- `PartitionKey`: `passcodeAbuse`  
- `RowKey`: event id (time-sortable unique id)

| Field | Type | Description |
|------|------|-------------|
| eventId | string | Unique event identifier. |
| sessionId | string (UUID) | Target session. |
| sourceFingerprintHash | string | Source context reference (non-sensitive). |
| outcomeCategory | enum | `Allowed`, `Rejected`, `Blocked`, `LockedOut`. |
| reasonCode | string | Stable machine-readable reason. |
| occurredAt | datetime | Event timestamp. |
| correlationId | string | Cross-service trace identifier. |
| actorType | enum | `Device`, `Operator`, `System`. |

## Entity: DeviceSessionTokenValidationRecord

Tracks device-session token status and replay-relevant evaluation context. The "DeviceSession" prefix aligns this entity with the `DeviceSessionToken` naming convention used in feature 001.

**Suggested table**: `DeviceSessionTokenValidation`  
- `PartitionKey`: `sessionId`  
- `RowKey`: token identifier (`tokenId`)

| Field | Type | Description |
|------|------|-------------|
| sessionId | string (UUID) | Expected session context for token. |
| tokenId | string | Unique token identifier (jti or equivalent). |
| tokenFamilyId | string | Family identifier for rotated token lineage. |
| issuedAt | datetime | Token issuance time. |
| expiresAt | datetime | Token expiry time. |
| revokedAt | datetime? | Revocation timestamp if revoked. |
| isReplayDetected | bool | True when replay behavior is confirmed. |
| replayDetectedAt | datetime? | Replay detection timestamp. |
| lastValidatedAt | datetime | Last validation timestamp. |
| lastValidationOutcome | enum | `Accepted`, `RejectedExpired`, `RejectedRevoked`, `RejectedReplay`, `RejectedContextMismatch`. |
| correlationId | string | Last correlated request id. |

## Entity: TokenRequestFingerprint

Supports safe retries and replay protection on mutating endpoints. Keyed by the device-session tokenId.

**Suggested table**: `TokenRequestFingerprints`  
- `PartitionKey`: `tokenId`  
- `RowKey`: request nonce/hash

| Field | Type | Description |
|------|------|-------------|
| tokenId | string | Token identifier used for the request. |
| requestNonce | string | Client-provided nonce/idempotency key or canonical request hash. |
| operationName | string | Endpoint operation name. |
| firstSeenAt | datetime | First observation timestamp. |
| lastSeenAt | datetime | Last seen timestamp for retries. |
| decision | enum | `Accepted`, `RejectedReplay`, `AcceptedIdempotentRetry`. |
| correlationId | string | Request correlation id. |

## Entity: SecurityRedactionPolicy

Defines prohibited fields/patterns and redaction behavior.

**Suggested table**: `SecurityPolicies`  
- `PartitionKey`: `redaction`  
- `RowKey`: policy name (for example `default`)

| Field | Type | Description |
|------|------|-------------|
| policyName | string | Policy identifier. |
| prohibitedFieldNames | string[] | Field names to always redact/omit. |
| prohibitedPatterns | string[] | Pattern signatures (for example SAS URL query signatures, key headers). |
| redactionMode | enum | `Mask`, `DropField`, `DropPayload`. |
| exceptionPathEnabled | bool | Whether exception payload inspection is enforced. |
| ciValidationPatterns | string[] | Pattern set used by CI leakage tests. |
| isEnabled | bool | Runtime flag. |
| updatedAt | datetime | Last update timestamp. |

## Entity: SecurityTelemetryEvent

Structured event emitted for abuse control, replay defense, and redaction outcomes.

**Suggested table**: `SecurityTelemetry` (optional persisted mirror if sinking beyond logs)

| Field | Type | Description |
|------|------|-------------|
| eventId | string | Unique telemetry event id. |
| eventType | enum | `PasscodeAbuseBlocked`, `TokenReplayRejected`, `RedactionApplied`, `RedactionPolicyViolation`. |
| severity | enum | `Info`, `Warning`, `Error`, `Critical`. |
| sessionId | string? | Related session id, when available. |
| sourceContext | string | Non-sensitive source classification. |
| occurredAt | datetime | Event timestamp. |
| correlationId | string | Trace identifier for end-to-end diagnostics. |
| attributesJson | string | Structured non-sensitive dimensions for analysis. |

## Relationships

- `PasscodeAbusePolicy` 1..* `PasscodeAttemptCounter`
- `PasscodeAttemptCounter` 1..* `PasscodeAbuseEvent`
- `DeviceSessionTokenValidationRecord` 1..* `TokenRequestFingerprint`
- `SecurityRedactionPolicy` governs emission behavior for all `SecurityTelemetryEvent` and standard logs

## State Rules

1. Passcode coupling succeeds at most once per passcode; subsequent attempts are rejected.
2. Lockout/backoff applies when invalid attempts exceed policy threshold within the policy window.
3. Tokens that are expired, revoked, replayed, or context-mismatched are rejected without state mutation.
4. Retried requests with approved idempotency semantics are accepted as retries, not abuse.
5. All blocked security decisions emit telemetry with correlation id and non-sensitive context.

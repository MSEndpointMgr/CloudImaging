# Contract Delta: Imaging Core API

**Feature**: 002-security-hardening-refinement

## Affected Internal Contracts

- Coupling validation and passcode consumption workflows
- Session token validation for all device-session authenticated operations
- Security telemetry/logging persistence interfaces

## Security Behavior Requirements

1. Enforce passcode policy checks before coupling success decisions.
2. Guarantee one-time passcode consumption with concurrency-safe persistence semantics.
3. Validate token freshness, revocation status, and session binding for each request.
4. Reject replayed/mismatched tokens with no session state mutation.
5. Distinguish idempotent retry from replay abuse for supported operations.
6. Apply redaction policy to all structured logs/telemetry and exception payloads before sink write.

## Internal Validation Contract

| Validation Step | Result on Failure |
|-----------------|------------------|
| Passcode policy check | `PASSCODE_ATTEMPT_BLOCKED` or `PASSCODE_INVALID` |
| Token status check | `TOKEN_REVOKED_OR_EXPIRED` |
| Session binding check | `TOKEN_CONTEXT_MISMATCH` |
| Replay/nonce check | `TOKEN_REPLAY_REJECTED` |

## Telemetry Contract

Mandatory event types:
- `PasscodeAbuseBlocked`
- `TokenReplayRejected`
- `RedactionApplied`
- `RedactionPolicyViolation`

Mandatory fields:
- `eventType`
- `occurredAt`
- `correlationId`
- `severity`
- `sessionId` (if available)
- `reasonCode` (for rejection events)

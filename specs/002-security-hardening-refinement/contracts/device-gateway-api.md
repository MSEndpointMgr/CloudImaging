# Contract Delta: Device Gateway API

**Feature**: 002-security-hardening-refinement

## Affected Endpoints

- Coupling/passcode validation endpoint(s)
- Device poll/status endpoint(s)
- Progress/update endpoint(s)
- Token refresh endpoint(s)

## Security Behavior Requirements

1. Passcode abuse policy is evaluated on every coupling attempt.
2. Coupling returns lockout/backoff response after threshold breach.
3. One-time passcode consumption is race-safe under concurrent attempts.
4. Device-session token validation enforces expiry, revocation, and session binding.
5. Replay detection rejects reused invalid tokens or invalid token+nonce combinations.
6. Safe duplicate retries may be accepted under idempotent rules for supported operations.

## Request Header Expectations

| Header | Required | Purpose |
|--------|----------|---------|
| `Authorization: Bearer <deviceSessionToken>` | Yes (device-authenticated endpoints) | Session authentication |
| `X-Correlation-Id` | Yes | End-to-end tracing |
| `X-Request-Nonce` | Required for mutating operations | Replay/idempotency control |

## Response Rules

- 2xx: include `correlationId`.
- 401/403/429: include `correlationId`, `errorCode`, and optional `retryAfterSeconds` where applicable.
- Never include secret-bearing values in error details.

## Telemetry Events

- `PasscodeValidationFailed`
- `PasscodeAbuseBlocked`
- `TokenReplayRejected`
- `TokenContextMismatch`

Each event must include `correlationId`, `sessionId` (when available), and non-sensitive source context.

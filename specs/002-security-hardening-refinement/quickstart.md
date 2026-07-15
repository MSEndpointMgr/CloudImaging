# Quickstart Validation Guide: Security Hardening Refinement

**Feature**: 002-security-hardening-refinement  
**Date**: 2026-07-15  
**Purpose**: Validate passcode abuse controls, token replay prevention, and redaction enforcement end-to-end.

## References

- Data model: `data-model.md`
- Contracts:
  - `contracts/api-overview.md`
  - `contracts/device-gateway-api.md`
  - `contracts/operator-api.md`
  - `contracts/imaging-core-api.md`

## Prerequisites

1. Existing CloudImaging environment is deployed and healthy.
2. Device Gateway, Operator API, Imaging Core API, and portal backend are reachable.
3. Test identities/devices for authorized and unauthorized scenarios are available.
4. CI pipeline includes security log/telemetry scan stage.

## Scenario 1: Passcode Abuse Threshold and Lockout

**Validates**: FR-001, FR-002, FR-004, SC-001

1. Create an active session with a valid one-time passcode.
2. Submit invalid passcodes from one source context until the configured threshold is exceeded.
3. Attempt one additional coupling request during lockout.
4. Wait for lockout to expire and submit the valid passcode.

Expected result:
- Requests beyond threshold are blocked with safe errors.
- Abuse/lockout events are emitted with correlation ids.
- Valid request after lockout succeeds if passcode is still valid and unconsumed.

## Scenario 2: Passcode Race Safety (One-Time Use)

**Validates**: FR-003, FR-015

1. Start two concurrent coupling requests for the same valid passcode.
2. Force near-simultaneous submission.

Expected result:
- Exactly one request succeeds.
- Remaining request is rejected as consumed/invalid without session corruption.

## Scenario 3: Token Replay Rejection Across Authenticated Operations

**Validates**: FR-005, FR-006, FR-008, SC-002

1. Obtain a valid device-session token and perform a normal poll/progress flow.
2. Replay a previously invalidated/revoked token against polling endpoint.
3. Replay the same token and nonce against a mutating endpoint.
4. Submit a token bound to Session A against Session B.

Expected result:
- Replay/revoked/context-mismatched attempts are rejected.
- No unauthorized state mutation occurs.
- Replay security telemetry events are emitted with reason codes.

## Scenario 4: Benign Retry Handling

**Validates**: FR-007

1. Perform an operation with a valid token and idempotency nonce.
2. Simulate transient network failure and retry with same nonce.
3. Retry with modified payload under same nonce.

Expected result:
- Safe duplicate retry is accepted under idempotent semantics.
- Mismatched replay attempts are rejected.
- Progress and session consistency are preserved.

## Scenario 5: Logging and Telemetry Redaction

**Validates**: FR-009, FR-010, FR-011, SC-003

1. Execute successful and failing coupling/token validation workflows.
2. Force exception paths that include request context.
3. Collect logs, traces, and telemetry payloads.
4. Search outputs for prohibited values (passcodes, session tokens, SAS token URLs, certificate private material).

Expected result:
- Prohibited fields are redacted or omitted in all paths.
- Correlation ids and non-sensitive diagnostics remain available.

## Scenario 6: CI Security Leakage Gate

**Validates**: FR-012, SC-003

1. Run CI validation stage that scans logs/traces/diagnostics for prohibited patterns.
2. Inject a synthetic prohibited pattern in a controlled branch run.

Expected result:
- Baseline run passes with zero leaks.
- Injected leak causes deterministic CI failure and blocks merge.

## Scenario 7: Administrative Security Observability

**Validates**: FR-013, FR-014, SC-004

1. Review security telemetry dashboards/queries for blocked passcode attempts and replay rejections.
2. Confirm policy configuration values are visible with secure defaults.

Expected result:
- Blocked attempt/replay counts are observable.
- Configuration is deployment-safe and does not expose secrets.

## Regression Guard

Run representative authorized imaging workflows before and after enabling controls.

Expected result:
- End-to-end authorized imaging still succeeds.
- Coupling/polling latency increase remains within the <= 10% p95 threshold (SC-005).

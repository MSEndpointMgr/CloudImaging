# Feature Specification: Security Hardening Refinement

**Feature Branch**: `002-security-hardening-refinement`

**Created**: 2026-07-15

**Status**: Draft

**Input**: User description: "with a security-hardening refinement focused on passcode abuse controls, token replay prevention, and logging redaction."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Harden Session Coupling Against Passcode Abuse (Priority: P1)

A technician enters a device passcode to couple an active session, while the system continuously protects the coupling flow against brute-force attempts, rapid retries, and abusive automation without blocking legitimate operators.

**Why this priority**: Session coupling is a public-facing control surface that can be abused before imaging begins. Hardening this path reduces unauthorized session takeover risk across all deployments.

**Independent Test**: Can be fully tested by running high-volume invalid passcode attempts against coupling endpoints while validating that legitimate coupling requests still succeed within acceptable latency.

**Acceptance Scenarios**:

1. **Given** repeated invalid passcode attempts from the same source context, **When** the retry threshold is exceeded within the configured window, **Then** the system applies temporary lockout or backoff and records an abuse event.
2. **Given** an active session with a valid passcode, **When** a legitimate operator submits the correct passcode outside any active lockout, **Then** coupling succeeds and the passcode is consumed once.
3. **Given** a passcode that is expired, consumed, or invalid, **When** it is submitted, **Then** the system rejects the request with a safe error response and records the failed attempt for auditing.
4. **Given** simultaneous coupling attempts against the same passcode, **When** one request succeeds first, **Then** subsequent requests are rejected without session state corruption.

---

### User Story 2 - Prevent Device-Session Token Replay End to End (Priority: P1)

A device session token is accepted only when it is valid, current, and non-replayed, and replayed or stale tokens are rejected across polling, progress updates, and token refresh operations.

**Why this priority**: Replay resistance protects core imaging controls from token theft, reuse, and race conditions that could alter session state or leak download access.

**Independent Test**: Can be fully tested by replaying previously valid tokens across all device-session authenticated operations and verifying deterministic rejection with no state mutation.

**Acceptance Scenarios**:

1. **Given** a valid active token presented for the intended session context, **When** the request is submitted, **Then** the request is accepted and processed normally.
2. **Given** a previously used or revoked token, **When** it is replayed, **Then** the system rejects the request and emits a replay security event.
3. **Given** a token bound to one session context, **When** it is presented for another session context, **Then** the request is rejected and no data is disclosed.
4. **Given** benign retry behavior caused by transient network conditions, **When** retries occur within allowed semantics, **Then** valid progress is preserved without replay bypass.

---

### User Story 3 - Enforce Sensitive-Data Redaction in Logging and Telemetry (Priority: P1)

Operators and maintainers can diagnose failures using structured logs and telemetry that preserve traceability while never exposing passcodes, device-session tokens, SAS token URLs, certificate private material, or equivalent secrets.

**Why this priority**: Observability is required for operations, but leaked secrets in logs create long-lived compromise risk and incident expansion.

**Independent Test**: Can be fully tested by executing representative end-to-end workflows and scanning generated logs and telemetry payloads for prohibited sensitive values.

**Acceptance Scenarios**:

1. **Given** successful and failed imaging flows, **When** logs and telemetry are emitted, **Then** prohibited sensitive fields are consistently redacted or omitted.
2. **Given** an unexpected exception containing request context, **When** error details are recorded, **Then** redaction policies are applied before persistence.
3. **Given** support diagnostics that require correlation, **When** operators review telemetry, **Then** non-sensitive correlation identifiers are available without exposing secrets.
4. **Given** security validation jobs in CI, **When** a prohibited sensitive pattern appears in output, **Then** the job fails and blocks merge.

---

### Edge Cases

- What happens when distributed passcode abuse attempts originate from multiple network origins against a single session? (Expected: composite fingerprinting key detects and blocks the distributed attack regardless of source count; legitimate single-source coupling is unaffected)
- What happens when replay attempts occur near token expiration boundaries with clock skew? (Expected: a configurable clock skew tolerance window is applied so that requests arriving within the tolerance window after nominal expiry are accepted while genuine replays are rejected)
- What happens when a retry from a legitimate client arrives after a temporary lockout starts? (Expected: retry is queued or receives backoff guidance; no silent discard; the lockout window is respected and the attempt is logged)
- What happens when logs include nested exception payloads with serialized request bodies? (Expected: redaction engine traverses nested structures and removes all prohibited fields before persistence)
- What happens when concurrent updates race between replay detection and session lifecycle transitions? (Expected: atomic state operations ensure no race window where a token is simultaneously valid and terminal-revoked)
- What happens when a device-session token is validated within the clock skew tolerance window immediately after nominal expiry? (Expected: the token is accepted if within the tolerance window and the operation is logged with a skew indicator for monitoring)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST enforce passcode abuse controls on session coupling paths, including configurable attempt thresholds, time windows, and temporary backoff or lockout behavior. Abuse counting MUST be evaluated on a composite key combining the target session identifier with a non-personally-identifiable source context hash so that distributed multi-origin attacks against the same session are detected and blocked regardless of the number of distinct originating sources.
- **FR-002**: The system MUST reject expired, consumed, invalid, or abuse-blocked passcode submissions with safe error responses that do not disclose sensitive internals.
- **FR-003**: The system MUST ensure passcode coupling remains one-time and race-safe under concurrent request conditions.
- **FR-004**: The system MUST emit structured audit events for passcode abuse decisions, including source context, outcome category, and correlation identifiers.
- **FR-005**: The system MUST validate device-session token freshness and replay status for all device-session authenticated operations.
- **FR-006**: The system MUST reject replayed, revoked, or context-mismatched device-session tokens without mutating session state.
- **FR-007**: The system MUST preserve valid client retry behavior for transient network failures while still preventing token replay abuse. A valid retry is defined as a repeat of an identical operation carrying the same client-provided idempotency key or request nonce within an approved time window; the replay guard MUST classify such retries as idempotent and permit them without treating them as replay abuse. Any request presenting a token without a matching idempotency context that duplicates the semantic effect of a prior completed operation MUST be classified as replay and rejected.
- **FR-008**: The system MUST generate security telemetry for token replay detections and repeated token validation failures.
- **FR-009**: The system MUST define and enforce a prohibited-sensitive-fields logging policy. The following field categories are explicitly prohibited in all logs, telemetry, and exception output: passcode values; device-session bearer tokens; SAS token query strings including `sig` and `sv` parameters and full SAS URLs; certificate PFX bytes and private key material; and any field whose name matches or contains `token`, `secret`, `key`, `password`, `pfx`, `sig`, `credential`, `authorization`, or `bearer` in request bodies, response bodies, headers, or exception context objects. **Scope clarification**: this policy applies to all four server components (DeviceGatewayApi, OperatorApi, ImagingCoreApi, and Cloud Imaging Portal backend). WPF local log files produced by the Cloud Imaging Client and Cloud Imaging Media Builder (governed by feature 001 FR-066) are **explicitly out of scope** for this policy because they are local files never transmitted to a central sink; however, those components MUST NOT log passcode values or device-session token values as a matter of principle consistent with the intent of this requirement.
- **FR-010**: The system MUST apply redaction or omission policies before log and telemetry persistence for both normal events and exception paths.
- **FR-011**: The system MUST maintain actionable observability by emitting non-sensitive correlation metadata for end-to-end troubleshooting.
- **FR-012**: The system MUST include automated validation that fails CI when prohibited sensitive patterns appear in logs, traces, or diagnostics.
- **FR-013**: Security hardening controls MUST be configurable through deployment-safe parameters with secure defaults and no hardcoded environment secrets.
- **FR-014**: The system MUST expose administrative observability for abuse controls and replay defenses, including counts of blocked attempts and replay rejections. This observability MUST be accessible exclusively to principals holding the `CloudImaging.Administrator` role and MUST NOT be available to unauthenticated callers or principals holding only the `CloudImaging.Technician` role.
- **FR-015**: The system MUST preserve existing authorized end-to-end imaging workflows while introducing these security protections.
- **FR-016**: The system MUST revoke all active device-session tokens associated with a session immediately when that session transitions to a terminal state (SessionCompleted, SessionFailed, or SessionNotAuthorized). Revoked tokens MUST be rejected on the next validation attempt without waiting for natural token expiry, and a security telemetry event MUST be emitted on any attempt to use a token whose session has reached a terminal state.

### Key Entities *(include if feature involves data)*

- **PasscodeAbusePolicy**: Defines allowed attempts, evaluation window, temporary lockout duration, and response behavior for coupling abuse protection.
- **PasscodeAbuseEvent**: Records passcode validation outcomes, abuse-rule triggers, source context, timestamp, and correlation identifier.
- **DeviceSessionTokenValidationRecord**: Captures device-session token validation result, replay status, session-context match result, and evaluation timestamp.
- **SecurityRedactionPolicy**: Defines prohibited sensitive fields and required redaction behavior across logs, traces, and exception output.
- **SecurityTelemetryEvent**: Represents structured security events for replay detection, abuse control actions, and redaction enforcement outcomes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In abuse simulation tests, 100% of passcode brute-force attempts beyond policy thresholds are blocked while legitimate coupling remains successful.
- **SC-002**: In replay simulation tests, 100% of replayed or context-mismatched device-session tokens are rejected with no unintended session state changes.
- **SC-003**: In automated observability scans of representative workflows, 0 prohibited sensitive values appear in logs, traces, or exception payloads.
- **SC-004**: Security telemetry for abuse controls and replay detection is available for 100% of blocked security decisions with valid correlation identifiers.
- **SC-005**: End-to-end authorized imaging flows remain operational with no more than a 10% increase in coupling and polling latency at p95 under baseline load.
- **SC-006**: In distributed multi-origin abuse simulation tests, 100% of coordinated brute-force attempts across multiple source contexts targeting the same session are blocked by the composite fingerprinting policy while legitimate single-source coupling within the same test run succeeds.

## Assumptions

- Existing deployment boundaries and ingress patterns remain unchanged, and this refinement hardens current flows without redesigning core architecture.
- Operational teams need actionable diagnostics and will rely on correlation metadata rather than secret-bearing payloads.
- Security thresholds are deployment-configurable to accommodate different tenant risk profiles while preserving secure defaults.
- Existing role and identity models remain in place and are extended only by these additional hardening controls.
- The organization will run automated security validation in CI for every merge candidate.

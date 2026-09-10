# Tasks: Security Hardening Refinement

**Input**: Design documents from `/specs/002-security-hardening-refinement/`

**Prerequisites**: plan.md (required), spec.md (required), data-model.md, contracts/, research.md, quickstart.md

**Tests**: Included. Constitution mandates test-first development for all non-trivial security logic.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no unmet dependencies)
- **[Story]**: User story label (`[US1]`, `[US2]`, `[US3]`)
- Every task includes an exact file path

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Initialize security-hardening scaffolding, shared configuration, and CI baseline for all three workstreams.

- [ ] T001 Create directory scaffolding for security hardening in src/CloudImaging.Contracts/Security/, src/CloudImaging.DeviceGatewayApi/Security/, src/CloudImaging.DeviceGatewayApi/Middleware/Security/, src/CloudImaging.OperatorApi/Security/, src/CloudImaging.OperatorApi/Middleware/Security/, src/CloudImaging.ImagingCoreApi/Security/, src/CloudImaging.ImagingCoreApi/Middleware/Security/, src/CloudImaging.ImagingCoreApi/Repositories/Security/, src/CloudImaging.ImagingCoreApi/Functions/Security/, src/cloud-imaging-portal/server/src/security/, and tests/security/
- [ ] T002 [P] Add deployment parameter templates for passcode abuse thresholds, replay skew tolerance, and redaction policy defaults in src/deploy/parameters/security-hardening.parameters.json and src/deploy/scripts/security-hardening-settings.ps1
- [ ] T003 [P] Add CI step stubs for security contract tests and log-leakage scan gates in .github/workflows/ci.yml

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared security entities, repositories, telemetry infrastructure, redaction primitives, safe-error contracts, and startup validation, required by all three user stories.

**CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Implement shared security entity contracts in src/CloudImaging.Contracts/Security/PasscodeAbusePolicy.cs, src/CloudImaging.Contracts/Security/PasscodeAbuseEvent.cs, src/CloudImaging.Contracts/Security/PasscodeAttemptCounter.cs, src/CloudImaging.Contracts/Security/DeviceSessionTokenValidationRecord.cs, src/CloudImaging.Contracts/Security/TokenRequestFingerprint.cs, src/CloudImaging.Contracts/Security/SecurityRedactionPolicy.cs, and src/CloudImaging.Contracts/Security/SecurityTelemetryEvent.cs; include all fields and enums from data-model.md; add SecurityErrorResponse.cs with correlationId, errorCode, and retryAfterSeconds fields per api-overview.md error taxonomy
- [ ] T005 [P] Implement Table Storage repositories for all security entities in src/CloudImaging.ImagingCoreApi/Repositories/Security/PasscodeAbusePolicyRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/Security/PasscodeAttemptCounterRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/Security/PasscodeAbuseEventRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/Security/SessionTokenValidationRepository.cs, src/CloudImaging.ImagingCoreApi/Repositories/Security/TokenRequestFingerprintRepository.cs, and src/CloudImaging.ImagingCoreApi/Repositories/Security/SecurityRedactionPolicyRepository.cs; use partition/row key layout from data-model.md
- [ ] T006 [P] Implement security telemetry emitter and cross-service correlation helpers for DeviceGatewayApi, ImagingCoreApi, and OperatorApi in src/CloudImaging.DeviceGatewayApi/Security/SecurityTelemetryEmitter.cs, src/CloudImaging.ImagingCoreApi/Security/SecurityTelemetryEmitter.cs, src/CloudImaging.OperatorApi/Security/SecurityTelemetryEmitter.cs, and src/cloud-imaging-portal/server/src/security/securityTelemetry.ts; emit SecurityTelemetryEvent with eventType, severity, sessionId (when available), sourceContext, and correlationId (FR-004, FR-008); **PREREQUISITE NOTE**: SecurityTelemetryEmitter for .NET components MUST use the Application Insights SDK client registered by feature 001 task T025a (src/CloudImaging.DeviceGatewayApi/Program.cs, src/CloudImaging.OperatorApi/Program.cs, src/CloudImaging.ImagingCoreApi/Program.cs); if feature 002 is implemented before 001 T025a is complete, T006 MUST bootstrap its own Application Insights SDK registration to avoid a missing sink at runtime
- [ ] T007 [P] Implement cross-service redaction engine and prohibited-pattern policy loader for DeviceGatewayApi, OperatorApi, ImagingCoreApi, and portal backend in src/CloudImaging.DeviceGatewayApi/Security/Redaction/RedactionEngine.cs, src/CloudImaging.ImagingCoreApi/Security/Redaction/RedactionEngine.cs, src/CloudImaging.OperatorApi/Security/Redaction/RedactionEngine.cs, and src/cloud-imaging-portal/server/src/security/redactionEngine.ts; the engine MUST traverse nested structures and drop or mask any field matching the FR-009 prohibited name/pattern catalog
- [ ] T008 [P] Implement safe-error response mapping for all security-rejection codes in src/CloudImaging.Contracts/Security/SecurityErrorResponse.cs and src/cloud-imaging-portal/server/src/security/securityErrorMapping.ts; map PASSCODE_ATTEMPT_BLOCKED, PASSCODE_INVALID, TOKEN_REPLAY_REJECTED, TOKEN_CONTEXT_MISMATCH, TOKEN_REVOKED_OR_EXPIRED, and SECURITY_POLICY_VIOLATION to correct HTTP codes per api-overview.md error taxonomy; ensure response payloads never include passcodes, tokens, SAS URLs, or private key material (FR-002, FR-006); **ALIGNMENT NOTE**: SecurityErrorResponse MUST be implemented as a RFC 7807 ProblemDetails extension: include standard `type`, `title`, `status`, and `detail` fields alongside the security-specific `errorCode`, `correlationId`, and `retryAfterSeconds` fields; this ensures security rejection responses are structurally consistent with the ProblemDetailsMiddleware established by feature 001 task T017 and are not a parallel response format
- [ ] T009 [P] Add foundational unit tests for repository CRUD, redaction engine policy loading, and safe-error mapping in tests/CloudImaging.ImagingCoreApi.Tests/Unit/SecurityRepositoriesTests.cs and tests/cloud-imaging-portal/server/security/redaction-policy.test.ts; follow Red-Green-Refactor -- tests MUST fail before T004/T007 implementation begins
- [ ] T009a [P] Implement startup validation guards that reject application launch when required security deployment parameters are absent or contain insecure placeholder values; add unit tests for both the startup-failure and startup-success cases (FR-013) in src/CloudImaging.DeviceGatewayApi/Startup/SecurityStartupValidator.cs, src/CloudImaging.ImagingCoreApi/Startup/SecurityStartupValidator.cs, src/cloud-imaging-portal/server/src/security/startupValidator.ts, and tests/CloudImaging.ImagingCoreApi.Tests/Unit/SecurityStartupValidatorTests.cs

**Checkpoint**: Foundation complete; all three user story phases may begin in parallel.

---

## Phase 3: User Story 1 - Harden Session Coupling Against Passcode Abuse (Priority: P1) 🎯 MVP

**Goal**: Protect session coupling from brute-force, rapid-retry, and distributed multi-origin abuse while preserving legitimate operator coupling at normal latency.

**Independent Test**: Execute high-volume single-source and multi-origin invalid passcode attempts against coupling endpoints; verify lockout/backoff behavior, abuse audit events, safe-only error responses, and legitimate coupling success outside lockout windows. Validate SC-001 and SC-006.

### Tests for User Story 1

- [ ] T010 [P] [US1] Add Imaging Core API contract tests for passcode abuse policy evaluation: single-source threshold breach triggers lockout, composite session+source-hash fingerprinting blocks multi-origin attacks on same session, legitimate attempt outside lockout succeeds, and passcode is consumed at most once (FR-001, FR-003, SC-001, SC-006) in tests/CloudImaging.ImagingCoreApi.Tests/Contracts/PasscodeAbusePolicyContractTests.cs
- [ ] T010a [P] [US1] Add Device Gateway API contract tests for abuse guard middleware: requests blocked by policy return HTTP 429 with PASSCODE_ATTEMPT_BLOCKED error code and retryAfterSeconds, requests for invalid/expired/consumed passcodes return HTTP 401 with PASSCODE_INVALID, responses contain correlationId and no sensitive internals (FR-001, FR-002, api-overview.md) in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/PasscodeAbuseGuardContractTests.cs
- [ ] T010b [P] [US1] Add Operator API contract tests for passcode abuse proxy behavior: abuse-blocked and lockout responses are correctly forwarded from Imaging Core API through Operator API to portal backend without exposing sensitive internals; CloudImaging.PortalAccess service-role enforcement is preserved under abuse conditions (FR-001, FR-002) in tests/CloudImaging.OperatorApi.Tests/Contracts/PasscodeAbuseProxyContractTests.cs
- [ ] T011 [P] [US1] Add Imaging Core API integration tests for race-safe one-time passcode consumption: concurrent coupling requests for the same passcode succeed exactly once; all subsequent requests receive 409; no session state corruption occurs (FR-003) in tests/CloudImaging.ImagingCoreApi.Tests/Integration/PasscodeConsumeRaceIntegrationTests.cs
- [ ] T012 [P] [US1] Add portal backend integration tests for safe passcode rejection responses: invalid/expired/blocked passcode submissions receive safe non-disclosing error bodies; abuse telemetry events are emitted for each blocked attempt with correlationId (FR-002, FR-004) in tests/cloud-imaging-portal/server/security/passcode-abuse.test.ts
- [ ] T013 [P] [US1] Add load-style single-source abuse simulation test: validate that 100% of attempts beyond the policy threshold are blocked and that legitimate coupling succeeds throughout without latency regression beyond SC-005 tolerance in tests/security/passcode-abuse-simulation.test.ps1
- [ ] T013a [P] [US1] Add distributed multi-origin abuse simulation test: simulate coordinated brute-force attempts from multiple distinct source fingerprints targeting the same session; verify composite session+source-hash policy blocks the attack across source rotations; verify legitimate single-source coupling within the same window succeeds unaffected; validate SC-006 (FR-001) in tests/security/multi-origin-abuse-simulation.test.ps1

### Implementation for User Story 1

- [ ] T014 [US1] Implement passcode abuse policy evaluator with composite session+source-hash fingerprinting key: reads PasscodeAbusePolicy via T005 repository, evaluates attempt count within sliding window, applies Backoff or Lockout response mode, and delegates to PasscodeAttemptTracker for state updates (FR-001, FR-013) in src/CloudImaging.ImagingCoreApi/Security/PasscodeAbusePolicyEvaluator.cs
- [ ] T015 [P] [US1] Implement passcode attempt counter and lockout state transitions: atomic increment of PasscodeAttemptCounter via optimistic concurrency; sets lockoutUntil on threshold breach; resets window on natural expiry (FR-001, FR-003) in src/CloudImaging.ImagingCoreApi/Security/PasscodeAttemptTracker.cs
- [ ] T016 [US1] Implement coupling endpoint abuse enforcement and safe response mapping in Imaging Core API: integrate T014 evaluator into the coupling flow; return SecurityErrorResponse with correct error code and retryAfterSeconds; invalidate passcode on success; persist rejection as PasscodeAbuseEvent via T005 repository (FR-001, FR-002, FR-003) in src/CloudImaging.ImagingCoreApi/Functions/Security/CoupleSessionSecurityFunction.cs
- [ ] T017 [P] [US1] Implement Device Gateway API coupling guard middleware: proxy abuse-policy outcome from Imaging Core API upstream; return HTTP 429/401 with safe SecurityErrorResponse body; register before session token validation middleware so blocked requests are rejected before token processing (FR-001, FR-002) in src/CloudImaging.DeviceGatewayApi/Middleware/Security/PasscodeAbuseGuardMiddleware.cs
- [ ] T018 [US1] Implement portal backend passcode coupling route hardening: translate Imaging Core API abuse outcomes to non-disclosing HTTP responses for the browser; apply T007 redaction to any proxied error body before forwarding to client (FR-002) in src/cloud-imaging-portal/server/src/routes/security/couple-session.ts and src/cloud-imaging-portal/server/src/security/passcodeAbuseHandler.ts
- [ ] T019 [US1] Emit structured passcode abuse audit events and security telemetry: publish PasscodeAbuseEvent to Table Storage via T005 repository and emit SecurityTelemetryEvent of type PasscodeAbuseBlocked via T006 emitter with sessionId, sourceFingerprintHash (non-sensitive), outcomeCategory, reasonCode, occurredAt, and correlationId (FR-004) in src/CloudImaging.ImagingCoreApi/Security/PasscodeAbuseAuditPublisher.cs

**Checkpoint**: US1 independently functional and testable. SC-001 and SC-006 validatable in isolation.

---

## Phase 4: User Story 2 - Prevent Device-Session Token Replay End to End (Priority: P1)

**Goal**: Reject replayed, revoked, stale, and context-mismatched tokens across all device-session authenticated operations; distinguish idempotent retries from genuine replay abuse; revoke tokens immediately on terminal session state; apply clock skew tolerance.

**Independent Test**: Replay valid tokens across polling, progress, and refresh endpoints; present context-mismatched tokens; attempt use of terminal-session tokens; verify deterministic rejection with no state mutation; verify idempotent retries succeed; validate SC-002 and SC-004.

### Tests for User Story 2

- [ ] T020 [P] [US2] Add Device Gateway API contract tests for replay rejection: previously used token is rejected with HTTP 401 TOKEN_REPLAY_REJECTED; context-mismatched token is rejected with HTTP 403 TOKEN_CONTEXT_MISMATCH; terminal-session token is rejected with HTTP 401 TOKEN_REVOKED_OR_EXPIRED; all rejections include correlationId and no sensitive data (FR-005, FR-006, FR-016) in tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/TokenReplayContractTests.cs
- [ ] T020a [P] [US2] Add Imaging Core API unit tests for token revocation on session terminal state transitions: session reaching SessionCompleted, SessionFailed, or SessionNotAuthorized immediately marks all associated DeviceSessionTokenValidationRecord entries as revoked; subsequent token validation for that session returns RejectedRevoked; a SecurityTelemetryEvent of type TokenReplayRejected is emitted on any use-after-terminal attempt (FR-016) in tests/CloudImaging.ImagingCoreApi.Tests/Unit/SessionTerminalTokenRevocationTests.cs
- [ ] T020b [P] [US2] Add Imaging Core API integration tests for clock skew tolerance boundary: token presented within the configurable skew window (default 30 s) after nominal expiry is accepted and a skew observation is logged; same token presented outside the window is rejected as RejectedExpired; skew window is read from deployment parameter and changing it takes effect without restart (FR-005, FR-013) in tests/CloudImaging.ImagingCoreApi.Tests/Integration/TokenExpiryClockSkewIntegrationTests.cs
- [ ] T021 [P] [US2] Add Imaging Core API integration tests for token replay tracking and context mismatch rejection: replayed token is detected after first use and all subsequent uses are rejected without state mutation; token bound to sessionA is rejected when presented for sessionB with no data disclosure (FR-005, FR-006) in tests/CloudImaging.ImagingCoreApi.Tests/Integration/TokenReplayPreventionIntegrationTests.cs
- [ ] T022 [P] [US2] Add Imaging Core API integration tests for idempotent retry acceptance: a request carrying the same idempotency key/nonce as a prior accepted request within the approved retry window is accepted as a retry and not flagged as replay; a request with a new nonce for the same semantic operation is flagged as replay (FR-007) in tests/CloudImaging.ImagingCoreApi.Tests/Integration/TokenRetrySemanticsIntegrationTests.cs
- [ ] T023 [P] [US2] Add security telemetry completeness tests: every replay rejection, context mismatch rejection, and terminal-session token use emits a SecurityTelemetryEvent with non-null correlationId, correct eventType, and no sensitive token values in payload (FR-008, SC-004) in tests/security/token-replay-telemetry.test.ps1

### Implementation for User Story 2

- [ ] T024 [US2] Implement session token validation and replay decision engine in Imaging Core API: validates freshness, checks DeviceSessionTokenValidationRecord for isReplayDetected/revokedAt, matches token to session context, and classifies outcome as Accepted, RejectedExpired, RejectedRevoked, RejectedReplay, or RejectedContextMismatch; applies clock skew tolerance window from deployment parameter (FR-005, FR-006, FR-013) in src/CloudImaging.ImagingCoreApi/Security/SessionTokenReplayGuard.cs
- [ ] T025 [P] [US2] Implement token request fingerprint store and idempotency-aware retry classifier: on first request records TokenRequestFingerprint with tokenId, requestNonce, operationName; on subsequent request with same nonce within window returns AcceptedIdempotentRetry; on request without matching nonce for same semantic operation returns RejectedReplay (FR-007) in src/CloudImaging.ImagingCoreApi/Security/TokenRequestFingerprintService.cs
- [ ] T026 [US2] Implement replay enforcement middleware for Device Gateway API: integrates with T024 replay guard via service call to Imaging Core API; maps guard outcome to HTTP response using T008 safe-error mapping; registers on all device-session authenticated endpoints before any business logic (FR-005, FR-006) in src/CloudImaging.DeviceGatewayApi/Middleware/Security/TokenReplayGuardMiddleware.cs
- [ ] T027 [P] [US2] Wire replay guard outcomes to Device Gateway API endpoint handlers for session polling, progress reporting, and SAS token refresh so each handler receives a pre-validated context; update function registrations to apply T026 middleware in src/CloudImaging.DeviceGatewayApi/Functions/Security/SessionStatusSecurityFunction.cs, src/CloudImaging.DeviceGatewayApi/Functions/Security/ReportProgressSecurityFunction.cs, and src/CloudImaging.DeviceGatewayApi/Functions/Security/RefreshSasTokenSecurityFunction.cs
- [ ] T028 [US2] Emit token replay security events and administrative counters: publish SecurityTelemetryEvent of type TokenReplayRejected via T006 emitter for every replay detection, context mismatch, and terminal-session use; update administrative counter records accessible to CloudImaging.Administrator role (FR-008, FR-014) in src/CloudImaging.ImagingCoreApi/Security/TokenReplayAuditPublisher.cs and src/CloudImaging.DeviceGatewayApi/Security/SecurityTelemetryEmitter.cs
- [ ] T028a [US2] Implement session terminal-state token revocation: on any session transition to SessionCompleted, SessionFailed, or SessionNotAuthorized, atomically mark all associated DeviceSessionTokenValidationRecord entries as revoked (revokedAt = now) via T005 repository; integrate with T024 replay guard so subsequent calls return RejectedRevoked without an additional Table Storage lookup (FR-016); **CROSS-FEATURE PREREQUISITE**: this task hooks into the session state machine implemented in feature 001 tasks T030 (ImagingCore create-session), T048 (ImagingCore progress endpoint and state transitions), and T134 (DevicePreFlightAuthorizationService); T028a MUST NOT be considered complete until the 001 state machine has been verified to fire terminal-state transitions that the T028a hook can intercept in src/CloudImaging.ImagingCoreApi/Security/SessionTerminalTokenRevoker.cs and src/CloudImaging.ImagingCoreApi/Security/SessionTokenReplayGuard.cs
- [ ] T028b [P] [US2] Implement clock skew tolerance in replay guard token expiry check: add configurable skew window (default: 30 seconds, read from deployment parameter) to T024 replay guard; tokens within the window after nominal expiresAt are accepted and a skew indicator is recorded in telemetry; tokens outside the window are rejected as RejectedExpired; skew window change takes effect immediately without restart (FR-005, FR-013) in src/CloudImaging.ImagingCoreApi/Security/SessionTokenReplayGuard.cs

**Checkpoint**: US2 independently functional and testable. SC-002 and SC-004 validatable in isolation.

---

## Phase 5: User Story 3 - Enforce Sensitive-Data Redaction in Logging and Telemetry (Priority: P1)

**Goal**: Ensure all four server components (DeviceGatewayApi, OperatorApi, ImagingCoreApi, portal backend) emit diagnostically useful logs and telemetry with zero prohibited sensitive values; enforce via automated CI leakage scans.

**Independent Test**: Run representative end-to-end workflows across all four components; scan all emitted logs, traces, and exception payloads for prohibited patterns; verify CI scan fails on introduced leakage; verify correlation IDs survive redaction and remain traceable. Validate SC-003.

### Tests for User Story 3

- [ ] T029 [P] [US3] Add redaction engine unit tests for all four server components: verify request body, response body, header, and exception payload traversal removes all FR-009 prohibited field categories; verify nested exception structures are fully traversed; verify correlationId is preserved after redaction; follow Red-Green-Refactor (FR-009, FR-010, FR-011) in tests/CloudImaging.DeviceGatewayApi.Tests/Unit/RedactionEngineTests.cs, tests/CloudImaging.ImagingCoreApi.Tests/Unit/RedactionEngineTests.cs, and tests/CloudImaging.OperatorApi.Tests/Unit/RedactionEngineTests.cs
- [ ] T030 [P] [US3] Add portal backend redaction integration tests: verify structured log output for normal and error request paths contains no prohibited field values; verify redaction is applied before errorHandler persistence; verify correlationId is present in all log entries (FR-009, FR-010, FR-011) in tests/cloud-imaging-portal/server/security/log-redaction.test.ts
- [ ] T031 [P] [US3] Add CI leakage scan tests: scan DeviceGatewayApi, OperatorApi, ImagingCoreApi, and portal backend log fixtures for each prohibited pattern from FR-009 (token, secret, key, password, pfx, sig, credential, authorization, bearer, full SAS URL query strings); fail test run on any match (FR-012) in tests/security/log-leakage-scan.test.ps1
- [ ] T032 [P] [US3] Add end-to-end diagnostic correlation tests: execute a complete session coupling and imaging flow; confirm correlationIds in DeviceGatewayApi, OperatorApi, ImagingCoreApi, and portal backend logs trace the same logical operation end-to-end without any sensitive value appearing in any correlated log entry (FR-011, SC-003) in tests/security/redaction-correlation.test.ps1

### Implementation for User Story 3

- [ ] T033 [US3] Implement Device Gateway API redaction middleware: register as the outermost middleware layer in the pipeline so request/response/exception context is redacted before reaching any structured logger or Application Insights sink; use T007 RedactionEngine; apply prohibited-pattern policy from T005 SecurityRedactionPolicyRepository (FR-009, FR-010) in src/CloudImaging.DeviceGatewayApi/Middleware/Security/RedactionMiddleware.cs
- [ ] T034 [P] [US3] Implement Imaging Core API redaction middleware: same pattern as T033; register outermost in isolated-worker Function middleware pipeline; ensure exception-path logging via ILogger is also intercepted (FR-009, FR-010) in src/CloudImaging.ImagingCoreApi/Middleware/Security/RedactionMiddleware.cs
- [ ] T034a [P] [US3] Implement Operator API redaction middleware: apply T007 RedactionEngine and SecurityRedactionPolicy to Operator API structured logs, outbound telemetry sinks, and exception paths; register outermost in the Function pipeline; depends on T007 (FR-009, FR-010) in src/CloudImaging.OperatorApi/Middleware/Security/RedactionMiddleware.cs
- [ ] T035 [P] [US3] Implement portal backend redaction hooks: integrate T007 redaction engine into the Express logger middleware and into the centralized errorHandler so all structured log calls and uncaught exception payloads pass through the redaction filter before writing to Application Insights or console (FR-009, FR-010) in src/cloud-imaging-portal/server/src/security/redactionMiddleware.ts and src/cloud-imaging-portal/server/src/middleware/errorHandler.ts
- [ ] T036 [US3] Implement prohibited-sensitive-fields pattern catalog with the exact enumeration from FR-009: field names (token, secret, key, password, pfx, sig, credential, authorization, bearer) and structured field names (passcode, deviceSessionToken, sasTokenUrl, pfxBytes, privateKey); SAS URL pattern (query string containing sig= or sv=); catalog loaded from T005 SecurityRedactionPolicyRepository at startup and cached; updates take effect on next request cycle without restart (FR-009, FR-013) in src/CloudImaging.ImagingCoreApi/Security/Redaction/ProhibitedPatternCatalog.cs and src/cloud-imaging-portal/server/src/security/prohibitedPatternCatalog.ts
- [ ] T037 [US3] Implement CI enforcement step that runs T031 leakage scan on every PR and fails the build when any prohibited pattern appears in log fixtures or telemetry output captured during test runs; wire into the Security Leakage Gate in ci.yml (FR-012) in .github/workflows/ci.yml and src/deploy/scripts/security-log-scan.ps1
- [ ] T038 [US3] Implement non-sensitive correlation enrichment across all four server components: every log entry, telemetry event, and security audit record includes a correlationId derived from the incoming request trace context; enrichers MUST NOT include session token values or any FR-009 prohibited field in the correlation metadata (FR-011) in src/CloudImaging.DeviceGatewayApi/Security/CorrelationEnricher.cs, src/CloudImaging.ImagingCoreApi/Security/CorrelationEnricher.cs, src/CloudImaging.OperatorApi/Security/CorrelationEnricher.cs, and src/cloud-imaging-portal/server/src/security/correlationEnricher.ts

**Checkpoint**: US3 independently functional and testable. SC-003 validatable in isolation.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Staging validation, operational readiness, latency regression, and security review.

- [ ] T039 [P] Update operations guidance for passcode abuse handling procedures, replay incident response runbook, and redaction policy management in docs/operations-security-hardening.md
- [ ] T040 [P] Add deployment parameter documentation covering all security-hardening defaults (abuse thresholds, replay skew window, redaction policy), tuning guidance, and upgrade-path notes in specs/002-security-hardening-refinement/quickstart.md
- [ ] T041 [P] Add dedicated staging smoke validation tests for all three security user stories: abuse control, replay rejection, and redaction leakage scan run against a deployed staging environment in tests/validation/security-hardening-staging-smoke.ps1 and docs/validation-security-hardening-staging.md
- [ ] T042 [P] Run full quickstart validation and capture evidence for SC-001 through SC-006 in specs/002-security-hardening-refinement/quickstart.md
- [ ] T043 Complete security review and threat-model delta write-up covering attack surfaces addressed and residual risks in docs/security-threat-model-delta-002.md
- [ ] T044 [P] Configure and execute security integration tests against real Azure endpoints using short-lived managed identities in an isolated CI staging resource group: T013 abuse simulation, T023 replay telemetry, and T031 log-leakage scan MUST all run against real Function App and Table Storage endpoints with no HTTP-layer mocks; document the managed identity configuration and isolation approach in docs/security-ci-managed-identity-setup.md (constitution Principle III)
- [ ] T045 [P] Add security middleware latency regression tests: benchmark p95 coupling and polling latency on the hardened path against a pre-hardening baseline snapshot; fail the job if p95 increases exceed 10% on either path; document baseline and results in docs/validation-security-latency-baseline.md (SC-005) in tests/performance/security-middleware-latency-regression.k6.js

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup; blocks all user stories.
- **User Stories (Phase 3, 4, 5)**: Depend on Foundational completion; can run in parallel across stories.
- **Polish (Phase 6)**: Depends on all user stories complete.

### User Story Dependencies

- **US1 (P1)**: Starts after Foundational. No inter-story dependency.
- **US2 (P1)**: Starts after Foundational. No inter-story dependency; can run parallel with US1.
- **US3 (P1)**: Starts after Foundational. T034a depends on T007 (Foundational). Can run parallel with US1 and US2.

### Within Each User Story

- Tests MUST be written and confirmed failing before implementation begins (Constitution Principle II).
- Security entity/repository layer precedes policy evaluation and middleware.
- Middleware precedes endpoint handler wiring.
- Telemetry and audit event emission must be enabled before story sign-off.
- T028a (terminal revocation) and T028b (clock skew) both extend T024 (replay guard) in the same file; both must be complete before US2 is signed off.

### Parallel Opportunities

- Phase 1: T002, T003 parallel after T001.
- Phase 2: T005, T006, T007, T008, T009, T009a all parallel after T004.
- US1 tests T010 through T013a all parallel.
- US2 tests T020 through T023 all parallel.
- US3 tests T029 through T032 all parallel.
- US1, US2, US3 can be developed in parallel by separate contributors after Phase 2.
- Phase 6: T039, T040, T041, T042, T044, T045 all parallel after stories complete.

---

## Parallel Example: User Story 1

```bash
# All US1 tests can run concurrently
T010  T010a  T010b  T011  T012  T013  T013a

# Parallel-capable implementation after T014 baseline
T015  T017

# Sequential coupling enforcement chain
T014 -> T016 -> T018 -> T019
```

## Parallel Example: User Story 2

```bash
# All US2 tests can run concurrently
T020  T020a  T020b  T021  T022  T023

# Core replay engine must complete before middleware and wiring
T024 -> T026 -> T027 -> T028

# Fingerprint service parallel with T026 setup
T025  (parallel after T024)

# Extensions to T024 (extend replay guard after core is in place)
T024 -> T028a   # terminal-state revocation
T024 -> T028b   # clock skew tolerance
```

## Parallel Example: User Story 3

```bash
# All US3 tests can run concurrently
T029  T030  T031  T032

# Redaction implementation (T034a depends on T007 from Foundation)
T007 -> T034a
T033  T034  T034a  T035  (parallel once T007 is complete)

# Pattern catalog must exist before CI enforcement and correlation enrichment
T036 -> T037
T036 -> T038
```

---

## Implementation Strategy

### MVP First (US1 + US2)

1. Complete Phase 1 (Setup) and Phase 2 (Foundational).
2. Deliver US1 passcode abuse controls; validate SC-001 and SC-006.
3. Deliver US2 token replay prevention; validate SC-002 and SC-004.
4. Run staging smoke tests before expanding to US3.

### Incremental Delivery

1. Add US3 redaction enforcement after US1/US2 are stable; validate SC-003.
2. Execute Phase 6 operational readiness, latency regression (SC-005), and evidence capture (SC-001 through SC-006).
3. Promote after all six success criteria are validated in staging.

### Parallel Team Strategy

With two or three contributors:

1. Team completes Phase 1 + Phase 2 together.
2. Once Foundational is complete:
   - Developer A: US1 (passcode abuse)
   - Developer B: US2 (token replay)
   - Developer C: US3 (redaction)
3. Integrate through CI gates and staging validation.

---

## Notes

- [P] tasks target separate files and have no unmet dependencies at that point in the execution order.
- [USx] labels provide exact traceability from task to user story for validation reporting.
- All three user stories are independently completable, testable, and deliverable.
- Security telemetry and redaction enforcement are mandatory story completion criteria, not optional polish.
- The prohibited field catalog (T036) must precisely match FR-009 enumeration; any discrepancy between the catalog and the spec is a correctness defect.
- T028a (terminal revocation) and T028b (clock skew) both extend T024 in the same file; both MUST be implemented before US2 is considered complete.

# Research: Security Hardening Refinement

**Feature**: 002-security-hardening-refinement  
**Date**: 2026-07-15  
**Status**: Complete

## Decision 1: Enforce passcode abuse control with policy-driven adaptive lockouts

**Decision**: Introduce a configurable passcode abuse policy that evaluates attempts by session and source fingerprint (IP, user agent/device signature where available), using threshold-based lockout/backoff windows and one-time passcode consumption semantics.

**Rationale**:
- Meets FR-001 through FR-004 without changing current coupling flow.
- Mitigates brute-force and rapid retry abuse while preserving legitimate coupling.
- Keeps controls deployment-tunable through secure defaults and configuration parameters.

**Alternatives considered**:
- Permanent account/session lock after threshold breach: rejected because it creates high operational friction and recovery burden.
- Captcha/interactive challenge in coupling: rejected because WinPE/device workflows are non-interactive in this path.

## Decision 2: Use token family tracking + replay ledger for replay prevention

**Decision**: Add token validation records that track token family, token identifier, issuance/expiry, and replay status. Enforce context-binding checks (token -> session) on every device-session authenticated endpoint. Use idempotency semantics for safe retries and replay rejection events for abuse.

**Rationale**:
- Meets FR-005 through FR-008 with deterministic replay rejection.
- Supports benign retries (FR-007) by distinguishing duplicate safe retries from invalid replays.
- Keeps state mutation blocked for replayed, revoked, or mismatched tokens.

**Alternatives considered**:
- Stateless validation only (signature + expiry): rejected because it cannot reliably detect replay/revocation.
- Single-use access token for all operations: rejected because polling/progress workflows require repeated valid token use over a bounded lifetime.

## Decision 3: Centralized redaction pipeline before all persistence sinks

**Decision**: Implement a shared prohibited-sensitive-fields redaction policy and enforce it in one centralized pre-persistence logging/telemetry layer across APIs and exception handlers.

**Rationale**:
- Meets FR-009 through FR-011 by ensuring consistent behavior in normal and exception paths.
- Reduces risk of per-endpoint redaction drift.
- Preserves observability by emitting correlation metadata and safe error categories.

**Alternatives considered**:
- Ad hoc endpoint-level masking: rejected due to inconsistency and high omission risk.
- Post-persistence sanitization job: rejected because data is already exposed once written.

## Decision 4: Security leakage prevention is enforced by CI validation gates

**Decision**: Add CI jobs that run representative security test flows and fail on prohibited sensitive patterns in logs, traces, and diagnostics output.

**Rationale**:
- Directly satisfies FR-012 and SC-003.
- Converts redaction requirements into enforceable merge-blocking quality gates.
- Provides repeatable evidence of non-regression.

**Alternatives considered**:
- Manual periodic auditing only: rejected because it is non-deterministic and too late to prevent merge.

## Decision 5: Observability model uses structured event taxonomy

**Decision**: Define standardized event categories (`PasscodeAbuseBlocked`, `PasscodeValidationFailed`, `TokenReplayRejected`, `TokenContextMismatch`, `RedactionApplied`, `RedactionPolicyViolation`) with stable correlation identifiers and source context.

**Rationale**:
- Satisfies FR-004, FR-008, and FR-014.
- Enables dashboards/alerts without leaking sensitive payloads.
- Supports operational debugging and incident response.

**Alternatives considered**:
- Free-form text logs only: rejected due to poor machine analysis and inconsistent severity tagging.

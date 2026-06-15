<!--
SYNC IMPACT REPORT
==================
Version change  : 1.1.0 -> 1.2.0 (MINOR - stricter WPF threading and UI responsiveness policy)
Modified        : Principle IV (zero UI freeze rule, explicit multithreading requirements)
                  Development Workflow (added UI responsiveness gate, now 6 gates)
Added sections  : Project Architecture
Removed sections: N/A
Templates status:
  - .specify/templates/plan-template.md    : reviewed, no changes required
  - .specify/templates/spec-template.md    : reviewed, no changes required
  - .specify/templates/tasks-template.md   : reviewed, no changes required
Deferred TODOs  : none
-->

# CloudImaging Constitution

## Project Architecture

CloudImaging is a pure .NET 10 solution composed of four components that MUST
be designed, built, and deployed as a coherent system:

| Component | Runtime | Description |
|-----------|---------|-------------|
| **WPF Client** | .NET 10 / WinPE | Imaging client application running inside Windows PE on bare-metal devices. Operates under WinPE constraints: no persistent storage, limited memory, no full Windows shell. |
| **Function App - Imaging** | .NET 10 / Azure Functions v4 (isolated) | Handles imaging workflow orchestration: job dispatch, status tracking, and device lifecycle events. |
| **Function App - Backend** | .NET 10 / Azure Functions v4 (isolated) | Handles background processing: image management, blob operations, notification delivery, and scheduled tasks. |
| **Admin Portal** | ASP.NET Core 10 / Azure App Service | Operational and administrative web interface for IT administrators. Single App Service deployment. |

All four components target **net10.0** (or **net10.0-windows** for the WPF
client). Shared business logic and contracts MUST live in dedicated class
libraries referenced by each component - never duplicated.

## Core Principles

### I. Zero-Warning .NET Build Quality (NON-NEGOTIABLE)

Every change to .NET code MUST result in a build that produces zero compiler
errors, zero compiler warnings, and zero analyzer diagnostics (CSxxxx, CAxxxx,
IDExxxx, MSBuild warnings) in Release configuration.

**Forbidden practices - never use any of the following:**
- `#pragma warning disable` / `#pragma warning restore`
- `[SuppressMessage]` / `System.Diagnostics.CodeAnalysis.SuppressMessage`
- `<NoWarn>` or `<WarningsAsErrors>` modifications in `.csproj` files
- `GlobalSuppressions.cs` or any global suppression file
- `.editorconfig` entries used solely to disable rules
- Conditional compilation (`#if false`) or comments used to silence diagnostics
- Removing or commenting out code purely to eliminate a warning
- Excluding files or folders from the build to avoid diagnostics

**Required approach - always fix the root cause:**
- CS8618 (non-nullable field uninitialized): use `required`, proper constructor
  initialization, or correct nullable annotations with null guards.
- CS1998 (async lacks await): add `await` or remove `async` if not needed.
- CA2007 / CS4014: properly `await` tasks or use `.ConfigureAwait(false)`.
- Nullability warnings: add null checks; use null-forgiving operator only when
  the value is provably non-null by invariant.
- Performance / Roslyn analyzers: apply the recommended refactoring directly.

This principle has highest priority and overrides all other guidance when
dealing with .NET code, builds, or diagnostics.

### II. Test-First Development (NON-NEGOTIABLE)

Test-Driven Development is mandatory for all non-trivial logic.

- Tests MUST be written and reviewed before implementation begins.
- The Red-Green-Refactor cycle MUST be strictly enforced.
- No feature is considered complete until its acceptance tests pass.
- Unit test coverage MUST be >= 80% for all new code paths; coverage reports
  are generated and checked on every CI run.
- Tests MUST be deterministic: no random seeds, no time-dependent assertions
  without controlled clock injection, no network calls in unit tests.
- Test names MUST follow the pattern: `MethodOrScenario_StateUnderTest_ExpectedBehavior`.

### III. Integration & End-to-End Testing

Integration tests are required for all boundaries where components communicate.

- Every new HTTP/Function contract MUST have a corresponding contract test
  using `Microsoft.AspNetCore.Mvc.Testing` or the Azure Functions test host.
- WPF client logic that does not depend on the Windows shell MUST be covered
  by headless unit tests; UI interaction tests MUST use a WinPE-compatible
  test harness or be clearly marked as manual verification items in the spec.
- Cloud provisioning workflows MUST have end-to-end smoke tests executed in a
  dedicated Azure staging environment (separate subscription or resource group)
  before any merge to main.
- External service interactions (Azure APIs, Blob storage, Entra ID) MUST be
  tested against real endpoints in CI using short-lived managed identities and
  isolated resource groups - never mocked at the HTTP layer in integration tests.
- The Admin Portal MUST have integration tests covering all authenticated routes
  using `WebApplicationFactory<T>` with a test identity provider.

### IV. User Experience Consistency

All user-facing surfaces - the WPF imaging client, the Admin Portal, and any
in-process status/error output - MUST conform to a single, consistent style.

**WPF Client (WinPE)**
- The UI MUST function correctly in WinPE: no reliance on user profile, shell
  extensions, COM automation, or components absent from the WinPE image.
- Workflows MUST be linear and touch-friendly; no more than three clicks/taps
  to reach any primary action.
- The WPF client MUST be fully non-blocking on the UI thread. All I/O, network,
  file, cryptographic, and CPU-intensive operations MUST execute off the
  dispatcher thread via asynchronous patterns or background workers.
- UI thread work is limited to rendering, binding updates, and lightweight
  orchestration only; synchronous waits (`.Wait()`, `.Result`, blocking sleep,
  or long-running loops on dispatcher callbacks) are forbidden.
- Progress MUST be reported in real time via a visible progress indicator with
  no tolerated UI freeze window. The operator MUST always see active status
  updates while long-running operations execute.
- Error dialogs MUST include: what went wrong, likely cause, and a clear
  remediation instruction or support reference code.

**Admin Portal**
- The portal MUST follow a consistent component library (agreed per feature
  plan); ad-hoc inline styles are forbidden.
- All administrative actions with destructive or irreversible effects MUST
  require an explicit confirmation step.
- The portal MUST be accessible to WCAG 2.1 AA standard.

**All components**
- All user-facing strings MUST use ASCII-only punctuation; no Unicode em-dashes,
  smart quotes, or non-ASCII symbols.
- Breaking changes to any user-facing interface MUST be preceded by a
  deprecation notice in at least one prior release.

### V. Performance Requirements

Every feature touching the imaging pipeline MUST meet defined performance budgets.

- **End-to-end provisioning**: Cloud image round-trip (PXE/network boot to
  first-boot-complete of the provisioned OS) MUST complete within 15 minutes
  for a reference workload on standard-tier Azure compute.
- **WPF client startup**: The WPF client MUST reach its main window within
  5 seconds of process launch on hardware meeting the minimum WinPE spec
  (2 GB RAM, dual-core CPU).
- **Function App cold start**: Both Function Apps MUST respond to their first
  HTTP trigger within 3 seconds of a cold start in the Consumption or Flex
  Consumption plan.
- **Admin Portal**: All server-rendered page responses MUST be <= 500 ms at
  p95; all API calls from the portal MUST be <= 300 ms at p95 under a
  50-concurrent-user load.
- **Memory**: The WPF client MUST remain below 512 MB working set during
  normal imaging; Function App instances MUST remain below 256 MB RSS under
  steady-state load.
- Performance regressions > 10% from the recorded baseline MUST be treated as
  bugs and block merge until resolved.

## .NET Diagnostic Policy

This section elaborates the enforcement mechanism for Principle I.

- The project MUST set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in
  `Directory.Build.props` so the build itself rejects any warning.
- Roslyn analyzers (Microsoft.CodeAnalysis.NetAnalyzers) MUST be enabled at
  the `Recommended` rule set or stricter.
- All projects MUST target **net10.0**; the WPF client MUST target
  **net10.0-windows**. Deviating from .NET 10 requires a documented
  compatibility constraint approved in the feature plan.
- Nullable reference types MUST be enabled (`<Nullable>enable</Nullable>`) in
  every project file; the null-oblivious context (`#nullable disable`) is
  forbidden.
- IDE diagnostics (IDExxxx) that represent code style MUST be enforced via
  `.editorconfig`; style rules that cannot be auto-fixed MUST be resolved
  manually before the PR is merged.

## Development Workflow & Quality Gates

All contributions to the CloudImaging codebase MUST pass the following gates
before merge to `main`:

1. **Build Gate**: `dotnet build -c Release` exits with code 0, zero warnings,
   zero errors (enforced by `TreatWarningsAsErrors`).
2. **Unit Test Gate**: `dotnet test --configuration Release` passes; coverage
   report shows >= 80% line coverage for changed code.
3. **Integration Test Gate**: Full .NET integration suite passes in the CI
   Azure staging environment (isolated resource group, short-lived identity).
4. **Lint Gate**: `.editorconfig` style rules report zero violations
   (`dotnet format --verify-no-changes`).
5. **Performance Gate**: No performance regression > 10% vs. the recorded
   baseline for affected pipeline stages.
6. **UI Responsiveness Gate**: WPF client verification proves all long-running
  operations execute off the UI thread, with no dispatcher block > 100 ms in
  instrumentation traces for representative workflows.

Pull requests MUST be reviewed by at least one other engineer before merge.
Auto-merge is permitted only when all six gates pass and at least one approval
is recorded.

## Governance

This constitution supersedes all other practices, README guidance, and verbal
agreements when conflicts arise. Amendments require:

1. A written proposal describing the change and its rationale.
2. Approval from at least two project maintainers.
3. A migration plan for any code that would violate the new rule.
4. Version bump following semantic versioning:
   - MAJOR: removal or backward-incompatible redefinition of a principle.
   - MINOR: new principle or materially expanded guidance added.
   - PATCH: clarifications, wording fixes, non-semantic refinements.

All pull requests and code reviews MUST verify compliance with this
constitution. Complexity MUST be justified against the principles above;
unnecessary abstraction is a violation of Principle II and V.

**Version**: 1.2.0 | **Ratified**: 2026-06-14 | **Last Amended**: 2026-06-14

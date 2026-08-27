<!-- SPECKIT START -->
For additional context about technologies to be used, project structure,
shell commands, and other important information, read the current plan
at specs/002-security-hardening-refinement/plan.md
<!-- SPECKIT END -->

Before making changes for any bug fix or feature implementation, first reason through the complete problem and the surrounding system.

Treat the requested bug or feature as an end-to-end engineering task, not as an isolated code change.

Start by understanding the full flow and establishing the intended behavior.

Determine:

* What the user or system is expected to do
* What currently happens
* Where the behavior originates
* Which frontend, backend, API, state, persistence, authentication, authorization, validation, configuration, and shared-type layers are involved
* Which existing patterns in the codebase should be followed
* What upstream and downstream code paths could be affected
* What edge cases, failure paths, race conditions, stale state, partial state, or inconsistent states may exist
* What tests or existing implementations clarify the intended behavior

Trace references, callers, callees, data flow, request/response flow, and state transitions as needed. Do not assume that the first file, function, error, or visible symptom is the actual root cause.

For bugs:

1. Reconstruct the affected end-to-end flow.
2. Identify the root cause rather than only treating the visible symptom.
3. Determine why the current implementation allows the bug to occur.
4. Check whether the same root cause affects other code paths or similar flows.
5. Implement the fix at the correct architectural layer.
6. Ensure the fix does not introduce regressions elsewhere.

For features:

1. Reconstruct the intended end-to-end user and system flow.
2. Identify every layer required to make the feature function completely.
3. Determine which parts already exist and which are missing, partial, stubbed, inconsistent, or incorrectly wired together.
4. Implement the feature across all required layers.
5. Ensure the implementation integrates with existing architecture and behavior rather than creating a parallel or one-off pattern.

Before editing code, form an implementation plan based on the repository.

Then execute that plan.

While working, actively inspect for related incomplete or incorrect code paths, including:

* TODOs, FIXMEs, placeholders, stubs, and temporary implementations
* Empty or partially implemented functions
* Hardcoded or mock behavior that should now be real
* Missing branches, enum cases, union cases, or switch cases
* Missing frontend handling for backend behavior
* Missing backend support for frontend behavior
* Request/response contract mismatches
* Incorrect, duplicated, or incomplete TypeScript types
* Missing validation
* Missing error propagation or error presentation
* Missing loading, empty, success, retry, or failure states
* Missing persistence or state synchronization
* Incorrect cache invalidation or stale state
* Missing authentication or authorization checks
* Inconsistent create/read/update/delete behavior
* Dead-end user flows
* Happy-path-only implementations
* Similar code paths that contain the same bug
* Tests, mocks, fixtures, or test data that no longer match the intended behavior

Do not stop after the first successful code change or when the original error disappears.

After each significant implementation step, reconsider the end-to-end flow and determine whether the change exposes additional missing or incorrect behavior.

When an issue can be resolved from the repository, make the necessary code change directly instead of only describing the problem.

Prefer fixing the underlying design or contract over applying narrow workarounds.

Preserve existing conventions and reuse existing abstractions where appropriate. Avoid unnecessary rewrites or unrelated refactoring, but make adjacent changes when they are necessary for correctness and completeness.

Validate the result using the checks available in the repository, including where applicable:

* TypeScript type checking
* Linting
* Unit tests
* Integration tests
* End-to-end tests
* Build validation
* Relevant manual or logical flow verification

Fix failures caused by the implementation.

Before considering the task complete, perform a final end-to-end review from the perspective of the original bug or feature request.

Verify that:

* The root cause or requested behavior has been fully addressed
* All required layers are connected correctly
* Success and failure paths behave correctly
* Relevant edge cases are handled
* Types and API contracts remain consistent
* State and persistence remain consistent
* Related code paths do not contain the same unresolved issue
* Tests adequately cover the important behavior
* No obvious partial implementation remains

Do not consider “the code compiles” or “the immediate bug is gone” sufficient evidence of completion.

The task is complete only when the requested behavior works coherently through its full intended flow and there are no known related implementation gaps that can reasonably be resolved from the repository.

At the end, provide a concise summary of:

* Your understanding of the problem
* The root cause or implementation approach
* The end-to-end code paths you traced
* The changes you made
* The validation performed
* Any remaining assumptions, risks, or unresolved limitations

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

## General PowerShell Practices

These rules apply to **all `.ps1` files** in this repository.

### Script Structure
- Always use `Begin` / `Process` / `End` blocks.
- `Begin` block must initialise: all collections, all counters, and a `$Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"` variable for output file naming when needed.
- Do NOT add `[System.Diagnostics.Stopwatch]` or any timer logic by default. Only include it for long-running operational scripts where writing to the output stream on every iteration would incur a measurable performance hit and elapsed time is genuinely useful context for the operator.

### Object Creation
- Always use `[PSCustomObject]@{}` to create objects. Never use `New-Object PSObject` followed by `Add-Member` calls.

### Collections
- Always use `New-Object -TypeName "System.Collections.Generic.List[System.Object]"` for collections that are built up in loops. Never use `@()` with `+=` for accumulation.

### Output Inside Loops
- Use `Write-Progress` inside loops for per-item progress. Never use `Write-Output` per item inside a loop.
- Reserve `Write-Output` for milestone messages outside of loops (e.g. query results count, phase start/end, export path confirmation).
- Never use `Write-Host`. Use `Write-Output` for all console output.
- Never use `Write-Output -InputObject ""` to print an empty line. Use `Write-Output ""` instead.

### Code Style
- Never align `=` signs across consecutive variable assignments with extra padding spaces. Each assignment uses a single space before and after `=`, regardless of neighbouring variable name lengths. This applies to both regular variable assignments and property definitions inside `[PSCustomObject]@{}` blocks.
  ```powershell
  # WRONG
  $TotalToRemove      = $RemovalList.Count
  $CurrentRemoveCount = 0

  [PSCustomObject]@{
      ObjectID                      = $Device.id
      ApproximateLastSignInDateTime = $Device.approximateLastSignInDateTime
  }

  # CORRECT
  $TotalToRemove = $RemovalList.Count
  $CurrentRemoveCount = 0

  [PSCustomObject]@{
      ObjectID = $Device.id
      ApproximateLastSignInDateTime = $Device.approximateLastSignInDateTime
  }
  ```
- Always wrap variables embedded in strings in a sub-expression: `"$($var)"` not `"$var"`. This avoids ambiguity with property access and maintains consistency.
- Never use backtick line continuation. Write every invocation on a **single line**, however long it becomes. Line length is not a constraint in this repository.
  ```powershell
  # WRONG
  New-AzTemplateSpec `
      -Name 'CloudImaging' `
      -ResourceGroupName $ResourceGroupName `
      -Version $Version

  # CORRECT
  New-AzTemplateSpec -Name "CloudImaging" -ResourceGroupName $ResourceGroupName -Version $Version
  ```
- Never split a long string across lines with `("..." + "...")` concatenation to shorten it. One long single-line string is correct.
- Never introduce splatting purely to shorten a line. Splat only when the same parameter set is genuinely reused. A single-line call listing every parameter is preferred over a hashtable that is used once.
  ```powershell
  # WRONG - hashtable used once, purely to avoid a long line
  $BlobParams = @{
      File = $PackagePath
      Container = $ContainerName
      Context = $StorageContext
      Force = $true
  }
  Set-AzStorageBlobContent @BlobParams | Out-Null

  # CORRECT
  Set-AzStorageBlobContent -File $PackagePath -Container $ContainerName -Context $StorageContext -Force | Out-Null
  ```
- Do not quote enumeration values passed to parameters: `-ErrorAction SilentlyContinue`, not `-ErrorAction "SilentlyContinue"`.
- Keep code compact. No gratuitous blank lines, and a single blank line between function definitions.
- Always place a blank line before a section/block comment (`# ...`), **except** when the comment is the first statement inside a code block (e.g. the opening line of an `if`, `else`, `try`, `catch`, `foreach`, `while`, function body, etc.).
  ```powershell
  # WRONG - no blank line before comment on line 2
  $SafeBase = [System.IO.Path]::GetFullPath($DriverItemPath)
  # Build exclusion set
  $ExtractionExclusions = @()

  # CORRECT - blank line separates prior statement from comment
  $SafeBase = [System.IO.Path]::GetFullPath($DriverItemPath)

  # Build exclusion set
  $ExtractionExclusions = @()

  # CORRECT - comment is first line inside a block; no preceding blank line needed
  try {
      # Iterate archive entries
      foreach ($ZipEntry in $ZipArchive.Entries) {
  ```

### Error Handling
- Always use `catch [System.Exception]` with a descriptive `Write-Warning` that includes `$($_.Exception.Message)`.
- Wrap the entire `Process` block body in a top-level `try/catch`.

### Comments
- Every non-obvious block of code carries a short comment stating **why**, never restating what the next line does. Resource lookups, matching logic, validation loops and each distinct phase of a script must not sit there uncommented.
- Keep it to one or two lines. Do not write a paragraph where a sentence will do.

### Parameters
- All parameters must include a `HelpMessage`.
- Always use `[ValidateNotNullOrEmpty()]` on string parameters.
- Use `[ValidateRange()]` on numeric parameters where a sensible bound exists.
- Keep `param()` blocks compact: no blank line between parameter declarations, and no blank line between the closing `)` and the first line of the body.
  ```powershell
  # CORRECT
  function Add-Result {
      param (
          [Parameter(Mandatory = $true, HelpMessage = "Component the result applies to.")]
          [ValidateNotNullOrEmpty()]
          [string] $Component,
          [Parameter(Mandatory = $false, HelpMessage = "Detail shown in the summary table.")]
          [string] $Detail = ""
      )
      $Results.Add([PSCustomObject]@{
          Component = $Component
          Detail = $Detail
      })
  }
  ```

### Module Requirements
- Declare module prerequisites with `#Requires -Modules <Name>, <Name>`. Never hand-roll a `Get-Module -ListAvailable` loop or a list of individual cmdlet availability checks.
- Place the `#Requires` line **after** the comment-based help block, immediately before `[CmdletBinding()]`. A `#Requires` line at the very top of the file suppresses comment-based help: `Get-Help` then returns only the syntax line and none of the `.PARAMETER` text.

### Scope Discipline
- Do not add defensive scaffolding that was not asked for, such as long lists of per-cmdlet availability checks or validation for conditions that cannot occur. Ask before adding it.

### Azure Deployment Scripts
- Any script that changes Azure resources takes a **mandatory** `-SubscriptionId`, so a run can never target whichever subscription the session happened to default to.
- Handle authentication explicitly: call `Connect-AzAccount` when there is no context, `Set-AzContext` to the supplied subscription, verify the context actually switched, then verify the resource group exists before doing any work.
- Resolve Azure resource names from the deployed naming convention rather than accepting them as parameters or matching on wildcards.

### Header Block
Every script must include a comment-based help block with:
- `.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER` (one per param), `.EXAMPLE` (at least one), `.NOTES`
- `.NOTES` must include: `FileName`, `Author`, `Created`, `Updated`, and a `Version history` section.

---
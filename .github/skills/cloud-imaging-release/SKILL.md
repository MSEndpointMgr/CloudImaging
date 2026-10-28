---
name: cloud-imaging-release
description: 'Release a new version of Cloud Imaging: Backend/IaC, Client and Media Builder release streams. Use when asked to "release", "cut a release", "ship v1.x", "tag a release", "publish a new version", "prepare release notes", or "trigger the release workflows". Confirms versions with the user, inventories what changed since the last tag, writes release notes, checks documentation coverage, runs pre-release gates, pushes tags in dependency order and verifies the GitHub Releases.'
argument-hint: 'Optional: version (e.g. 1.2.0) and streams (backend, client, mediabuilder)'
---

# Cloud Imaging Release

Produces GitHub Releases for one or more of the three release streams, with release notes and
documentation that match what actually ships.

## Release streams

| Stream | Tag | Workflow | Notes file | Paths that belong to it | Alias moved on stable |
|---|---|---|---|---|---|
| Backend/IaC | `mse-ci-v<ver>` | `.github/workflows/release-iac.yml` | `.github/release-notes/backend/v<ver>.md` | `src/CloudImaging.DeviceGatewayApi`, `src/CloudImaging.OperatorApi`, `src/CloudImaging.ImagingCoreApi`, `src/cloud-imaging-portal`, `src/deploy` | `mse-ci-iac-latest` |
| Media Builder | `mse-ci-mediabuilder-v<ver>` | `.github/workflows/release-mediabuilder.yml` | `.github/release-notes/mediabuilder/v<ver>.md` | `src/CloudImaging.MediaBuilder`, `src/CloudImaging.MediaBuilder.Installer` | none |
| Client | `mse-ci-client-v<ver>` | `.github/workflows/release-client.yml` | `.github/release-notes/client/v<ver>.md` | `src/CloudImaging.Client` | `mse-ci-client-latest` |

`src/CloudImaging.Contracts` is shared: a change there puts every stream that consumes the changed
contract in scope.

Facts the workflows enforce:
- Versions come from the tag only. Nothing in the repo needs a version bump.
- The notes file must exist, be non-empty and be on `origin/main` **before** the tag is pushed,
  otherwise the run fails before building.
- A version with a `-` suffix is a prerelease and does not move the alias. This skill only
  cuts stable releases.
- Each workflow runs a High/Critical dependency vulnerability gate and fails on a hit.
- There is no `needs` between streams; the order below is this skill's job.

## Hard rules

- Never push a tag, commit, push, delete a tag or re-run a release without explicit user
  confirmation for that step.
- The user often edits the repo in parallel sessions. Never stage, commit or revert files you did
  not change for this release; list them and ask.
- Release notes and docs: no spec, requirement or task identifiers (`FR-`, `SC-`, `T0`, `US`,
  "spec 005"), no em dash characters. Grep every file you touched for both before committing.
- Tags are created from `origin/main`, not local `HEAD`.

## Procedure

### 1. Establish the baseline
1. `git fetch origin main --tags`, then `git status --short`. Note uncommitted work and ask whether
   it belongs in this release.
2. Latest tag per stream (exclude the `-latest` aliases):
   `git tag --list "mse-ci-v*" --sort=-v:refname`, same for `mse-ci-client-v*` and
   `mse-ci-mediabuilder-v*`.
3. Changes per stream since its last tag:
   `git log --oneline <lastTag>..origin/main -- <stream paths>`.
   A stream with no changes is not released unless the user asks.
4. Existing notes already drafted for the next version: list `.github/release-notes/<stream>/`.

### 2. Confirm versions (always ask)
Streams are versioned independently: each bumps from its own last tag based on its own changes.
Propose a version per in-scope stream and ask the user to confirm or change it, using the
ask-questions tool. Suggest: major for breaking upgrades (data or contract changes that need
manual migration), minor for new features, patch for fixes only. Show the last tag, the commit
count and whether a notes file already exists. Then verify the tag is free:
`git ls-remote --tags origin "<tag>"` must return nothing.

### 3. Inventory what ships
For each in-scope stream:
1. Read the commit list from step 1, and the `specs/<NNN-feature>/` folders whose work landed
   since the last tag (`spec.md` user stories, `tasks.md` status).
2. Group into user-visible features, fixes, and upgrade-impacting changes. Flag:
   - new or changed app roles, Entra permissions or redirect URIs
   - Bicep or `uiFormDefinition.json` changes (customers must republish the Template Spec)
   - new blob containers, tables, app settings or Key Vault secrets
   - contract changes that force a minimum version on another stream
   - boot image changes that need new USB media
3. Find unfinished work that would ship: unchecked tasks in `specs/*/tasks.md`, `TODO`/`FIXME`
   in changed files, UI options for features that do not work. Ask whether to finish, hide or
   ship as is.
4. Present the feature list per stream and ask the user to confirm what goes into the notes
   (internal-only changes are left out).

### 4. Write the release notes
Create or update `.github/release-notes/<stream>/v<ver>.md` following
[the release notes template](./references/release-notes-template.md). Keep each stream's notes
about that stream; cross-reference others by tag. Every note includes:
- the minimum versions of the other streams when a feature spans streams, and the upgrade order
- a Template Spec notice when `src/deploy/bicep/**` or `src/deploy/uiFormDefinition.json`
  changed since the last backend tag: customers must download the new bundle and run
  `scripts\publish-template-spec.ps1` before redeploying, because `upgrade.ps1` does not
  republish it
- a **Rollback** section: whether going back to the previous tag is safe (data or schema written
  by the new version, roles added, boot media rebuilt) and the steps, linking
  `docs/upgrade-instructions.md#rollback`
- a **Full changes** link: `https://github.com/MSEndpointMgr/CloudImaging/compare/<previousTag>...<newTag>`

### 5. Check documentation coverage
For every confirmed feature, find where it is documented and update or add it:

| Topic | Document |
|---|---|
| Deployment, wizard fields, app registrations | `docs/setup-instructions.md` |
| Manual steps for existing tenants | `docs/upgrade-instructions.md`, section *Release-Specific Steps* (one subsection per step, stating which version introduces it) |
| Which stream versions work together | `docs/upgrade-instructions.md`, section *Version Compatibility* (create it under *Before You Start* if missing): one row per backend version with the minimum Media Builder and Client versions |
| Roles and access | `docs/roles-and-access.md` |
| Session flow, deployment timeline steps | `docs/session-lifecycle.md` |
| Image uploads | `docs/image-upload-pipeline.md` |
| Operations, troubleshooting, error messages | `docs/operations-runbook.md`, `docs/troubleshooting-index.md` |
| Architecture or new components | `docs/architecture-overview.md`, `docs/concepts-and-glossary.md` |
| Feature summary and release streams | `README.md` |
| New doc page | add to `docs/SUMMARY.md` (GitBook TOC) and `docs/README.md` |

Every link in the release notes must resolve to an existing heading. Report features with no
documentation and fix them before tagging.

### 6. Pre-release gates
Run and report each; stop on failure:
1. VS Code task **Release: Verify no secrets/config committed**.
2. If `src/deploy/bicep/**` changed: `az bicep build --file src/deploy/bicep/main.bicep --outfile src/deploy/bicep/main.json`.
   If `main.json` changes, it was stale: include it in the release pull request.
3. The same dependency gate the workflows run, so a release does not fail half-way (after
   `dotnet restore CloudImaging.sln` and `npm ci` in both portal folders):
   - Backend: `./.github/scripts/Assert-NoVulnerableDependencies.ps1 -MinimumSeverity High -Project 'src/CloudImaging.DeviceGatewayApi/CloudImaging.DeviceGatewayApi.csproj','src/CloudImaging.OperatorApi/CloudImaging.OperatorApi.csproj','src/CloudImaging.ImagingCoreApi/CloudImaging.ImagingCoreApi.csproj' -NpmDirectory 'src/cloud-imaging-portal/server','src/cloud-imaging-portal/client'`
   - Client: `-Project 'src/CloudImaging.Client/CloudImaging.Client.csproj'`
   - Media Builder: `-Project 'src/CloudImaging.MediaBuilder/CloudImaging.MediaBuilder.csproj'`
4. Grep changed notes and docs for em dashes and spec identifiers.

CI is checked after the merge in step 7, because that is the commit that gets tagged.

### 7. Pull request, merge, CI
1. Ask the user, then open a pull request with only the release files using the
   `git-commit-pr` skill (branch e.g. `release/<stream>-v<ver>`).
2. Tags are pushed only after the PR is merged. Wait for the user to confirm the merge, then
   `git fetch origin main --tags` and check the notes are there:
   `git show origin/main:<notesPath>`.
3. CI on the merged commit: `gh run list --workflow ci.yml --branch main --limit 5` must show
   success for `origin/main`'s head. CI does not gate tags on its own.

### 8. Push tags in dependency order
Order: **Backend/IaC, then Media Builder, then Client.** The backend must exist before tools that
call it; the Client goes last because a stable Client tag moves `mse-ci-client-latest`, which
Media Builder's *Automatic download* picks up immediately.

For each stream, ask for confirmation, then run the matching VS Code task (*Release: Push ...
tag*) or the identical commands:
`git fetch origin main --tags; git tag <tag> origin/main; git push origin <tag>`.
Then watch the run (`gh run list --workflow <workflow> --limit 1`, `gh run watch <id>`) and do not
push the next tag until it succeeds.

On failure: `gh run view <id> --log-failed`. Missing notes or a vulnerability hit are fixed on
`main`; the run can then be repeated with `gh workflow run <workflow> -f tag_name=<tag>`, which
builds **the tag's commit**. If the fix must be in the build, moving the tag is destructive: ask
before deleting and re-creating it.

### 9. Verify the releases
- `gh release view <tag>`: assets match the notes' artifact table, `SHA256SUMS` present, not
  marked as prerelease.
- The alias release (`mse-ci-iac-latest`, `mse-ci-client-latest`) points at the new commit
  (`git ls-remote --tags origin <alias>`).

### 10. After release
- Update repository memory (`/memories/repo/releases.md`): released version per stream, date,
  and any release gotcha met on the way (failed gate, missing note, re-run).
- Summarise: tags pushed, release URLs, notes files, docs changed, and any manual steps
  customers must take.

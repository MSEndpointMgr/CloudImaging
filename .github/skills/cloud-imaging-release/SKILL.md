---
name: cloud-imaging-release
description: 'The one way to release a new version of Cloud Imaging (Backend/IaC, Media Builder and Client). Use when asked to "release", "cut a release", "ship v1.x", "new version", "tag a release", "check release readiness", "are the versions right", "prepare release notes" or "trigger the release workflows". Checks that versions line up across all components, confirms the versions with the user, writes release notes and docs, runs pre-release gates, pushes tags in dependency order and verifies the GitHub Releases.'
argument-hint: 'Optional: versions per stream, e.g. backend 1.1.0, mediabuilder 1.1.0, client 1.1.0'
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
- The notes file must exist, be non-empty and be on `origin/main` **before** the tag is pushed,
  otherwise the run fails before building.
- A version with a `-` suffix is a prerelease and does not move the alias. This skill only
  cuts stable releases.
- Each workflow runs a High/Critical dependency vulnerability gate and fails on a hit.
- There is no `needs` between streams; the order below is this skill's job.

## How versions work

Every version comes from the tag at build time, so **no product version is bumped in the repo**:

| Artifact | Stamped by | Where it shows |
|---|---|---|
| Client exe | `-p:Version` in `release-client.yml`; the release fails if the published exe reports another version | Client title bar and info panel, boot image manifest, Autopilot records |
| Media Builder exe | `-p:Version` in `release-mediabuilder.yml` | Tool version recorded on every USB it prepares |
| Media Builder MSI | `MsiProductVersion` in `release-mediabuilder.yml` | Installed programs, in-place upgrade |
| Portal backend | `DEPLOYED_VERSION` written to `src/version.ts` | Portal update check |
| Backend bundle | `version.txt` | `install.ps1`, `upgrade.ps1`, `publish-template-spec.ps1` |

A build that is not made from a tag (a local publish, CI, a Client that Media Builder takes from
a local folder) keeps a development placeholder instead: `0.0.0-dev` for the Client and Media
Builder, `dev` for the portal backend. The Client then shows `dev` and the commit, for example
`Cloud Imaging dev f715787`, so a test build never passes for a release. A test build does not
show the next version on purpose: that version is decided when the release is cut (step 2).

Placeholders that are not release versions and must stay as they are: `"version": "0.0.1"` in both
portal `package.json` files, the `MsiProductVersion` default `0.0.1` in the `.wixproj`, the
`0.0.0-dev` default `Version` in `CloudImaging.Client.csproj` and `CloudImaging.MediaBuilder.csproj`,
`DEPLOYED_VERSION = 'dev'` in the portal backend's `src/version.ts`, and `contentVersion` in ARM files.

What drifts is every **reference** to a version, and that is what this skill keeps consistent:
- each stream's notes file, its minimum versions of the other streams and its `compare/` link
- `docs/upgrade-instructions.md`: *Release-Specific Steps* (the version that introduces each step)
  and *Version Compatibility* (minimum Media Builder and Client per backend version)
- any doc or `README.md` sentence that names a tag

The [check script](./scripts/Get-ReleaseReadiness.ps1) correlates all of this. It only reads the
repo and the tags on origin; it never changes, commits or pushes anything:
```powershell
& .github/skills/cloud-imaging-release/scripts/Get-ReleaseReadiness.ps1 [-BackendVersion x.y.z] [-MediaBuilderVersion x.y.z] [-ClientVersion x.y.z]
```
It prints one row per stream (last released tag, target version, commits since, unreleased notes
drafts) and a list of findings marked `Fail` (blocks the release) or `Warning` (fix or accept).
Without versions, each stream targets the next notes draft above its last released tag.

## Hard rules

- Never push a tag, commit, push, delete a tag or re-run a release without explicit user
  confirmation for that step.
- The user often edits the repo in parallel sessions. Never stage, commit or revert files you did
  not change for this release; list them and ask.
- Release notes and docs: no spec, requirement or task identifiers (`FR-`, `SC-`, `T0`, `US`,
  "spec 005"), no em dash characters. Grep every file you touched for both before committing.
- Tags are created from `origin/main`, not local `HEAD`.
- When asking the user anything, use the ask-questions tool, explain any output or term they have
  not seen yet, and always leave the free-text answer enabled so they can answer "Other".

## Procedure

### 1. Check where each stream stands
1. `git status --short`. Note uncommitted work and ask whether it belongs in this release.
2. Run the check script without versions (it fetches `origin main --tags` itself).
3. Show the user the stream table in plain words: what was last released, what would be
   released, how many changes there are, and which notes drafts exist.
4. Changes per stream since its last tag:
   `git log --oneline <lastTag>..origin/main -- <stream paths>`.
   A stream with no changes is not released unless the user asks. Commits in
   `src/CloudImaging.Contracts` put every stream that uses the changed contract in scope.

### 2. Confirm versions (always ask)
Propose a version per in-scope stream and ask the user to confirm or change it. Streams are
versioned independently, each from its own last tag. Suggest major for breaking upgrades (data
or contract changes that need manual migration), minor for new features, patch for fixes only.
Show the last tag, the commit count and whether a notes file already exists.

If notes exist for more than one unreleased version (for example v1.1.0 and v1.2.0 drafts while
only v1.0.0 is tagged), always ask which version is next; the default is the lowest. A tag is cut
from `origin/main`, so everything merged ships in it, including work drafted under a later
version. Fold those later drafts into the target notes, delete them and replace their version in
every reference. Never release two versions of one stream at once.

Re-run the check script with the confirmed versions. It also fails if a target tag already
exists or is not higher than the last release.

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

### 6. Make the versions line up
Re-run the check script with the confirmed versions and fix every `Fail`, then every `Warning`
(or get the user to accept it). Edits to notes and docs are local: make them, but do not commit.

| Finding | Fix |
|---|---|
| Notes missing, or not on origin/main | Write them (step 4); they are merged with the release pull request. |
| Compare link missing or wrong | `**Full changes**: https://github.com/MSEndpointMgr/CloudImaging/compare/<released>...<target>` |
| Another unreleased draft (above or below the target) | Fold it into the target notes (step 2). |
| Notes or docs reference a tag that is neither released nor planned | Point it at the planned target of that stream, or the released tag that already has the capability. |
| No Version Compatibility section or row | Add it under *Before You Start*: one row per backend version with the minimum Media Builder and Client. |
| Template Spec notice missing | Add the Template Spec section to the backend notes. |
| `main.json` not rebuilt | `az bicep build --file src/deploy/bicep/main.bicep --outfile src/deploy/bicep/main.json` |
| Version stamping or release contract `Fail` | A workflow or a consumer of the release assets changed. Report it; fixing it is a code change the user approves. |
| No `0.0.0-dev` default in an app project | Test builds of that app report `1.0.0` again. Report it; restoring the default is a code change the user approves. |

Then read the target notes of all in-scope streams side by side; the script cannot check these:
1. Minimum versions agree in both directions and match the Version Compatibility row.
2. Every note gives the same upgrade order (Backend/IaC, Media Builder, Client) with the same tags.
3. Each contract change since the last tags names the backend version the consuming stream needs.
4. If the Client changed how it talks to the backend, the notes and *Release-Specific Steps* say
   to regenerate boot images and from which version.
5. If the Client needs a newer backend, its notes say to release and deploy the backend first,
   because a stable Client tag reaches Media Builder's *Automatic download* immediately.

### 7. Pre-release gates
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
5. The check script with the confirmed versions reports no `Fail`, and every `Warning` is fixed
   or accepted by the user.

CI is checked after the merge in step 8, because that is the commit that gets tagged.

### 8. Pull request, merge, CI
1. Ask the user, then open a pull request with only the release files using the
   `git-commit-pr` skill (branch e.g. `release/<stream>-v<ver>`).
2. Tags are pushed only after the PR is merged. Wait for the user to confirm the merge, then
   `git fetch origin main --tags` and check the notes are there:
   `git show origin/main:<notesPath>`.
3. CI on the merged commit: `gh run list --workflow ci.yml --branch main --limit 5` must show
   success for `origin/main`'s head. CI does not gate tags on its own.

### 9. Push tags in dependency order
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

### 10. Verify the releases
- `gh release view <tag>`: assets match the notes' artifact table, `SHA256SUMS` present, not
  marked as prerelease.
- The alias release (`mse-ci-iac-latest`, `mse-ci-client-latest`) points at the new commit
  (`git ls-remote --tags origin <alias>`).

### 11. After release
- Update repository memory (`/memories/repo/releases.md`): released version per stream, date,
  and any release gotcha met on the way (failed gate, missing note, re-run).
- Summarise: tags pushed, release URLs, notes files, docs changed, and any manual steps
  customers must take.

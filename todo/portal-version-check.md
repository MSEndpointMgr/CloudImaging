# TODO: Portal "new version available" check

Status: **implemented**. This document is retained for the design rationale and for the
follow-up work in [Out of scope for v1](#out-of-scope-for-v1), which is still outstanding.

Shipped: opt-in and off by default, Administrator-only, surfaced as a version panel on
**Configuration → Miscellaneous** and a dismissible banner. Scope is the backend/infrastructure
release stream only.

## Context / why this doesn't already exist

Nothing in the portal knows what version it is, and nothing knows what version is available.
There is no version endpoint, no version in the interface, and no comparison logic anywhere.

The only "newer version available" code in the repository is
[BootImageSelfUpdateService](../src/CloudImaging.Client/Services/BootImageSelfUpdateService.cs),
which is the WinPE client refreshing its own binaries on a USB stick against the
`mse-ci-client-latest` alias. It is unrelated to the portal and does not generalise.

Today an administrator only discovers an upgrade exists by manually watching the
[Releases page](https://github.com/MSEndpointMgr/CloudImaging/releases), which is a flat list
mixing three independently versioned streams.

## The prerequisite: recording the deployed version

Nothing records what is currently deployed. `update.ps1` resolves a release version, uses it only
as a blob filename label (`$packageLabel`), and discards it. No app setting, no tag, no stored row.
So there is nothing to compare a "latest" value against.

### Rejected: have `update.ps1` write an app setting

This was the obvious first idea and it is wrong. The portal backend's app settings are declared as
a Bicep array in
[cloud-imaging-portal.bicep](../src/deploy/bicep/modules/cloud-imaging-portal.bicep). A Template
Spec redeploy, which [upgrade-instructions.md](../docs/upgrade-instructions.md) explicitly tells
people to run whenever a release changes infrastructure, rewrites that array and **silently drops
any setting the script had added**. The recorded version would revert to nothing on an
infrastructure upgrade, which is exactly when it matters most.

Storing it in Table Storage avoids that but needs a new Imaging Core API endpoint, a new
repository, and a Storage Table data-plane role for the upgrading identity. Too much machinery for
one string.

### Chosen: stamp the version into the build artifact

Add a step to `release-iac.yml`, before the portal backend build, that writes the release tag into
a generated file (for example `src/version.json`) that the build includes in `dist/`.

The deployed version then becomes **intrinsic to the deployed code**:

- It cannot drift from what is actually running.
- It survives Bicep redeploys, because it is not configuration.
- It is correct even if someone deploys manually or out of band.
- `update.ps1` needs **no changes at all**.

A local development build with no tag stamps something like `dev`, and the interface treats any
unrecognised value as "unknown" rather than trying to compare it.

## Design

### Current version

The backend reads the stamped value at startup and includes it in the existing
`GET /api/config` response ([app.ts](../src/cloud-imaging-portal/server/src/app.ts)). That endpoint
is already unauthenticated and already the SPA's bootstrap call, and a product version is not
sensitive. No new plumbing on this side.

### Latest available version

New authenticated route, `GET /api/version`, Administrator-only via the existing `requireRole`
middleware. It calls:

```
https://api.github.com/repos/MSEndpointMgr/CloudImaging/releases/tags/mse-ci-iac-latest
```

This is deliberately the **same alias `update.ps1` resolves**, so the portal can never advertise a
version the upgrade script would not install. Using GitHub's repository-wide `releases/latest`
instead would resolve to whichever stream published most recently, which is the exact trap the
alias exists to avoid.

**The backend makes this call, not the browser.** Three reasons: it avoids exposing every
operator's IP address to github.com; a single server-side cache serves all users, keeping the
portal far below GitHub's 60-requests-per-hour unauthenticated limit rather than burning one
request per operator per page load; and in a restricted or air-gapped network it fails once,
server-side, instead of leaving every browser to time out.

Response shape:

```jsonc
{
  "current": "mse-ci-v1.2.0",
  "latest": "mse-ci-v1.3.0",   // null when unknown
  "updateAvailable": true,
  "releaseUrl": "https://github.com/...",
  "checkedAt": "2026-09-09T10:00:00Z",
  "status": "ok"               // ok | disabled | unreachable | rate-limited | unknown
}
```

Cache the upstream result in memory for several hours. A cold start re-fetches, which is
acceptable, and `alwaysOn` is already enabled on the plan so restarts are infrequent.

### Opt-in

Off unless an administrator enables it. Add a boolean to the existing portal configuration
(**Configuration → Miscellaneous**), so it is stored and edited exactly like the other settings
rather than becoming a new mechanism.

While disabled, `/api/version` returns `status: "disabled"` and makes no outbound call. This has to
be genuinely enforced server-side, not merely hidden in the interface, because the point is to
guarantee no traffic leaves the tenant.

### Interface

**Configuration page**: a version panel showing current version, latest version, last checked
timestamp, a link to the release notes, and the enable/disable toggle. When disabled or unknown,
it states that plainly instead of showing a broken comparison.

**Banner**: shown to Administrators when, and only when, the check is enabled, succeeded, and an
upgrade is genuinely available. Dismissible, with the dismissal remembered per user and per
version, so dismissing 1.3.0 does not also suppress 1.4.0. The existing `certExpiry` warning helper
([certExpiry.ts](../src/cloud-imaging-portal/client/src/lib/certExpiry.ts)) already solves the
"remember a dismissal until the situation changes" problem and should be the model rather than a
second bespoke pattern.

Link the banner to [upgrade-instructions.md](../docs/upgrade-instructions.md), since knowing an
upgrade exists is useless without the procedure.

### Version comparison

Parse `mse-ci-v<major>.<minor>.<patch>` and compare numerically. Do **not** compare strings:
`v1.10.0` sorts before `v1.9.0` lexically. Pre-release suffixes (`-rc.1`) must never trigger the
banner, since the alias only ever moves on stable releases. Any value that does not parse is
treated as unknown, which disables the comparison rather than guessing.

## Failure modes

Every one of these must be a non-event. A version check must never be able to degrade the portal.

| Condition | Behaviour |
|---|---|
| Check disabled | `status: "disabled"`, no outbound call, no banner |
| No outbound internet access | `status: "unreachable"`, no banner, panel says so |
| GitHub rate limit (HTTP 403) | `status: "rate-limited"`, serve the last cached value if present |
| Alias release missing | `status: "unknown"`, no banner |
| Current version unparseable (local build) | `status: "unknown"`, no comparison attempted |
| GitHub slow or hanging | Short timeout, treated as unreachable; never blocks the request |

## Out of scope for v1

**Client and Media Builder versions.** The portal cannot know what is deployed for either: the
Media Builder lives on technician workstations, and the Client is baked into boot images that may
have been built months ago. Reporting "latest available" for them without a corresponding
"currently deployed" would be noise.

There is a natural follow-up: have boot image catalog entries record the Client version they embed
at generation time, which the Media Builder already knows because it resolves
`mse-ci-client-latest` when using the automatic download source. The portal could then flag boot
images built against an outdated Client. That is a separate feature and should not block this one.

**Automatic upgrading.** Out of the question. Upgrades restart Function Apps, may require a
Template Spec redeploy, and can require rebuilding boot media. It stays a deliberate human action.

## Implementation checklist

All complete. The route shipped as `GET /api/update-check` rather than `/api/version`, and the
comparison lives server-side in
[services/version.ts](../src/cloud-imaging-portal/server/src/services/version.ts) so the interface
only transports a resolved result.

1. ~~`release-iac.yml`: stamp the release tag into the portal backend build before `npm run build`.~~
2. ~~Backend: read the stamped version at startup; add it to `GET /api/config`.~~
3. ~~Backend: add the opt-in flag to the portal configuration contract, defaulting to off.~~
4. ~~Backend: add the update check route, Administrator-only, cached, with the status values above.~~
5. ~~Frontend: version panel and toggle on the Configuration page.~~
6. ~~Frontend: dismissible Administrator banner, dismissal keyed per user and per version.~~
7. ~~Frontend: version parsing and comparison helper, with tests covering `1.10.0` versus `1.9.0`,
   pre-release suffixes, and unparseable values.~~
8. ~~Tests: backend route role-gating, the disabled path making no outbound call, and each failure
   mode degrading rather than throwing.~~
9. ~~Docs: note the setting in [setup-instructions.md](../docs/setup-instructions.md) under the
   optional configuration review, and reference it from
   [upgrade-instructions.md](../docs/upgrade-instructions.md).~~

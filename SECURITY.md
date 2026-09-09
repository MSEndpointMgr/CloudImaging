# Security policy

MSEndpointMgr Cloud Imaging provisions Windows images to bare-metal devices, issues
device-session bearer tokens, and distributes OS images over time-limited
signed links. It is deployed into your own Azure subscription, not hosted by
MSEndpointMgr. Please report suspected vulnerabilities privately so
maintainers can investigate before details are disclosed publicly.

## Supported versions

MSEndpointMgr Cloud Imaging ships as three independently versioned release streams (see
[Release Model](README.md#release-model) in the README):

| Stream | Tag prefix | Supported |
|---|---|---|
| Backend / infrastructure | `mse-ci-v#.#.#` | Latest tag only |
| Cloud Imaging Client | `mse-ci-client-v#.#.#` | Latest tag only |
| Media Builder | `mse-ci-mediabuilder-v#.#.#` | Latest tag only |

Only the latest release of each stream is supported. Fixes are not routinely
backported to older tags in any stream. A report that affects an older tag
but not the current one is still useful and welcome.

## Report a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/MSEndpointMgr/CloudImaging/security/advisories/new).
Do not open a public issue or pull request for an undisclosed vulnerability.

Include:

- The affected release stream and tag/version.
- The affected component (Client, Media Builder, Device Gateway API,
  Operator API, Imaging Core API, Portal, or the Bicep/IaC package).
- Reproduction steps and the expected security impact.
- A minimal proof of concept, if safe to provide.
- Any known mitigations or workarounds.

Do not send real tenant IDs, boot media certificates or PFX/private key
material, live device-session tokens, SAS/download URLs, Entra ID app
secrets, or unredacted client/portal logs. A maintainer will arrange a safer
exchange if additional sensitive evidence is required.

Maintainers will acknowledge a report as soon as practical, validate its
scope and impact, and provide updates on material progress. Remediation
timing depends on severity; fixed dates are not guaranteed. Please
coordinate public disclosure with the maintainers.

## Scope

Relevant reports include, but are not limited to:

- Bypass or forgery of the mutual TLS (mTLS) boot media certificate check on
  the Device Gateway API.
- Session passcode brute force, coupling race conditions, or bypass of the
  composite-key rate limiting.
- Device-session token forgery, replay, or scope escalation.
- SAS/download link scope, lifetime, or access-control abuse.
- Entra ID app-role bypass on the Operator API, Portal, or Media Builder
  (privilege escalation between `CloudImaging.Technician`,
  `CloudImaging.Administrator`, and the service-level roles).
- Access to the private, VNet-isolated Imaging Core API from outside the
  intended network boundary.
- Secret or credential exposure in logs, telemetry, or Application Insights
  that should have been redacted.
- Supply-chain issues in the published Template Spec, release archives, or
  `update.ps1`.

## Out of scope

- Actions available to someone with physical possession of a *provisioned*
  boot media USB drive or WIM. Boot media embeds an mTLS client certificate
  by design; it is a bearer credential and is documented as such (see
  [docs/operations-runbook.md](docs/operations-runbook.md), "Certificate
  Management"). Treat it, and the workstation used to build it, as sensitive.
  Loss or suspected compromise of boot media should be handled by rotating
  the boot media certificate, not reported here as a vulnerability.
- Misconfiguration of the deployer's own Azure subscription, network, or
  Entra ID tenant after deployment (role assignments, firewall rules,
  conditional access). MSEndpointMgr Cloud Imaging is deployed and administered
  by you; the resource group and tenant configuration are your responsibility
  to secure.
- Findings that only affect the underlying Azure platform, Windows ADK, or
  WinPE, rather than MSEndpointMgr Cloud Imaging itself.
- Automated scanner output with no reproducible impact specific to
  MSEndpointMgr Cloud Imaging.

## Deployer responsibilities

MSEndpointMgr Cloud Imaging deploys into your own Azure subscription; there is no
MSEndpointMgr-hosted instance. You are responsible for the Azure resource
group, network boundary, Entra ID app registrations, and role assignments
created during setup (see [docs/setup-instructions.md](docs/setup-instructions.md)).
Rotate the boot media certificate if a device or USB drive with embedded
boot media is lost, decommissioned insecurely, or suspected compromised (see
[docs/operations-runbook.md](docs/operations-runbook.md)).

## Good-faith research

Research only against a deployment, tenant, and devices you own or are
authorized to test. Avoid privacy violations, service disruption, and
destructive actions beyond what is necessary to demonstrate an issue. Good
faith reports that follow this policy will not be treated as malicious
activity.

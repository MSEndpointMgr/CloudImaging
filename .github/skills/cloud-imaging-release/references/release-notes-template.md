# Release notes template

One file per stream and version: `.github/release-notes/<stream>/v<ver>.md`. The workflow uses the
file as the GitHub Release body, so it starts directly with text (no top-level heading).

Style, matching the existing notes:
- Short, information-dense prose aimed at the administrator who upgrades.
- One `###` section per user-visible feature, then optional upgrade notes, then the artifact table.
- Bold UI paths (**Imaging** > **Setup Profiles**), backticks for tags, roles, files and settings.
- State who can use a feature (roles) and what an existing tenant must do, if anything.
- Link docs with absolute GitHub URLs on `main`, to an existing heading.
- No spec, requirement or task identifiers. No em dash characters.

## Backend/IaC

```markdown
Backend/IaC release: the 3 Function Apps, portal (frontend + backend), and
Bicep/deploy scripts. Cloud Imaging Client and Media Builder ship from their own
release streams (tags `mse-ci-client-v*` / `mse-ci-mediabuilder-v*`).

### <Feature name>

<What it does and where it is in the portal. Who can use it.>

- **<Aspect>**: <detail>
- **Infrastructure**: <Template Spec republish needed? new containers or settings? "No data migration.">
- **Upgrade order**: backend, then Media Builder `mse-ci-mediabuilder-v<ver>`, then new boot
  images with Client `mse-ci-client-v<ver>`. Follow
  [<upgrade step>](https://github.com/MSEndpointMgr/CloudImaging/blob/main/docs/upgrade-instructions.md#<anchor>).

See [<doc>](https://github.com/MSEndpointMgr/CloudImaging/blob/main/docs/<doc>.md).

### Template Spec

<Only when Bicep or the wizard changed.> Download `cloud-imaging-v<ver>.zip` and run
`scripts\publish-template-spec.ps1` before redeploying: `upgrade.ps1` does not republish the
Template Spec, so an older wizard stays in place until you do.

### Rollback

<Safe to go back to `mse-ci-v<prev>`? Say what the new version wrote that the old one ignores or
cannot read, and the steps.> See
[Rollback](https://github.com/MSEndpointMgr/CloudImaging/blob/main/docs/upgrade-instructions.md#rollback).

### Release artifacts
| File | Description |
|------|-------------|
| `cloud-imaging-v<ver>.zip` | Backend/IaC bundle (5 components + deploy scripts) |
| `SHA256SUMS` | SHA-256 checksums for integrity verification |

**Full changes**: https://github.com/MSEndpointMgr/CloudImaging/compare/mse-ci-v<prev>...mse-ci-v<ver>
```

## Client

```markdown
WinPE-runnable binary embedded into boot images by Media Builder, either via the
"Automatic download" source option (always resolves the `mse-ci-client-latest` alias
release) or by pointing Media Builder at a local extracted copy of one of these ZIPs
matching its selected target architecture.

### <Feature name>

- **<Behaviour>**: <what the device does differently>
- Needs backend `mse-ci-v<ver>` and a boot image from Media Builder `mse-ci-mediabuilder-v<ver>`.

### Rollback

Build boot images with Client `mse-ci-client-v<prev>` (local ZIP source in Media Builder) and
update USB media. <Anything the older Client cannot handle.>

### Release artifacts
| File | Description |
|------|-------------|
| `cloud-imaging-client-v<ver>-win-x64.zip` | Self-contained win-x64 WPF client (net10.0-windows) |
| `cloud-imaging-client-v<ver>-win-arm64.zip` | Self-contained win-arm64 WPF client (net10.0-windows) |
| `SHA256SUMS` | SHA-256 checksums for integrity verification |

**Full changes**: https://github.com/MSEndpointMgr/CloudImaging/compare/mse-ci-client-v<prev>...mse-ci-client-v<ver>
```

## Media Builder

```markdown
Technician-workstation WPF app used to generate WinPE boot images and prepare USB
storage devices.

Deploy it with the MSI: it installs per-machine and takes the tenant configuration as
install properties, so there is no need to repackage the payload per tenant. See
`docs/setup-instructions.md` > "Deploying the Media Builder with Intune" for the full
deployment guide, including MSI properties.

### <Feature name>

<What changes for the technician or operator building media. Role requirements. Whether new
boot images or USB media are needed, and the backend version it needs.>

### Rollback

Install the `mse-ci-mediabuilder-v<prev>` MSI over this one. <Boot images built with this version
keep working, or not, and why.>

### Release artifacts
| File | Description |
|------|-------------|
| `cloud-imaging-mediabuilder-v<ver>.msi` | Per-machine installer (x64). Configured via MSI properties; upgrades in place and keeps existing configuration. |
| `cloud-imaging-mediabuilder-v<ver>.zip` | The same self-contained win-x64 payload (net10.0-windows), unpackaged, for xcopy deployment or custom packaging. |
| `SHA256SUMS` | SHA-256 checksums for integrity verification |

**Full changes**: https://github.com/MSEndpointMgr/CloudImaging/compare/mse-ci-mediabuilder-v<prev>...mse-ci-mediabuilder-v<ver>
```

If a workflow's asset step changes file names, update the artifact table to match
(`release-*.yml`, *Create GitHub Release* step).

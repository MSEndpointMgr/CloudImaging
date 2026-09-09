# Contributing to MSEndpointMgr Cloud Imaging

Thank you for helping improve MSEndpointMgr Cloud Imaging. This project performs privileged,
network-facing, and sometimes destructive operations (disk formatting on
target devices, mutual TLS certificate handling, Entra ID role assignment),
so contributions must be focused, testable, and safe.

## Before you start

- Search existing [issues](https://github.com/MSEndpointMgr/CloudImaging/issues)
  and pull requests before opening a new one.
- Open an issue before starting substantial behavior, architecture, or schema
  changes, so the approach can be agreed on first.
- Never include tenant IDs, client secrets, certificates or private keys,
  device-session tokens, SAS/download links, hardware hashes, device
  identifiers, or unredacted logs in an issue, pull request, or commit.
- All contributions are submitted under the repository's [MIT License](LICENSE).
  Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
- Found a possible security vulnerability? Follow [SECURITY.md](SECURITY.md)
  instead of opening a public issue.

## Development environment

Requires:

- Windows, Git, PowerShell 7+
- .NET SDK matching [global.json](global.json) (currently 10.0.x)
- Node.js 22.x (portal client and server)
- Windows ADK + the matching Windows PE add-on, only needed to test Media
  Builder boot image generation and USB preparation end to end

Restore and build the .NET solution:

```powershell
dotnet restore CloudImaging.sln
dotnet build CloudImaging.sln --configuration Release
```

Install portal dependencies:

```powershell
cd src/cloud-imaging-portal/server; npm install
cd ../client; npm install
```

Local dev servers and other common workflows are available as VS Code tasks
(`Terminal > Run Task`), including `Dev: Run portal server`, `Dev: Run portal
client`, and `Dev: Simulate device session` (exercises the Device Gateway API
without building the WPF client).

## Solution map

| Project | Role |
|---|---|
| `CloudImaging.Contracts` | Shared DTOs/enums referenced by every other .NET project; a change here rebuilds all of them |
| `CloudImaging.Client` | WPF client that runs on the target device in WinPE |
| `CloudImaging.MediaBuilder` | WPF technician workstation app: boot image generation, USB preparation |
| `CloudImaging.DeviceGatewayApi` | Public Azure Functions app; mTLS-secured, device-facing |
| `CloudImaging.OperatorApi` | Public Azure Functions app; Entra ID + app-role secured, used by Portal and Media Builder |
| `CloudImaging.ImagingCoreApi` | Private, VNet-isolated Azure Functions app; owns session state, SAS tokens, image catalog |
| `cloud-imaging-portal/server` | Express backend for the Portal; calls the Operator API |
| `cloud-imaging-portal/client` | React frontend for the Portal |
| `src/deploy` | Bicep infrastructure-as-code and PowerShell deployment/upgrade scripts |

The portal (`cloud-imaging-portal/`) is fully independent of the .NET code.
Keep it that way: it must build and test without the .NET solution present.

API request/response contracts are documented per component under
`specs/*/contracts/`; update the matching file when a route or payload
changes.

## Make a change

- Create a focused branch; keep unrelated changes out of the pull request.
- Follow the existing patterns in the component you're touching, including
  structured logging, log redaction of secrets (see the "Security" section
  in [README.md](README.md)), and existing error/result conventions.
- Update `docs/` or the affected `specs/*/contracts/*.md` file when
  behavior, configuration, or a request/response contract changes.
- Config schema changes (Bicep parameters, `appsettings.json` shape,
  `docs.yaml`-style files) are compatibility contracts across components;
  bump only what actually changed and update every reader/writer together.

## Validate the change

Run whichever of these apply to your change (also available as VS Code
tasks):

```powershell
# .NET solution
dotnet build CloudImaging.sln --configuration Release
dotnet test CloudImaging.sln --configuration Release

# Portal client
cd src/cloud-imaging-portal/client; npm run lint; npm run test:run

# Portal server
cd src/cloud-imaging-portal/server; npm run lint; npm run test:run

# Before a public release only
pwsh -NonInteractive -File src/deploy/scripts/verify-release-secrets.ps1
```

Automated checks run the same build/test/lint steps per component on every
pull request, scoped to what changed. Fix failures caused by your change
rather than suppressing them.

Use disposable VMs, test disks, and a non-production Azure tenant for manual
imaging and deployment testing; imaging workflows format disks and Media
Builder workflows erase USB drives.

## Open a pull request

- Use a clear, descriptive title; Conventional Commit style
  (`fix(device-gateway): ...`, `feat(portal): ...`) is preferred but not
  required.
- Explain the reason for the change, what changed, and how you validated it.
- Link related issues with `Closes #123` where applicable.
- Include screenshots for visible Portal UI changes.
- Call out breaking changes, contract changes, and any known limitations.

Maintainers may request changes to keep component boundaries, security
posture, and documentation accurate.

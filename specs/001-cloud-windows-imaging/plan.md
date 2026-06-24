# Implementation Plan: Cloud Windows Imaging

**Branch**: `001-cloud-windows-imaging` | **Date**: 2026-06-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-cloud-windows-imaging/spec.md`

## Summary

Build a six-component polyglot system that provisions Windows OS images from
Azure Blob Storage to bare-metal devices running WinPE. The Cloud Imaging
Client (WPF .NET 10) on the device initiates and drives the imaging workflow
through a publicly reachable Device Gateway API. The Device Gateway API brokers
all device-originated calls to a private Imaging Core API over Private Link. The
Cloud Imaging Portal (React 19 frontend + Node.js/Express backend) and Cloud
Imaging Media Builder (WPF desktop app) both call an authenticated Operator API,
which in turn brokers operator-facing operations to the same Imaging Core API.
The Imaging Core API owns storage permissions, SAS token URL issuance, session state,
image catalog state, and boot image metadata. The entire solution must be
packaged as a reproducible, self-hostable deployment bundle so companies can
deploy it into their own Azure tenant, networking model, and operational
environment without code changes.

---

## Technical Context

**Language/Version**:
- .NET 10 (`net10.0`) -- DeviceGatewayApi, OperatorApi, ImagingCoreApi, shared contracts library
- .NET 10 (`net10.0-windows`) -- Cloud Imaging Client, Cloud Imaging Media Builder
- Node.js v22 LTS -- Cloud Imaging Portal backend (Express + TypeScript)
- TypeScript 5.x -- Cloud Imaging Portal frontend (React 19 + Vite) and backend

**Primary Dependencies**:
- `Microsoft.Azure.Functions.Worker` v2 (isolated worker model, .NET 10) for DeviceGatewayApi, OperatorApi, and ImagingCoreApi
- `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`
- `Azure.Storage.Blobs` (SAS token generation in ImagingCoreApi)
- `Azure.Data.Tables` (session state, OS image catalog, boot image metadata)
- `Microsoft.Identity.Web` (JWT validation and App Role checks on Function Apps)
- `Microsoft.Extensions.Http` (IHttpClientFactory for inter-service calls)
- `System.Management` + Windows Storage APIs (USB disk discovery and safety checks in Cloud Imaging Media Builder)
- DISM and BCDBoot invocation wrappers (WinPE media creation, boot configuration in Cloud Imaging Media Builder)
- `@azure/msal-desktop` (Entra ID sign-in for Cloud Imaging Media Builder on Windows workstation)
- React 19, Vite, Shadcn/UI (Radix UI), Tailwind CSS v3
- `@azure/msal-react` + `@azure/msal-browser` (portal frontend auth)
- `@azure/msal-node` (portal backend token validation)
- `@tanstack/react-query` v5 (portal frontend server state + polling)
- `react-router-dom` v7 (portal routing)
- `lucide-react` (icons), Recharts (dashboard charts)
- Express v5 + TypeScript

**Storage**:
- Azure Blob Storage:
  - OS image files (.wim / .esd) for device provisioning
  - Boot image artifacts (.iso or .vhd format) for Cloud Imaging Media Builder
  - Branding logo uploads (Cloud Imaging Portal)
- Azure Table Storage:
  - DeviceSession, ImagingStep, OSImageMetadata, BrandingConfiguration entities (ImagingCoreApi)
  - BootImage and BootImageManifest records (ImagingCoreApi)

**Deployment/Packaging**:
- Infrastructure-as-Code bundle (Bicep preferred) for all Azure resources:
  Storage Account, Function Apps, App Service, Static Web Apps, VNet,
  Private Endpoint, Private DNS, App Insights, Managed Identities, and RBAC
- Environment-specific configuration package (`.env.example`, app settings
  templates, and deployment parameter files) for dev, test, and production
- Cloud Imaging Media Builder-generated boot image output and instructions so
  organizations can generate WinPE boot media that embeds the Cloud Imaging
  Client without modifying application source
- USB preparation tooling package for technician workstations that produces
  standardized two-partition bootable media and writes preparation manifest data
- Release model: every versioned GitHub Release is a complete bundle containing
  release artifacts from all six components (three Function App packages, Portal
  frontend and backend as separate artifacts, Cloud Imaging Client WinPE binary,
  and Media Builder Windows installer -- seven release artifacts in total), IaC
  templates, parameter templates, and documentation; all six components are
  included in every release regardless of which changed since the prior release;
  no component is released independently; versioning is solution-level
- In-place upgrade support: an included upgrade script (update.ps1) re-applies
  the IaC package and redeploys all application components against an existing
  deployment, bringing it to the new release version without manual resource
  deletion or re-entry of unchanged parameter values; the script is idempotent
  and suitable for self-hosters maintaining a single production environment
- Internal CI/CD (dev team only): the two core contributors provision and update
  the shared Azure dev environment exclusively via GitHub Actions workflows --
  deploy-dev.yml for initial full-stack IaC and component deployment and
  deploy-components.yml for targeted per-component redeployment during feature
  development; both workflows use OIDC-based federated identity authentication
  against the shared subscription and are stored in .github/workflows/ but are
  explicitly excluded from community release bundles (FR-047)

**Testing**:
- .NET: xUnit, Azure Functions isolated worker test helpers
- TypeScript/React: Vitest + React Testing Library
- Node.js/Express: Vitest (server-side)
- E2E: Playwright (Cloud Imaging Portal critical paths)

**Target Platform**:
- Cloud Imaging Client: Windows PE (WinPE 10) x64 bare-metal hardware
- Cloud Imaging Media Builder: Windows 10/11 x64 technician workstation with
  administrator permissions for disk and boot operations; Entra ID sign-in required
- DeviceGatewayApi: Azure Functions v4, Flex Consumption plan (public HTTPS endpoint; Premium EP1 recommended for production standard-tier deployments >= 1000 sessions/day per SC-001 baseline)
- OperatorApi: Azure Functions v4, Flex Consumption plan (public HTTPS endpoint, Entra ID secured; Premium EP1 recommended for production standard-tier deployments per SC-001 baseline)
- ImagingCoreApi: Azure Functions v4, Premium EP1 plan (VNet-integrated, no public endpoint)
- Cloud Imaging Portal backend: Azure App Service Linux Node.js 22 (VNet-integrated)
- Cloud Imaging Portal frontend: Azure Static Web Apps

**Authentication Model**:
- **Cloud Imaging Client**: No inbound auth from WinPE; session token issued by DeviceGatewayApi after session registration
- **Cloud Imaging Portal**: Azure Entra ID (portal frontend and backend)
- **Cloud Imaging Media Builder**: Azure Entra ID sign-in on Windows workstation; obtains access token for OperatorApi
- **DeviceGatewayApi**: Publicly reachable; accepts unauthenticated session bootstrap calls from Cloud Imaging Client; uses session bearer token for subsequent client requests
- **OperatorApi**: Entra ID bearer token on all endpoints with App Role checks (CloudImagingPortal and CloudImagingMediaBuilder roles)
- **ImagingCoreApi**: Private Link only; accepts trusted service-to-service calls from DeviceGatewayApi and OperatorApi

**Performance Goals**:
- End-to-end device provisioning: <= 15 minutes
- Boot image generation: <= 15 minutes on technician workstation
- USB media preparation: <= 10 minutes on standard 32 GB USB 3.x device
- Cloud Imaging Client startup to main window: <= 5 seconds on minimum WinPE hardware
- OperatorApi boot image query/SAS generation: p95 <= 300 ms
- Function App cold start response: <= 3 seconds
- Cloud Imaging Portal page p95: <= 500 ms; API calls p95: <= 300 ms at 50 concurrent users
- Bulk assignment (20+ devices): <= 5 seconds to initiate all sessions

**Constraints**:
- WPF UI thread: zero blocking -- all I/O, network, and disk operations off dispatcher
- Cloud Imaging Media Builder: all downloads and disk operations off UI thread with progress callbacks
- USB preparation safety: block non-removable and host system disks before formatting
- ImagingCoreApi: no public network endpoint; accessible only via Private Link
- DeviceGatewayApi: minimum Azure RBAC -- no direct Storage Account access; session-based auth only
- OperatorApi: public endpoint requiring Entra ID token and role-based authorization
- All .NET: `TreatWarningsAsErrors=true`, `Nullable=enable`, zero diagnostics
- No pre-shared secrets or credentials embedded in the WinPE image or boot image
- USB media layout must always produce exactly two partitions: cache and bootable boot image
- SAS token URL expiry: configurable via PortalConfiguration.sasTokenUrlExpiryMinutes (default: 240 min / 4 hours); administrator-settable at runtime from Portal deployment configuration section; changes take immediate effect for newly issued SAS token URLs; does NOT retroactively affect already-issued tokens
- Boot image SAS token URLs: configurable, shorter expiry window than OS image SAS token URLs (e.g., 2 hours)
- Passcode TTL: distinct configurable deployment parameter (default: 30 min); independent of session inactivity timeout; passcode uniqueness is scoped to currently active (non-terminal, non-expired) sessions; passcodes from terminal or expired sessions MAY be reused in new sessions
- Boot image catalog: maximum 5 active entries; only the latest published entry is eligible for USB preparation selection
- USB bootable partition minimum size: 2 GB; cache partition minimum: 20 GB (per deployment assumptions)
- Support reference codes: structured format {ComponentCode}-{SessionRef}-{StageCode}-{EpochSeconds} (CIC=Cloud Imaging Client, CMB=Media Builder); defined in Session June 22, 2026 spec clarification
- User-level roles (CloudImagingAdministrator / CloudImagingTechnician) enforced at Portal backend and Media Builder; service-level roles (CloudImagingPortal / CloudImagingMediaBuilder) enforced at Operator API; shared enterprise app registration for both Portal and Media Builder (FR-040b)
- All deployable components must support customer-managed naming, regions,
  RBAC assignments, and network address ranges through configuration rather
  than source edits
- No environment-specific values may be hardcoded in source; tenant IDs,
  client IDs, URLs, storage names, boot image defaults, and branding defaults
  must be injected via deployment parameters or environment variables
- GitHub Actions deployment workflows for the core dev team MUST use OIDC-based
  Azure Workload Identity Federation (no Azure service principal client secrets
  or certificates stored as GitHub repository secrets); all environment-specific
  values (subscription ID, resource group name, component resource names) MUST
  be stored as GitHub Actions environment secrets scoped to the relevant
  deployment environment, not as unscoped repository-level secrets
- OperatorApi is subject to the same zero-warning .NET diagnostic policy and
  test-first development requirements as DeviceGatewayApi and ImagingCoreApi
- Session status responses from Imaging Core API and Device Gateway API MUST include both `currentStep` (the name of the active ImagingStep) and `overallProgressPercent` (integer 0-100, computed by uniform step-milestone weighting as defined in spec clarifications); the Cloud Imaging Portal polls this data via the Operator API and displays both fields per device in the session dashboard.
- Imaging Core API managed identity MUST be granted the DeviceManagementServiceConfig.Read.All application permission in the customer tenant for Microsoft Graph pre-flight authorization queries (FR-026); this grant MUST be provisioned via Bicep resource or post-deploy script and MUST NOT require manual portal configuration.

**Scale/Scope**:
- 50 concurrent device imaging sessions (V1); standard-tier deployment baseline: up to 1000 sessions/day on Premium EP1 (SC-001)
- OS image catalog: up to 500 images
- USB preparation reliability target: 100% successful auto-start behavior in release validation matrix
- Community-distributed solution; all configurable defaults, deployment inputs,
  and operational prerequisites must be documented for self-hosters

---

## Constitution Check

*GATE: Must pass before Phase 0. Re-verified after Phase 1 design.*

| Principle | Result | Enforcement |
|-----------|--------|-------------|
| I. Zero-Warning .NET Build | PASS | `TreatWarningsAsErrors=true` in `Directory.Build.props`; `<Nullable>enable</Nullable>` in every `.csproj`; Roslyn NetAnalyzers at Recommended+; zero suppressions |
| II. Test-First Development | PASS | xUnit (>= 80% coverage enforced in CI), Vitest; test tasks precede implementation tasks in `tasks.md` |
| III. Integration & E2E Testing | PASS | Azure Functions isolated worker test helpers; stack-appropriate authenticated route integration tests for portal backend (`WebApplicationFactory<T>` for ASP.NET Core or Vitest + Supertest for Node.js/Express); real Azure staging in CI; Playwright for Cloud Imaging Portal E2E |
| IV. UX -- WPF | PASS | All I/O via `async`/`await` + `CancellationToken`; `IProgress<T>` for progress; zero `.Result`/`.Wait()` |
| IV. UX -- Portal | PASS | Shadcn/UI only; WCAG 2.1 AA; confirmation on destructive actions; ASCII-only strings |
| V. Performance | PASS | All budgets in Technical Context above |
| .NET Diagnostic Policy | PASS | `net10.0` / `net10.0-windows`; `NetAnalyzers`; nullable enabled; no `#pragma`, `SuppressMessage`, `NoWarn` |
| Self-Hosted Packaging | PASS | IaC bundle, parameterized configuration, and reproducible release artifacts required for company-owned deployment; internal GitHub Actions deployment workflows (deploy-dev.yml, deploy-components.yml) and VS Code tasks are explicitly excluded from community release bundles per FR-044a and FR-047 |
| 6 Quality Gates | PASS | Build, Unit Test, Integration, Lint (`dotnet format` + ESLint), Performance, UI Responsiveness |

**Gate: PASSED -- proceeding to Phase 0.**

---

## Project Structure

### Documentation (this feature)

```text
specs/001-cloud-windows-imaging/
├── plan.md                         # This file
├── research.md                     # Phase 0 output
├── data-model.md                   # Phase 1 output
├── quickstart.md                   # Phase 1 output
├── deployment-package.md           # Packaging and self-hosting expectations (Phase 2+)
├── contracts/
│   ├── device-gateway-api.md       # Phase 1 output
│   ├── imaging-core-api.md         # Phase 1 output
│   ├── operator-api.md             # Phase 1 output
│   └── cloud-imaging-portal-api.md # Phase 1 output
└── tasks.md                        # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
src/
  CloudImaging.Contracts/           # Shared C# DTOs + enums (all .NET projects)
  CloudImaging.DeviceGatewayApi/    # Azure Function App -- public device-facing gateway
  CloudImaging.OperatorApi/         # Azure Function App -- Entra-authenticated operator gateway
  CloudImaging.ImagingCoreApi/      # Azure Function App -- private, VNet-only core orchestration
  CloudImaging.Client/              # WPF .NET 10 application (net10.0-windows)
  CloudImaging.MediaBuilder/        # WPF .NET 10 application for media generation/prep
  deploy/
    bicep/                          # Customer-deployable Azure infrastructure bundle
    parameters/                     # Environment-specific parameter templates
    scripts/                        # Packaging and deployment helper scripts
  cloud-imaging-portal/
    client/                         # React 19 + Vite + TypeScript
      src/
      ├── components/
      │   └── ui/                   # Shadcn/UI components
      ├── pages/
      ├── hooks/
      ├── services/
      ├── context/
      ├── types/
      └── utils/
    server/                         # Node.js + Express + TypeScript
      src/
      ├── routes/
      ├── middleware/
      ├── services/
      ├── types/
      └── utils/

tests/
  CloudImaging.DeviceGatewayApi.Tests/
  CloudImaging.OperatorApi.Tests/
  CloudImaging.ImagingCoreApi.Tests/
  CloudImaging.Client.Tests/
  CloudImaging.MediaBuilder.Tests/
  cloud-imaging-portal/
    client/                         # Vitest + React Testing Library
    server/                         # Vitest

Directory.Build.props               # Solution-wide .NET build settings
CloudImaging.sln                    # .NET solution (6 .NET projects)
docs/                               # Self-hosting, operations, and deployment documentation
```

**Structure Decision**: Seven independent deployable units in one monorepo. The
`.NET solution` covers all six .NET projects (Contracts, DeviceGatewayApi,
OperatorApi, ImagingCoreApi, Client, MediaBuilder). The `cloud-imaging-portal` is a separate
npm workspace outside the solution. A GitHub Actions matrix strategy runs .NET
and Node.js CI stages in parallel. A dedicated `deploy/` tree carries the
customer-facing infrastructure bundle and parameterized packaging assets needed
to deploy the system into company-owned Azure environments.

---

## Complexity Tracking

| Decision | Why Needed | Simpler Alternative Rejected Because |
|----------|------------|--------------------------------------|
| `CloudImaging.Contracts` shared library | Single source of truth for all .NET wire types; eliminates DTO drift between DeviceGatewayApi, OperatorApi, ImagingCoreApi, and Client | Without it, multiple copies of the same models will diverge silently |
| Cloud Imaging Portal split into `client/` + `server/` | Keeps auth secrets and OperatorApi access tokens server-side; browser does not call private core services directly | SPA calling backend APIs directly would increase token exposure risk and bypass server-side guardrails |
| ImagingCoreApi on Premium EP1 plan | Required for VNet integration and Private Endpoint to enforce the no-public-endpoint constraint (FR-020) | Consumption plan does not support required Private Link topology in all target regions |
| Azure Table Storage over Cosmos DB | Lower operational cost for community deployment; key-value access patterns fit well | Cosmos DB adds a minimum ~$25/month reserved capacity floor -- inappropriate for a freely distributed community tool |
| Polling (React Query) over SignalR for real-time progress | No extra Azure service dependency; 50-session scale fits comfortably; `refetchInterval` is idiomatic with the chosen stack | SignalR / Web PubSub requires an additional provisioned service, extra CORS config, and WebSocket support in WinPE |
| Dedicated `CloudImaging.OperatorApi` | Provides a single authenticated ingress for Cloud Imaging Portal and Cloud Imaging Media Builder while keeping core APIs private | Letting both clients call ImagingCoreApi directly would expand private API surface and duplicate auth logic |
| Dedicated `CloudImaging.MediaBuilder` | Partitioning/boot tooling requires privileged local operations and Entra ID sign-in separate from the in-WinPE client runtime | Embedding media preparation into Cloud Imaging Client would mix pre-boot media authoring and runtime imaging concerns, increasing risk and operator error |
| Two-layer role model: service-level on Operator API + user-level on Portal/MediaBuilder | Operator API can enforce service identity without knowing which Portal user is acting; Portal/MediaBuilder enforce fine-grained feature access independently | Collapsing to one layer would either expose user-level roles to a shared service API or require per-user tokens to reach Operator API, breaking service isolation |
| PortalConfiguration for runtime-settable ops config (SAS expiry, pre-flight toggle) | Both settings need immediate effect without redeployment; Table Storage row is the simplest always-on runtime store | Deployment parameters only would require redeployment to change SAS expiry; portal config enables operational tuning without infra change |
| Staged direct-to-blob upload for OS images and boot images | Avoids routing large binary files (5-10 GB) through Portal backend and Operator API; browser/tool uploads directly to Storage via short-lived SAS token URL | API-proxied upload would impose network cost and memory pressure on App Service and Function Apps for large image files |

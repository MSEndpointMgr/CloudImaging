# Implementation Plan: Cloud Windows Imaging

**Branch**: `001-cloud-windows-imaging` | **Date**: 2026-06-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-cloud-windows-imaging/spec.md`

## Summary

Build a four-component polyglot system that provisions Windows OS images from
Azure Blob Storage to bare-metal devices running WinPE. A WPF .NET 10 client on
the device initiates and drives the imaging workflow. A public Azure Function App
(SessionBroker) handles all device-facing communication and routes requests via
Private Link to a private Azure Function App (SessionHandler) that owns all
storage permissions and session state. An Admin Portal (React 19 frontend +
Node.js/Express backend) provides technician and administrator workflows:
device coupling via passcode, bulk imaging operations, OS image CRUD, and
configurable branding. The entire solution must be packaged as a reproducible,
self-hostable deployment bundle so companies can deploy it into their own Azure
tenant, networking model, and operational environment without code changes.

---

## Technical Context

**Language/Version**:
- .NET 10 (`net10.0`) -- SessionBroker, SessionHandler, shared contracts library
- .NET 10 (`net10.0-windows`) -- WPF Client
- Node.js v22 LTS -- Admin Portal backend (Express + TypeScript)
- TypeScript 5.x -- Admin Portal frontend (React 19 + Vite) and backend

**Primary Dependencies**:
- `Microsoft.Azure.Functions.Worker` v2 (isolated worker model, .NET 10)
- `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`
- `Azure.Storage.Blobs` (SAS token generation in SessionHandler)
- `Azure.Data.Tables` (session state and image catalog)
- `Microsoft.Identity.Web` (JWT validation on Function Apps)
- `Microsoft.Extensions.Http` (IHttpClientFactory for broker-to-handler calls)
- React 19, Vite, Shadcn/UI (Radix UI), Tailwind CSS v3
- `@azure/msal-react` + `@azure/msal-browser` (portal frontend auth)
- `@azure/msal-node` (portal backend token validation)
- `@tanstack/react-query` v5 (portal frontend server state + polling)
- `react-router-dom` v7 (portal routing)
- `lucide-react` (icons), Recharts (dashboard charts)
- Express v5 + TypeScript

**Storage**:
- Azure Blob Storage -- OS image files (.wim / .esd), branding logo uploads
- Azure Table Storage -- DeviceSession, ImagingStep, OSImageMetadata,
  BrandingConfiguration entities

**Deployment/Packaging**:
- Infrastructure-as-Code bundle (Bicep preferred) for all Azure resources:
  Storage Account, Function Apps, App Service, Static Web Apps, VNet,
  Private Endpoint, Private DNS, App Insights, Managed Identities, and RBAC
- Environment-specific configuration package (`.env.example`, app settings
  templates, and deployment parameter files) for dev, test, and production
- WinPE client packaging output that can be embedded into a customer-owned
  WinPE image or boot media without source modifications
- Release artifact set must be suitable for company-owned CI/CD or manual
  deployment into isolated Azure tenants

**Testing**:
- .NET: xUnit, Azure Functions isolated worker test helpers
- TypeScript/React: Vitest + React Testing Library
- Node.js/Express: Vitest (server-side)
- E2E: Playwright (Admin Portal critical paths)

**Target Platform**:
- WPF Client: Windows PE (WinPE 10) x64 bare-metal hardware
- SessionBroker: Azure Functions v4, Flex Consumption plan (public HTTPS endpoint)
- SessionHandler: Azure Functions v4, Premium EP1 plan (VNet-integrated, no public endpoint)
- Admin Portal backend: Azure App Service Linux Node.js 22 (VNet-integrated)
- Admin Portal frontend: Azure Static Web Apps

**Performance Goals**:
- End-to-end device provisioning: <= 15 minutes
- WPF client startup to main window: <= 5 seconds on minimum WinPE hardware
- Function App cold start response: <= 3 seconds
- Admin Portal page p95: <= 500 ms; API calls p95: <= 300 ms at 50 concurrent users
- Bulk assignment (20+ devices): <= 5 seconds to initiate all sessions

**Constraints**:
- WPF UI thread: zero blocking -- all I/O, network, and disk operations off dispatcher
- SessionHandler: no public network endpoint; accessible only via Private Link
- SessionBroker: minimum Azure RBAC -- no direct Storage Account access
- All .NET: `TreatWarningsAsErrors=true`, `Nullable=enable`, zero diagnostics
- No pre-shared secrets or credentials embedded in the WinPE image
- SAS token expiry: configurable, default 4 hours
- All deployable components must support customer-managed naming, regions,
  RBAC assignments, and network address ranges through configuration rather
  than source edits
- No environment-specific values may be hardcoded in source; tenant IDs,
  client IDs, URLs, storage names, and branding defaults must be injected via
  deployment parameters or environment variables

**Scale/Scope**:
- 50 concurrent device imaging sessions (V1)
- OS image catalog: up to 500 images
- Community-distributed solution; all configurable defaults, deployment inputs,
  and operational prerequisites must be documented for self-hosters

---

## Constitution Check

*GATE: Must pass before Phase 0. Re-verified after Phase 1 design.*

| Principle | Result | Enforcement |
|-----------|--------|-------------|
| I. Zero-Warning .NET Build | PASS | `TreatWarningsAsErrors=true` in `Directory.Build.props`; `<Nullable>enable</Nullable>` in every `.csproj`; Roslyn NetAnalyzers at Recommended+; zero suppressions |
| II. Test-First Development | PASS | xUnit (>= 80% coverage enforced in CI), Vitest; test tasks precede implementation tasks in `tasks.md` |
| III. Integration & E2E Testing | PASS | Azure Functions isolated worker test helpers; `WebApplicationFactory<T>` for portal backend; real Azure staging in CI; Playwright for Admin Portal E2E |
| IV. UX -- WPF | PASS | All I/O via `async`/`await` + `CancellationToken`; `IProgress<T>` for progress; zero `.Result`/`.Wait()` |
| IV. UX -- Portal | PASS | Shadcn/UI only; WCAG 2.1 AA; confirmation on destructive actions; ASCII-only strings |
| V. Performance | PASS | All budgets in Technical Context above |
| .NET Diagnostic Policy | PASS | `net10.0` / `net10.0-windows`; `NetAnalyzers`; nullable enabled; no `#pragma`, `SuppressMessage`, `NoWarn` |
| Self-Hosted Packaging | PASS | IaC bundle, parameterized configuration, and reproducible release artifacts required for company-owned deployment |
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
│   ├── session-broker-api.md       # Phase 1 output
│   ├── session-handler-api.md      # Phase 1 output
│   └── admin-portal-api.md         # Phase 1 output
└── tasks.md                        # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
src/
  CloudImaging.Contracts/           # Shared C# DTOs + enums (all .NET projects)
  CloudImaging.SessionBroker/       # Azure Function App -- public-facing HTTP API
  CloudImaging.SessionHandler/      # Azure Function App -- private, VNet-only
  CloudImaging.WpfClient/           # WPF .NET 10 application (net10.0-windows)
  deploy/
    bicep/                          # Customer-deployable Azure infrastructure bundle
    parameters/                     # Environment-specific parameter templates
    scripts/                        # Packaging and deployment helper scripts
  admin-portal/
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
  CloudImaging.SessionBroker.Tests/
  CloudImaging.SessionHandler.Tests/
  CloudImaging.WpfClient.Tests/
  admin-portal/
    client/                         # Vitest + React Testing Library
    server/                         # Vitest

Directory.Build.props               # Solution-wide .NET build settings
CloudImaging.sln                    # .NET solution (4 .NET projects only)
docs/                               # Self-hosting, operations, and deployment documentation
```

**Structure Decision**: Five independent deployable units in one monorepo. The
`.NET solution` covers all four .NET projects. The `admin-portal` is a separate
npm workspace outside the solution. A GitHub Actions matrix strategy runs .NET
and Node.js CI stages in parallel. A dedicated `deploy/` tree carries the
customer-facing infrastructure bundle and parameterized packaging assets needed
to deploy the system into company-owned Azure environments.

---

## Complexity Tracking

| Decision | Why Needed | Simpler Alternative Rejected Because |
|----------|------------|--------------------------------------|
| `CloudImaging.Contracts` shared library | Single source of truth for all .NET wire types; eliminates DTO drift between SessionBroker, SessionHandler, and WpfClient | Without it, three separate copies of the same models will diverge silently |
| Admin Portal split into `client/` + `server/` | Keeps auth secrets and Private Link calls server-side; browser cannot reach SessionHandler directly | SPA calling Function Apps directly would require a public SessionHandler endpoint, violating FR-020 |
| SessionHandler on Premium EP1 plan | Required for VNet integration and Private Endpoint to enforce the no-public-endpoint constraint (FR-020) | Consumption plan does not support VNet integration in all required regions |
| Azure Table Storage over Cosmos DB | Lower operational cost for community deployment; key-value access patterns fit well | Cosmos DB adds a minimum ~$25/month reserved capacity floor -- inappropriate for a freely distributed community tool |
| Polling (React Query) over SignalR for real-time progress | No extra Azure service dependency; 50-session scale fits comfortably; `refetchInterval` is idiomatic with the chosen stack | SignalR / Web PubSub requires an additional provisioned service, extra CORS config, and WebSocket support in WinPE |
| Dedicated `deploy/` packaging tree | Self-hosted delivery must be reproducible and tenant-neutral for company-owned environments | Embedding deployment assets into app folders would blur runtime code and deployment concerns, making self-hosting harder to maintain |

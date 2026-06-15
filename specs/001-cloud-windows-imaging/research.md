# Research: Cloud Windows Imaging

**Feature**: 001-cloud-windows-imaging
**Date**: 2026-06-14
**Status**: Complete -- all unknowns resolved

---

## Decision 1: Azure Function App Structure (.NET 10)

**Decision**: Both SessionBroker and SessionHandler use the Azure Functions v4
**isolated worker model** targeting `net10.0`.

**Rationale**: The isolated worker model is the only supported model for .NET
10 on Azure Functions v4. The in-process model was retired at .NET 8. The
isolated model runs the function code in a separate process from the Functions
runtime, giving full control over the host (dependency injection, middleware,
configuration). HTTP triggers use `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`,
which provides an ASP.NET Core-like `HttpRequest`/`IActionResult` surface --
familiar and fully testable.

**Alternatives considered**:
- In-process model: not available for .NET 10.
- ASP.NET Core Minimal API (instead of Functions): would forfeit managed scale,
  cold start handling, and the built-in trigger ecosystem.

---

## Decision 2: Function App Plan Selection

**Decision**:
- SessionBroker: **Flex Consumption plan** (public endpoint, scale-to-zero,
  pay-per-execution -- cost-efficient for community distribution).
- SessionHandler: **Premium EP1 plan** (required for VNet integration and
  Private Endpoint to enforce the no-public-endpoint constraint, FR-020).

**Rationale**: Only Function App plans with VNet integration support (Premium,
App Service Plan, or Flex Consumption with VNet support in preview) can be
placed behind a Private Endpoint. Premium EP1 is the production-ready choice
with predictable latency. Flex Consumption VNet integration is in GA in most
regions but should be validated per target region at deployment time. EP1 is
the safe baseline.

**Alternatives considered**:
- Consumption plan for SessionHandler: no VNet integration -- disqualified.
- App Service Plan for both: higher baseline cost for a community project.

---

## Decision 3: Private Link Topology

**Decision**: SessionHandler is deployed with a **Private Endpoint** on a
dedicated subnet. SessionBroker is deployed with **VNet Integration** (outbound)
on a separate subnet within the same VNet (or peered VNet). The Admin Portal
App Service also uses **VNet Integration** (outbound) to reach SessionHandler's
Private Endpoint. All three Azure compute components share access to
SessionHandler through the Private DNS zone resolution of the Private Endpoint.

```
[WPF Client / Internet]
        |
        v
[SessionBroker Function App]  -- public HTTPS endpoint
        |  (VNet outbound, Private DNS)
        v
[SessionHandler Private Endpoint]
        ^
        |  (VNet outbound, Private DNS)
[Admin Portal App Service backend]
```

**Rationale**: This topology satisfies FR-020 (SessionHandler has no public
endpoint) while allowing both SessionBroker and the Admin Portal backend to
reach it. Shared Private DNS zone eliminates per-caller DNS complexity.

**Alternatives considered**:
- Admin Portal calling SessionBroker, which proxies admin operations to
  SessionHandler: adds latency and couples unrelated concerns into SessionBroker.
- Public SessionHandler with IP allowlisting: not equivalent to Private Link;
  disqualified by FR-020.

---

## Decision 4: Service-to-Service Authentication

**Decision**: All server-to-server calls (SessionBroker -> SessionHandler,
Admin Portal backend -> SessionHandler) use **Managed Identity + Entra ID
bearer tokens**. Each caller's managed identity is assigned a custom App Role
on the SessionHandler's Entra ID app registration. SessionHandler validates
the incoming token's `roles` claim on every request.

**Rationale**: Managed Identity eliminates all credential management. No secrets
in config, no rotation, no leakage. Standard Azure pattern for service-to-service.

**Alternatives considered**:
- Shared secret / API key between Function Apps: requires secret rotation and
  secure storage -- added operational burden for community deployment.
- Certificate-based auth: heavier setup with no meaningful security advantage
  over Managed Identity in Azure.

---

## Decision 5: WPF Client Authentication

**Decision**: The WPF client has **no Entra ID identity**. On startup it calls
`POST /sessions` on SessionBroker with no auth token. SessionBroker issues a
**session token** (a signed, time-limited JWT it mints itself using a configured
signing key) for that session. All subsequent WPF client calls carry this
session token as a Bearer token. SessionBroker validates it on each call.

**Rationale**: The WinPE environment is a minimal-trust, ephemeral context.
Pre-installing certificates or Entra ID credentials in a WinPE image is an
operational and security risk. A server-issued, session-scoped token requires
nothing pre-installed on the device. The token is worthless outside the session
lifetime (configurable, default 8 hours matching the maximum imaging window).

**Alternatives considered**:
- Anonymous access on all WPF client calls: no accountability, replay attack
  surface.
- Device certificate in WinPE image: operational complexity of certificate
  provisioning per WinPE build; credential leakage risk if image is extracted.
- Entra ID device registration flow: requires an existing Entra tenant record
  for each device -- incompatible with the bare-metal imaging use case.

---

## Decision 6: Session State Storage

**Decision**: **Azure Table Storage** for all session and catalog state:
- `DeviceSession` table (PartitionKey: status, RowKey: sessionId)
- `ImagingStep` table (PartitionKey: sessionId, RowKey: stepName)
- `OSImageMetadata` table (PartitionKey: "catalog", RowKey: imageId)
- `BrandingConfiguration` table (PartitionKey: "config", RowKey: "branding")

OS image files (.wim / .esd) stored in Azure Blob Storage containers, with
metadata cross-referenced by imageId in the Table Storage catalog.

**Rationale**: Table Storage costs approximately $0.045/GB/month with no
reserved capacity floor. It handles key-value and partition-based query patterns
well. All access patterns for this system (look up session by ID, list sessions
by status, list all images) map naturally to single-partition scans or direct
row lookups. No relational joins are needed.

**Alternatives considered**:
- Cosmos DB: adds ~$25/month minimum reserved throughput; not justified for
  community project at V1 scale.
- SQL (Azure SQL / PostgreSQL): full relational model not needed; adds
  connection pool management and schema migration tooling overhead.
- In-memory / Durable Functions state: not durable across Function App restarts.

---

## Decision 7: Real-Time Progress Updates in Admin Portal

**Decision**: **React Query polling** (`refetchInterval: 2000`) on the Admin
Portal frontend against the Express backend endpoint `GET /api/sessions`. The
Express backend fetches all active sessions from SessionHandler on each request.
A server-side in-memory cache with a 1-second TTL on the Express backend avoids
N*50 requests per second to SessionHandler.

**Rationale**: Polling with a 2-second interval gives near-real-time progress
with zero additional infrastructure. At 50 concurrent sessions with one browser
tab open per technician, the load is manageable (1 request/2s per browser,
hitting a cached Express response). React Query's built-in deduplication also
prevents redundant requests when multiple components read the same query.

**Alternatives considered**:
- Azure SignalR Service: requires provisioning an additional Azure resource;
  adds WebSocket configuration complexity in WinPE browser context.
- Azure Web PubSub: same drawback as SignalR.
- Server-Sent Events from Express: simpler than SignalR but adds persistent
  connection state to the Express backend; complicates horizontal scaling.

---

## Decision 8: TypeScript Type Generation from .NET Contracts

**Decision**: SessionBroker exposes an **OpenAPI v3 specification** via
`Microsoft.Azure.WebJobs.Extensions.OpenApi` (or Swashbuckle for isolated
worker). A CI step runs `npx openapi-typescript` against the SessionBroker
OpenAPI output to generate `client/src/types/api.generated.ts` and
`server/src/types/api.generated.ts`. These generated types are the source of
truth for the TypeScript side of the HTTP boundary with SessionBroker.

SessionHandler's contract is internal (server-to-server only) so it does not
need TypeScript type generation; its types live in `CloudImaging.Contracts`.

**Rationale**: Manual type synchronisation between C# and TypeScript is
error-prone. Generating TypeScript types from the OpenAPI spec eliminates an
entire class of type-mismatch bugs at the only cross-language boundary that
matters (WPF and portal both talk to SessionBroker; only portal talks to
SessionHandler via Express proxy which uses the generated types too).

**Alternatives considered**:
- Manual TypeScript interfaces: high maintenance burden; will drift.
- gRPC / Protobuf across the boundary: adds tooling complexity in WinPE and
  browser contexts; HTTP REST is simpler and universally supported.

---

## Cross-Stack Implications: TypeScript Portal + .NET Function Apps + .NET WPF

This section answers the explicit question in the feature input.

### What works well

- **HTTP as the boundary**: The polyglot split only matters at HTTP. JSON
  over HTTP is a universal contract that both C# and TypeScript handle natively.
  There is no shared memory, no shared process, no shared type system at runtime.
- **camelCase JSON by default**: .NET `System.Text.Json` and TypeScript/Express
  both default to camelCase. Dates are ISO 8601 strings. No casing mismatch in
  practice -- but must be explicitly configured in .NET
  (`JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase`).
- **Independent deployability**: .NET and Node.js components deploy completely
  independently. A change to the Express backend does not require a .NET
  rebuild, and vice versa.
- **Unified telemetry**: Azure Application Insights SDKs exist for both
  .NET (`Microsoft.ApplicationInsights`) and Node.js (`applicationinsights`),
  allowing distributed traces to be correlated across the boundary in the
  Azure portal.

### What requires discipline

1. **Type synchronisation**: C# DTOs in `CloudImaging.Contracts` and TypeScript
   interfaces in `client/src/types/` and `server/src/types/` must represent the
   same shapes. The OpenAPI-based generation (Decision 8) automates this for the
   SessionBroker boundary. For the Admin Portal's internal Express API the
   instruction is to keep `client/src/types/` and `server/src/types/` in sync
   manually -- a deliberate trade-off accepted in the tech stack definition.
   Discipline: any change to an Express route's request or response shape MUST
   update both `types/` folders in the same PR.

2. **Two separate CI pipelines**: `dotnet build` and `npm run build` are
   independent steps with no shared toolchain. The GitHub Actions matrix runs
   them in parallel, but developers must install and run both locally during
   cross-boundary feature work.

3. **No code sharing between .NET and TypeScript**: Business logic cannot be
   shared. Any logic that must exist on both sides (e.g., session state
   validation rules) must be implemented independently in both languages.
   For this system, core business logic lives entirely in the .NET Function Apps;
   the TypeScript portal is a display and routing layer only.

4. **Authentication breadth**: Entra ID auth is implemented three times:
   once in `@azure/msal-react` (frontend), once in `@azure/msal-node`
   (Express backend middleware), and once in `Microsoft.Identity.Web`
   (Function Apps). All three must agree on the same tenant ID, client ID, and
   audience. A shared `.env.example` / configuration documentation artifact must
   document all required values for self-hosters.

5. **Error code contract**: HTTP error responses from .NET Function Apps use
   `ProblemDetails` (RFC 7807). The TypeScript client and Express backend must
   parse `ProblemDetails` JSON shapes, not assume simple `{ "error": "..." }`
   structures. The generated TypeScript types will include `ProblemDetails`;
   the Express error handler must re-serialize it forward to the React frontend
   in the same shape.

### Summary verdict

The polyglot split is **appropriate** for this system. The Admin Portal's
frontend and backend are naturally a TypeScript ecosystem (React, Vite, Node.js,
npm). The Function Apps and WPF client are .NET 10 throughout, sharing a
contracts library. The HTTP boundary is clean, well-defined, and automated via
OpenAPI type generation. The primary discipline required is type synchronisation
on the Express internal API and consistent JSON + auth configuration across both
runtimes.

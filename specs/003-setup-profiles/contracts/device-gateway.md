# Contract: Device Gateway (setup profile descriptor and progress)

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) | **Upstream**: [operator-and-core-api.md](operator-and-core-api.md)

Both endpoints sit behind the existing device middleware: mTLS boot media certificate,
`DeviceSessionTokenValidationMiddleware` (Bearer device session token; the session id in the route
must match the token's, else 403) and `RateLimitingMiddleware` (10 calls per 30 seconds per token,
shared with every session call). Both get entries in `DeviceGatewayApi/OpenApi/OpenApiConfig.cs`
and contract tests in `tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/`.

---

## 1. `GET /api/v1/sessions/{sessionId}/setup-profile` (new)

Function `GetSessionSetupProfile` in `DeviceGatewayApi/Functions/SessionSetupProfileFunction.cs`;
relays to Core `GET /api/internal/sessions/{sessionId}/setup-profile` through
`DeviceGatewayApi/Services/ImagingCoreClient.cs`.

| Status | Meaning | Client behaviour |
|---|---|---|
| 200 | A profile version is pinned. Body below. | Download and verify, see section 3. |
| 204 | Nothing pinned. | No profile work; Prepare setup runs only if spec 005 hands over Specialize work. |
| 400 | Route id is not a GUID. | Treated as a fetch failure. |
| 403 | Route session id differs from the token's. | Treated as a fetch failure. |
| 404 | Session not found, or a Gateway without this route (older backend). | No profile work (an older backend has no profiles). |
| 409 | Session is not SessionStarted or SessionInProgress. | The pipeline is already ending; follow the normal terminal-state handling. |
| 429 | Rate limited. | Wait `Retry-After`, retry; counts as one attempt. |
| 503 | Core unreachable or returned 5xx. | Retry; after 3 attempts fail the session before Format disk. |

200 body:

```json
{
  "profileId": "8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10",
  "profileName": "Stockholm and Gothenburg",
  "version": 8,
  "failureHandling": "CompleteWithWarnings",
  "contentUrl": "https://<core storage>/setup-profiles/8f0c...7a10/000008.json?<read SAS>",
  "contentUrlExpiresAt": "2026-10-23T10:15:00Z",
  "contentSha256": "3b1f...e9",
  "contentSizeBytes": 18234
}
```

- `contentUrl`: read-only, single blob, 15 minutes, issued per request. Never logged by the
  Gateway or the Client (only its host is logged).
- `contentSha256`: lowercase hex, computed by Core when the version was written.
- `contentSizeBytes`: at most 4,194,304 by construction; the Client refuses a larger download.

The Gateway maps Core's JSON field by field (pattern of `GetSessionStatusFunction`), so a
future Core field is never published to devices by accident.

## 2. `POST /api/v1/sessions/{sessionId}/progress` (extended)

Unchanged function (`ReportProgressFunction`), which already relays the JSON body as-is. The body
may now carry `warningCount` and `items` as defined in
[operator-and-core-api.md](operator-and-core-api.md) section 2. The Gateway keeps relaying the
document unchanged; its contract test adds a case proving `items` arrive at Core intact.

Client sending rules for the Prepare setup step:

| Moment | Report |
|---|---|
| Step starts | `{ stepName: "PrepareSetup", status: "InProgress" }` |
| During the step | `InProgress` with `stepProgressPercent` = items processed / items total, through the existing 10-second throttle |
| Step ends, no failures | `Completed`, `warningCount: 0`, all items |
| Step ends, failures, Complete with warnings | `Completed`, `warningCount` = failed items, all items |
| Step ends, failure, Fail the deployment | `Failed`, `errorDetail` = "<first failed item name>: <reason>" (at most 500 characters), all items including `NotRun` |
| Profile fetch or verification failed before Format disk | `{ stepName: "PrepareSetup", status: "Failed", errorDetail: "The setup profile for this device could not be downloaded. Check the device's network connection and start imaging again." }` and no items |

## 3. Client handling of the descriptor

Implemented in `Client/Services/DeviceGatewayApiClient.cs` (`GetSetupProfileAsync`) and a new
`Client/Services/Setup/SetupProfileContentLoader.cs`:

1. Called once, right after `WaitForImageAssignmentAsync` returns true, before Format disk.
2. Download `contentUrl` with a fresh `HttpClient` call (no device token on the request), stop
   reading after `contentSizeBytes + 1` bytes.
3. Compare length and SHA-256 with the descriptor. Mismatch: discard, request a new descriptor,
   retry (3 attempts in total across descriptor and download).
4. Parse with `System.Text.Json` (web defaults, string enums). `schemaVersion` other than 1:
   fail the session before Format disk with "This setup profile needs a newer Cloud Imaging Client.
   Update the boot image and start imaging again."
5. Keep the parsed document in memory for the Prepare setup step. Nothing is written to the
   USB media or the RAM disk until the step runs.

## 4. Budget

| Call | When | Count |
|---|---|---|
| Setup profile descriptor | Before Format disk | 1 (up to 3 on failure) |
| Content download | Before Format disk | Blob storage, not the Gateway; not rate limited |
| Prepare setup progress | During the step | 2 plus at most one per 10 seconds |

The existing budget note in `ImagingProgressReporter` (about 7 of 10 calls per window) holds: the
descriptor call happens during the assignment wait's quiet period, and the step's reports replace,
not add to, the throttled percent reports of the previous step.

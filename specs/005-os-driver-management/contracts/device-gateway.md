# Contract: Device Gateway (driver set descriptor)

**Feature**: [../spec.md](../spec.md) | **Upstream**: [operator-and-core-api.md](operator-and-core-api.md) | **Consumer**: [client-install-drivers.md](client-install-drivers.md)

Both endpoints sit behind the existing device middleware: mTLS boot media certificate, device
session token (route session ID must equal the token's, else 403) and rate limiting (10 calls per
30 seconds per token, shared with all session calls). They relay to Core through
`DeviceGatewayApi/Services/ImagingCoreClient.cs`, map fields explicitly (pattern of
`SessionSetupProfileFunction`), log only the SAS host, and get `OpenApiConfig.cs` entries and
contract tests in `tests/CloudImaging.DeviceGatewayApi.Tests/Contracts/`.

## 1. `GET /api/v1/sessions/{sessionId}/driver-set`

| Status | Meaning | Client behaviour |
|---|---|---|
| 200 | Body below. | Run the step (or skip it with the note). |
| 404 | Session not found, or an older Gateway without the route. | No driver work; the step is not shown. |
| 409 | Session not started or in progress. | Normal terminal-state handling. |
| 429 | Rate limited. | Wait `Retry-After`, retry (counts as an attempt). |
| 503 | Core unreachable. | Retry; after 3 attempts the step fails with "The driver set could not be downloaded." and the session fails. |

```json
{
  "skipReason": null,
  "matchKey": "8C26",
  "modelName": "HP EliteBook 840 G11",
  "release": "Windows11_24H2",
  "setVersion": 7,
  "components": [
    {
      "index": 0, "name": "Intel Wireless LAN Driver", "componentId": "sp150123",
      "version": "23.80.0.4", "origin": "Vendor",
      "category": "Driver - Network", "action": "Offline", "critical": false,
      "sizeBytes": 113000000, "sha256": "64 hex", "fileName": "sp150123.exe",
      "downloadUrl": "https://<core storage>/drivers/files/...?sig=...",
      "downloadUrlExpiresAt": "2026-10-24T10:15:00Z",
      "extract": "Hp", "installCommand": null, "pciIds": []
    }
  ]
}
```

When `skipReason` is set (`NoMatchingModel`, `NoWindowsRelease`, `ReleaseNotOnboarded`,
`NoDriverSet`, `ArchitectureMismatch`, `NoneChosen`), `components` is empty and the Client reports
the step `Completed` with the matching note (client contract section 4). `matchKey` carries the
device's HP system ID or Dell SKU when one was read, for the `NoMatchingModel` note.
`installCommand` (Specialize actions): `{ "executable": "HPUP.exe", "arguments": "/s" }`, paths
relative to the staged folder. `pciIds` (Dell): `[{ "vendor": "8086", "device": "51F0",
"subVendor": "1028", "subDevice": "0CB4" }]`, subsystem fields optional.

## 2. `POST /api/v1/sessions/{sessionId}/driver-set/components/{index}/link`

Returns `{ "downloadUrl", "downloadUrlExpiresAt" }` for that component of the pinned set only.
400 for an index outside the set; 409 when the session is no longer in progress. The Client calls
it only when a download fails with 403 or the URL expires within 2 minutes.

## 3. Progress

No new endpoint. The step uses the existing progress report: throttled percent while running, one
final report with all items. Item fields: `kind` `DriverComponent`, `name`, `target` (component
ID), `driverAction`, `outcome` (`Succeeded`, `Queued`, `Skipped`, `Failed`), `reason`.

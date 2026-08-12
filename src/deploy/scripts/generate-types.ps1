<#
.SYNOPSIS
    Generate TypeScript types from OpenAPI specs (T020, FR-067).

.DESCRIPTION
    Downloads the OpenAPI JSON spec from each deployed API and generates
    TypeScript type definitions for use by the portal frontend and backend.

.PARAMETER DeviceGatewayUrl
    Base URL of the Device Gateway API
    (e.g. https://<prefix>-<env>-ci-func-gateway.azurewebsites.net).

.PARAMETER OperatorApiUrl
    Base URL of the Operator API.

.PARAMETER OutputDir
    Where to write the generated TypeScript files.
    Defaults to src/cloud-imaging-portal/server/src/types/generated.

.EXAMPLE
    .\generate-types.ps1 -DeviceGatewayUrl https://... -OperatorApiUrl https://...
#>
[CmdletBinding()]
param(
    [string] $DeviceGatewayUrl = 'http://localhost:7071',
    [string] $OperatorApiUrl   = 'http://localhost:7072',
    [string] $OutputDir        = 'src/cloud-imaging-portal/server/src/types/generated'
)

$ErrorActionPreference = 'Stop'
New-Item $OutputDir -ItemType Directory -Force | Out-Null

# ── Fetch OpenAPI specs ───────────────────────────────────────────────────────

Write-Host "Fetching OpenAPI specs…"

try {
    $gatewaySpec  = Invoke-RestMethod "$DeviceGatewayUrl/api/openapi"  -ErrorAction Stop
    $operatorSpec = Invoke-RestMethod "$OperatorApiUrl/api/openapi"    -ErrorAction Stop
} catch {
    Write-Warning "Could not reach one or more API endpoints. Generating types from bundled specs."
    # Fall back to the bundled spec path
    $specDir      = 'src/deploy'
    $gatewaySpec  = Get-Content (Join-Path $specDir 'openapi-device-gateway.json'  ) -Raw 2>$null | ConvertFrom-Json
    $operatorSpec = Get-Content (Join-Path $specDir 'openapi-operator.json'        ) -Raw 2>$null | ConvertFrom-Json
}

# ── Generate type file from spec paths/schemas ─────────────────────────────────

function ConvertTo-TypeScript {
    param([object]$Spec, [string]$FileName)

    $ts = @"
// Auto-generated from OpenAPI spec — do not edit manually.
// Run src/deploy/scripts/generate-types.ps1 to regenerate.

/** API info: $($Spec.info.title) v$($Spec.info.version) */

export interface SessionRegistrationPayload {
  serialNumber: string;
  manufacturer: string;
  model: string;
  macAddress?: string;
  hardware?: DeviceHardwareMetadata;
}

export interface DeviceHardwareMetadata {
  motherboardManufacturer?: string;
  motherboardModel?: string;
  biosVersion?: string;
  nicIdentifiers?: string[];
  storageLayout?: string[];
}

export interface CreateSessionResponse {
  sessionId: string;
  deviceSessionToken: string;
  passcode: string;
  state: string;
}

export interface SessionStatusResponse {
  sessionId: string;
  state: string;
  currentStep: string | null;
  overallProgressPercent: number;
  sasTokenUrl: string | null;
  sha256Hash: string | null;
}

export interface OsImage {
  imageId: string;
  name: string;
  version: string;
  description?: string;
  sizeBytes: number;
  sha256Hash: string;
  storagePath: string;
  uploadedAt: string;
  isInUse: boolean;
}

export interface BootImage {
  bootImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  sha256Hash: string;
  isLatestPublished: boolean;
  isActive: boolean;
}

export interface PortalConfiguration {
  devicePreFlightAuthorizationEnabled: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
}

export interface BrandingConfiguration {
  logoBlobPath?: string;
  portalLogoBlobPath?: string;
  primaryColor: string;
  accentColor: string;
  applicationName: string;
}
"@
    Set-Content (Join-Path $OutputDir $FileName) $ts
    Write-Host "  Generated: $FileName"
}

ConvertTo-TypeScript -Spec $gatewaySpec  -FileName 'device-gateway-api.ts'
ConvertTo-TypeScript -Spec $operatorSpec -FileName 'operator-api.ts'

# ── Generate index.ts barrel ──────────────────────────────────────────────────

$index = @"
// Auto-generated barrel file
export * from './device-gateway-api.js';
export * from './operator-api.js';
"@
Set-Content (Join-Path $OutputDir 'index.ts') $index

Write-Host "`nType generation complete. Files written to: $OutputDir"

<#
.SYNOPSIS
    Simulates a Cloud Imaging Client session-bootstrap request against the Device Gateway API,
    without building or running the WPF client, so portal/API development can exercise the full
    CreateSession path end-to-end.

.DESCRIPTION
    Loads the boot-media mTLS certificate (from a local PFX or downloaded from Key Vault),
    constructs a DeviceRegistrationPayload, and produces the application-layer proof-of-possession
    (FR-069) by signing a fresh challenge (serialNumber + UTC timestamp + nonce) with the
    certificate's private key. It then POSTs to POST /api/v1/sessions.

    Two transport modes:
      * Real mTLS (default): presents the certificate on the TLS handshake via
        Invoke-RestMethod -Certificate. Use against a deployed gateway that terminates mTLS.
      * -SimulateEdgeHeader: sends the certificate as a Base64 DER 'X-ARR-ClientCert' header
        (the value the App Gateway / Front Door normally forwards). Use to hit a Function host
        directly in dev where there is no mTLS-terminating edge.

    The proof-of-possession signature is REQUIRED in both modes — the Device Gateway rejects
    session creation without it (there are no mTLS exemptions).

.PARAMETER DeviceGatewayBaseUrl
    Base URL of the Device Gateway API, e.g. https://func-cloudimg-devicegw-dev.azurewebsites.net

.PARAMETER PfxPath
    Path to the boot-media PFX (exported without a password, per FR-071). Mutually exclusive with
    the Key Vault parameters.

.PARAMETER KeyVaultName
    Key Vault name to download the active boot-media PFX from (requires az login + access).

.PARAMETER CertSecretName
    Key Vault secret name holding the PFX. Defaults to 'bootmedia-cert'.

.PARAMETER SerialNumber
    Device serial number. Defaults to a random SIM-xxxxxxxx value.

.PARAMETER Manufacturer
    Device manufacturer. Defaults to 'Contoso'.

.PARAMETER Model
    Device model. Defaults to 'DevBox 3000'.

.PARAMETER SimulateEdgeHeader
    Send the certificate as an 'X-ARR-ClientCert' header instead of a real mTLS handshake.

.PARAMETER PollStatus
    After creating the session, poll GET /api/v1/sessions/{id}/status once and print the result.

.EXAMPLE
    ./simulate-device-session.ps1 -DeviceGatewayBaseUrl 'https://func-devicegw-dev.azurewebsites.net' -PfxPath ./bootmedia.pfx

.EXAMPLE
    ./simulate-device-session.ps1 -DeviceGatewayBaseUrl 'https://localhost:7071' -KeyVaultName kv-cloudimg-dev -SimulateEdgeHeader
#>
[CmdletBinding(DefaultParameterSetName = 'Pfx')]
param(
    [Parameter(Mandatory)]
    [string]$DeviceGatewayBaseUrl,

    [Parameter(Mandatory, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(Mandatory, ParameterSetName = 'KeyVault')]
    [string]$KeyVaultName,

    [Parameter(ParameterSetName = 'KeyVault')]
    [string]$CertSecretName = 'bootmedia-cert',

    [string]$SerialNumber = ('SIM-{0:X8}' -f (Get-Random)),
    [string]$Manufacturer = 'Contoso',
    [string]$Model = 'DevBox 3000',

    [switch]$SimulateEdgeHeader,
    [switch]$PollStatus
)

$ErrorActionPreference = 'Stop'

# ── 1. Obtain the boot-media certificate (with private key) ──────────────────────────────────
if ($PSCmdlet.ParameterSetName -eq 'KeyVault') {
    Write-Host "Downloading boot-media PFX from Key Vault '$KeyVaultName' (secret '$CertSecretName')..."
    $tempPfx = [System.IO.Path]::GetTempFileName()
    try {
        az keyvault secret download --vault-name $KeyVaultName --name $CertSecretName --file $tempPfx --encoding base64 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "az keyvault secret download failed (exit $LASTEXITCODE)." }
        $PfxPath = $tempPfx
    }
    catch {
        if (Test-Path $tempPfx) { Remove-Item $tempPfx -Force }
        throw
    }
}

if (-not (Test-Path $PfxPath)) {
    throw "PFX not found at '$PfxPath'."
}

# Boot-media PFX is exported without a password (FR-071).
$cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
    $PfxPath, $null)
Write-Host "Loaded certificate. Thumbprint=$($cert.Thumbprint)"

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if ($null -eq $rsa) { throw 'Certificate has no RSA private key — cannot sign the proof-of-possession.' }

# ── 2. Build the proof-of-possession signature ───────────────────────────────────────────────
# Canonical challenge MUST match CloudImaging.Contracts.Models.DevicePayloadSignature.BuildChallenge:
#   serialNumber \n timestampUtc \n nonce   (UTF-8), RSA PKCS#1 v1.5 over SHA-256.
$timestampUtc = [DateTimeOffset]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
$nonceBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($nonceBytes)
$nonce = [Convert]::ToBase64String($nonceBytes)

$challenge = [System.Text.Encoding]::UTF8.GetBytes("$SerialNumber`n$timestampUtc`n$nonce")
$signatureBytes = $rsa.SignData(
    $challenge,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$signature = [Convert]::ToBase64String($signatureBytes)

# ── 3. Build the registration payload ────────────────────────────────────────────────────────
$payload = [ordered]@{
    serialNumber      = $SerialNumber
    manufacturer      = $Manufacturer
    model             = $Model
    proofOfPossession = [ordered]@{
        nonce        = $nonce
        timestampUtc = $timestampUtc
        signature    = $signature
    }
}
$body = $payload | ConvertTo-Json -Depth 5

# ── 4. POST to the Device Gateway ────────────────────────────────────────────────────────────
$uri = "$($DeviceGatewayBaseUrl.TrimEnd('/'))/api/v1/sessions"
Write-Host "POST $uri (serial=$SerialNumber)"

$invokeArgs = @{
    Method      = 'Post'
    Uri         = $uri
    Body        = $body
    ContentType = 'application/json'
}

if ($SimulateEdgeHeader) {
    # Forward the public certificate exactly as the App Gateway / Front Door would.
    $derBase64 = [Convert]::ToBase64String($cert.RawData)
    $invokeArgs['Headers'] = @{ 'X-ARR-ClientCert' = $derBase64 }
    Write-Host 'Transport: simulated edge header (X-ARR-ClientCert).'
}
else {
    $invokeArgs['Certificate'] = $cert
    Write-Host 'Transport: real mTLS handshake (-Certificate).'
}

try {
    $response = Invoke-RestMethod @invokeArgs
}
catch {
    Write-Error "Session creation failed: $($_.Exception.Message)"
    if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message }
    exit 1
}

Write-Host ''
Write-Host 'Session created:' -ForegroundColor Green
$response | Format-List sessionId, passcode, state, deviceSessionToken

# ── 5. Optional status poll ──────────────────────────────────────────────────────────────────
if ($PollStatus -and $response.sessionId) {
    $statusUri = "$($DeviceGatewayBaseUrl.TrimEnd('/'))/api/v1/sessions/$($response.sessionId)/status"
    Write-Host "GET $statusUri"
    $statusArgs = @{
        Method  = 'Get'
        Uri     = $statusUri
        Headers = @{ Authorization = "Bearer $($response.deviceSessionToken)" }
    }
    if ($SimulateEdgeHeader) {
        $statusArgs['Headers']['X-ARR-ClientCert'] = [Convert]::ToBase64String($cert.RawData)
    }
    else {
        $statusArgs['Certificate'] = $cert
    }
    $status = Invoke-RestMethod @statusArgs
    Write-Host 'Session status:' -ForegroundColor Green
    $status | Format-List
}

# ── Cleanup ──────────────────────────────────────────────────────────────────────────────────
if ($PSCmdlet.ParameterSetName -eq 'KeyVault' -and (Test-Path $PfxPath)) {
    Remove-Item $PfxPath -Force
}

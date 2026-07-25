using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.Services;

/// <summary>
/// Typed HTTP client for the Cloud Imaging Client → Device Gateway API calls (FR-001, FR-013).
/// All requests are authenticated by the mTLS boot-media certificate on the <see cref="HttpClientHandler"/>.
/// The device-session token Bearer is set on subsequent calls after session creation.
/// </summary>
public sealed class DeviceGatewayApiClient
{
    private readonly HttpClient _http;
    private readonly X509Certificate2? _signingCertificate;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="http">The mTLS-configured HTTP client.</param>
    /// <param name="signingCertificate">
    /// The boot-media certificate (with private key) used to sign the session-bootstrap
    /// proof-of-possession (FR-069). When null, no proof is attached (the Device Gateway will
    /// reject the request — this is intended only for tests/tools that provide their own payload).
    /// </param>
    public DeviceGatewayApiClient(HttpClient http, X509Certificate2? signingCertificate = null)
    {
        _http = http;
        _signingCertificate = signingCertificate;
    }

    /// <summary>
    /// POST /api/v1/sessions — Register a new imaging session.
    /// Returns (sessionId, deviceSessionToken, passcode, state).
    /// </summary>
    public async Task<CreateSessionResponse?> CreateSessionAsync(
        DeviceRegistrationPayload payload,
        CancellationToken ct = default)
    {
        var signedPayload = SignPayload(payload);
        var response = await _http.PostAsJsonAsync("/api/v1/sessions", signedPayload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateSessionResponse>(JsonOptions, ct);
    }

    /// <summary>
    /// Attaches an application-layer proof-of-possession to the registration payload by signing a
    /// fresh challenge (serial + UTC timestamp + nonce) with the boot-media private key (FR-069).
    /// </summary>
    private DeviceRegistrationPayload SignPayload(DeviceRegistrationPayload payload)
    {
        if (_signingCertificate is null)
            return payload;

        using var rsa = _signingCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Boot-media certificate has no RSA private key.");

        var timestampUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = DevicePayloadSignature.BuildChallenge(payload.SerialNumber, timestampUtc, nonce);
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new DeviceRegistrationPayload
        {
            SerialNumber = payload.SerialNumber,
            Manufacturer = payload.Manufacturer,
            Model        = payload.Model,
            MacAddress   = payload.MacAddress,
            Hardware     = payload.Hardware,
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce        = nonce,
                TimestampUtc = timestampUtc,
                Signature    = Convert.ToBase64String(signature),
            },
        };
    }

    /// <summary>
    /// GET /api/v1/sessions/{sessionId}/status — Poll the current session state.
    /// Requires the device-session token Bearer to be set in <see cref="HttpClient.DefaultRequestHeaders"/>.
    /// </summary>
    public async Task<SessionStatusResponse?> GetSessionStatusAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/sessions/{sessionId}/status", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionStatusResponse>(JsonOptions, ct);
    }

    /// <summary>
    /// Sets the device-session Bearer token for all subsequent requests.
    /// Called immediately after <see cref="CreateSessionAsync"/> succeeds.
    /// </summary>
    public void SetSessionToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>POST /api/v1/sessions/{sessionId}/progress — Report imaging step progress.</summary>
    public async Task ReportProgressAsync(
        Guid sessionId,
        object payload,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/progress", payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>POST /api/v1/sessions/{sessionId}/sas/refresh — Refresh the SAS token URL.</summary>
    public async Task<string?> RefreshSasTokenAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/v1/sessions/{sessionId}/sas/refresh", null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("sasTokenUrl", out var p) ? p.GetString() : null;
    }
}

/// <summary>
/// Response from the Device Gateway API POST /api/v1/sessions endpoint.
/// </summary>
public sealed class CreateSessionResponse
{
    public Guid SessionId { get; init; }
    public string DeviceSessionToken { get; init; } = string.Empty;
    public string Passcode { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
}

/// <summary>
/// Response from the Device Gateway API GET /api/v1/sessions/{sessionId}/status endpoint (T043).
/// Always includes <see cref="CurrentStep"/> and <see cref="OverallProgressPercent"/> (plan.md constraint).
/// </summary>
public sealed class SessionStatusResponse
{
    public Guid SessionId { get; init; }
    public string? State { get; init; }
    public string? CurrentStep { get; init; }
    public int OverallProgressPercent { get; init; }
    public string? SasTokenUrl { get; init; }
    public string? SasTokenUrlExpiresAt { get; init; }
    public string? Sha256Hash { get; init; }
}

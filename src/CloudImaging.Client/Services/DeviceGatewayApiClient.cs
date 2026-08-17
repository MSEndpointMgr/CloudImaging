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
    /// On a non-success response, throws <see cref="DeviceGatewayApiException"/> (rather than a generic
    /// <see cref="HttpRequestException"/>) carrying the RFC7807 problem "type"/"detail" from the response
    /// body, if present, so callers can distinguish an mTLS certificate rejection from a rejected/expired/
    /// revoked device-session Bearer token — both surface as HTTP 401 but need different user-facing text.
    /// </summary>
    public async Task<SessionStatusResponse?> GetSessionStatusAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v1/sessions/{sessionId}/status", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await DeviceGatewayApiException.FromResponseAsync(response, ct);
        }

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

    /// <summary>
    /// POST /api/v1/sessions/{sessionId}/sas/refresh — Refresh the SAS token URL.
    /// Returns both the (possibly unchanged) URL and its actual server-computed expiry, so the caller
    /// never has to guess the new expiry (see <see cref="SasRefreshCoordinator"/>).
    /// </summary>
    public async Task<SasRefreshResult> RefreshSasTokenAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/v1/sessions/{sessionId}/sas/refresh", null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var url = doc.RootElement.TryGetProperty("sasTokenUrl", out var p) ? p.GetString() : null;
        var expiresAt = doc.RootElement.TryGetProperty("sasTokenUrlExpiresAt", out var e)
            && DateTimeOffset.TryParse(
                e.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        return new SasRefreshResult(url, expiresAt);
    }

    /// <summary>
    /// GET /api/v1/boot-image/latest — Retrieve the latest published boot image's version,
    /// hash, and SAS download URL, used by <see cref="BootImageSelfUpdateService"/> to detect
    /// stale boot media (T071b, FR-059a). Requires only the mTLS boot-media certificate — no
    /// device-session token — so it can run independently of session bootstrap. Returns null on
    /// any non-success response (no active session/token is required, but network/service
    /// errors are treated as "nothing to update" rather than thrown, since this check must never
    /// block Client startup).
    /// </summary>
    public async Task<LatestBootImageInfo?> GetLatestBootImageAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v1/boot-image/latest", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<LatestBootImageInfo>(JsonOptions, ct);
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

/// <summary>Result of a SAS token refresh call — see <see cref="DeviceGatewayApiClient.RefreshSasTokenAsync"/>.</summary>
public sealed record SasRefreshResult(string? SasTokenUrl, DateTimeOffset? ExpiresAt);

/// <summary>
/// Thrown when the Device Gateway API returns a non-success response. Carries the RFC7807
/// "type"/"detail" fields from the response body (when present) so callers can distinguish, e.g.,
/// an mTLS certificate rejection (<see cref="CloudImaging.Client.Services"/> boot-media cert) from a
/// rejected/expired/revoked device-session Bearer token — both surface as HTTP 401.
/// </summary>
public sealed class DeviceGatewayApiException : Exception
{
    /// <summary>Well-known problem type used by <c>DeviceSessionTokenValidationMiddleware</c> for token failures.</summary>
    public const string TokenProblemType = "https://cloudimaging.io/errors/unauthorized";

    public System.Net.HttpStatusCode StatusCode { get; }
    public string? ProblemType { get; }

    public DeviceGatewayApiException(System.Net.HttpStatusCode statusCode, string? problemType, string? detail)
        : base(detail ?? $"Device Gateway API returned HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ProblemType = problemType;
    }

    internal static async Task<DeviceGatewayApiException> FromResponseAsync(
        System.Net.Http.HttpResponseMessage response, CancellationToken ct)
    {
        string? problemType = null;
        string? detail = null;
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            problemType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch
        {
            // Response body wasn't RFC7807 problem-details JSON — leave both null.
        }

        return new DeviceGatewayApiException(response.StatusCode, problemType, detail);
    }
}

using System.Net.Http;
using System.Net.Http.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DeviceGatewayApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// POST /api/v1/sessions — Register a new imaging session.
    /// Returns (sessionId, deviceSessionToken, passcode, state).
    /// </summary>
    public async Task<CreateSessionResponse?> CreateSessionAsync(
        DeviceRegistrationPayload payload,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/sessions", payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateSessionResponse>(JsonOptions, ct);
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

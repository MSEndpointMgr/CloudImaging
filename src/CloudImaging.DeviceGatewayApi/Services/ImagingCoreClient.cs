using System.Net.Http.Json;
using System.Text.Json;

namespace CloudImaging.DeviceGatewayApi.Services;

/// <summary>
/// Typed HTTP client for Device Gateway API → Imaging Core API calls over Private Link (FR-013).
/// All operations forward requests using the Device Gateway API managed identity credential.
/// </summary>
public sealed class ImagingCoreClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ImagingCoreClient(HttpClient http) => _http = http;

    /// <summary>Forward session creation to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> CreateSessionAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions", payload, JsonOptions, ct);

    /// <summary>Forward status poll to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> GetSessionStatusAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}/status", ct);

    /// <summary>Forward progress report to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> ReportProgressAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/progress", payload, JsonOptions, ct);

    /// <summary>Forward SAS token refresh request to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> RefreshSasTokenAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/sessions/{sessionId}/sas/refresh", null, ct);

    /// <summary>Forward cache hash validation request to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> ValidateCacheHashAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/cache/validate", payload, JsonOptions, ct);

    /// <summary>Forward the active boot image catalog listing request to ImagingCoreApi (T071b, FR-059a).</summary>
    public Task<HttpResponseMessage> GetBootImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/boot-images", ct);

    /// <summary>Forward a boot image SAS URL issuance request to ImagingCoreApi (T071b, FR-059a).</summary>
    public Task<HttpResponseMessage> GetBootImageSasUrlAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/sas", null, ct);

    /// <summary>Forward the active recovery image catalog listing request to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> GetRecoveryImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/recovery-images", ct);

    /// <summary>Forward a recovery image SAS URL issuance request to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> GetRecoveryImageSasUrlAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/recovery-images/{recoveryImageId}/sas", null, ct);

    /// <summary>Forward a session log upload URL request to ImagingCoreApi.</summary>
    public Task<HttpResponseMessage> RequestSessionLogUploadUrlAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/sessions/{sessionId}/logs/upload-url", null, ct);
}

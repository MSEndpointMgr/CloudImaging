using System.Net.Http.Json;
using System.Text.Json;
using CloudImaging.Contracts.Models;

namespace CloudImaging.OperatorApi.Services;

/// <summary>
/// Typed HTTP client for Operator API → Imaging Core API calls over Private Link (FR-064).
/// All operations forward requests using the Operator API managed identity credential.
/// </summary>
public sealed class ImagingCoreClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ImagingCoreClient(HttpClient http) => _http = http;

    // ── Session operations ────────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetSessionsAsync(string? filter, CancellationToken ct = default) =>
        _http.GetAsync(filter is null ? "/api/internal/sessions" : $"/api/internal/sessions?filter={filter}", ct);

    public Task<HttpResponseMessage> GetSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}", ct);

    public Task<HttpResponseMessage> CoupleSessionAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/couple", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> AssignSessionAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/assign", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> BulkAssignAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/bulk-assign", payload, JsonOptions, ct);

    // ── OS image operations ────────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/images", ct);

    public Task<HttpResponseMessage> CreateImageAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> UpdateImageAsync(Guid imageId, object payload, CancellationToken ct = default) =>
        _http.PatchAsJsonAsync($"/api/internal/images/{imageId}", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> DeleteImageAsync(Guid imageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/images/{imageId}", ct);

    public Task<HttpResponseMessage> StartOsImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images/upload/start", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> PublishOsImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    // ── Boot image operations ──────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetBootImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/boot-images", ct);

    public Task<HttpResponseMessage> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/sas", null, ct);

    public Task<HttpResponseMessage> StartBootImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/boot-images/upload/start", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> PublishBootImageUploadAsync(string token, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/boot-images/upload/{token}/publish", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> PublishBootImageAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/boot-images/publish", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> DeleteBootImageAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/boot-images/{bootImageId}", ct);

    // ── Branding ──────────────────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetBrandingAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding", ct);

    public Task<HttpResponseMessage> UpdateBrandingAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> GetBrandingLogoSasAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/sas", ct);

    public Task<HttpResponseMessage> UploadBrandingLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/logo", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> UploadBrandingPortalLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/portal-logo", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> GetBrandingLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/content", ct);

    public Task<HttpResponseMessage> GetBrandingPortalLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/portal-logo/content", ct);

    public Task<HttpResponseMessage> DeleteBrandingLogoAsync(CancellationToken ct = default) =>
        _http.DeleteAsync("/api/internal/branding/logo", ct);

    public Task<HttpResponseMessage> DeleteBrandingPortalLogoAsync(CancellationToken ct = default) =>
        _http.DeleteAsync("/api/internal/branding/portal-logo", ct);

    // ── Portal configuration ───────────────────────────────────────────────────

    /// <summary>Returns the current portal configuration as a typed model.</summary>
    public async Task<PortalConfiguration> GetPortalConfigurationAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/internal/portal-configuration", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PortalConfiguration>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Null response from ImagingCoreApi portal-configuration endpoint.");
    }

    /// <summary>Replaces the portal configuration with the supplied model.</summary>
    public async Task UpsertPortalConfigurationAsync(PortalConfiguration config, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/internal/portal-configuration", config, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    // ── Partitioning scheme ─────────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetPartitioningSchemeAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/partitioning-scheme", ct);

    public Task<HttpResponseMessage> PutPartitioningSchemeAsync(PartitioningScheme scheme, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/partitioning-scheme", scheme, JsonOptions, ct);

    // ── Recovery image operations ──────────────────────────────────────────────

    public Task<HttpResponseMessage> GetRecoveryImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/recovery-images", ct);

    public Task<HttpResponseMessage> GetRecoveryImageSasAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/recovery-images/{recoveryImageId}/sas", null, ct);

    public Task<HttpResponseMessage> DeleteRecoveryImageAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/recovery-images/{recoveryImageId}", ct);

    public Task<HttpResponseMessage> StartRecoveryImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/recovery-images/upload/start", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> PublishRecoveryImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/recovery-images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    // ── Boot media certificate ────────────────────────────────────────────────

    public Task<HttpResponseMessage> GetActiveBootCertMetadataAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active", ct);

    public Task<HttpResponseMessage> GetActiveBootCertPfxAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active/pfx", ct);

    public Task<HttpResponseMessage> GenerateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/generate", payload, JsonOptions, ct);

    public Task<HttpResponseMessage> RotateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/rotate", payload, JsonOptions, ct);
}

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

    /// <summary>Initializes a new instance of the <see cref="ImagingCoreClient"/> class.</summary>
    public ImagingCoreClient(HttpClient http) => _http = http;

    // ── Session operations ────────────────────────────────────────────────────

    /// <summary>Fetches the session list from the Imaging Core API, optionally filtered.</summary>
    public Task<HttpResponseMessage> GetSessionsAsync(string? filter, CancellationToken ct = default) =>
        _http.GetAsync(filter is null ? "/api/internal/sessions" : $"/api/internal/sessions?filter={filter}", ct);

    /// <summary>Fetches a single session by its identifier from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}", ct);

    /// <summary>Couples a device session by passcode via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> CoupleSessionAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/couple", payload, JsonOptions, ct);

    /// <summary>Assigns an OS image to a session via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> AssignSessionAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/assign", payload, JsonOptions, ct);

    /// <summary>Bulk-assigns an OS image to multiple sessions via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> BulkAssignAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/bulk-assign", payload, JsonOptions, ct);

    /// <summary>Cancels a coupled session via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> CancelSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/sessions/{sessionId}", ct);

    /// <summary>Fetches session history records, optionally bounded by from/to dates, from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetSessionHistoryAsync(string? from, string? to, CancellationToken ct = default)
    {
        var query = string.Join('&', new[]
        {
            from is not null ? $"from={Uri.EscapeDataString(from)}" : null,
            to is not null ? $"to={Uri.EscapeDataString(to)}" : null,
        }.Where(p => p is not null));
        var url = query.Length > 0 ? $"/api/internal/session-history?{query}" : "/api/internal/session-history";
        return _http.GetAsync(url, ct);
    }

    // ── OS image operations ────────────────────────────────────────────────────

    /// <summary>Fetches the OS image catalog from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/images", ct);

    /// <summary>Creates an OS image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> CreateImageAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images", payload, JsonOptions, ct);

    /// <summary>Updates an OS image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> UpdateImageAsync(Guid imageId, object payload, CancellationToken ct = default) =>
        _http.PatchAsJsonAsync($"/api/internal/images/{imageId}", payload, JsonOptions, ct);

    /// <summary>Deletes an OS image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> DeleteImageAsync(Guid imageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/images/{imageId}", ct);

    /// <summary>Starts a staged OS image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> StartOsImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images/upload/start", payload, JsonOptions, ct);

    /// <summary>Publishes a staged OS image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> PublishOsImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    /// <summary>Abandons a staged OS image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> AbandonOsImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/images/upload/{uploadId}/abandon", payload, JsonOptions, ct);

    // ── Upload job status ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the status of a background publish job. Publish endpoints return 202 Accepted and
    /// hand the expensive verification work to a worker, so the portal polls this to learn when
    /// the image actually landed in the catalog (or why it did not).
    /// </summary>
    public Task<HttpResponseMessage> GetUploadJobAsync(string uploadId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/upload-jobs/{Uri.EscapeDataString(uploadId)}", ct);

    // ── Boot image operations ──────────────────────────────────────────────────

    /// <summary>Fetches the boot image list from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBootImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/boot-images", ct);

    /// <summary>Fetches a single boot image by its identifier from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBootImageByIdAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/boot-images/{bootImageId}", ct);

    /// <summary>Issues a SAS URL for a boot image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/sas", null, ct);

    /// <summary>Starts a staged boot image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> StartBootImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/boot-images/upload/start", payload, JsonOptions, ct);

    /// <summary>Publishes a staged boot image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> PublishBootImageUploadAsync(string token, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/boot-images/upload/{token}/publish", payload, JsonOptions, ct);

    /// <summary>Deletes a boot image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> DeleteBootImageAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/boot-images/{bootImageId}", ct);

    // ── Branding ──────────────────────────────────────────────────────────────

    /// <summary>Fetches the current branding configuration from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBrandingAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding", ct);

    /// <summary>Updates the branding configuration via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> UpdateBrandingAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding", payload, JsonOptions, ct);

    /// <summary>Requests a SAS URL for the branding logo via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBrandingLogoSasAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/sas", ct);

    /// <summary>Uploads the branding logo via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> UploadBrandingLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/logo", payload, JsonOptions, ct);

    /// <summary>Uploads the portal branding logo via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> UploadBrandingPortalLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/portal-logo", payload, JsonOptions, ct);

    /// <summary>Fetches the branding logo content from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBrandingLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/content", ct);

    /// <summary>Fetches the portal branding logo content from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetBrandingPortalLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/portal-logo/content", ct);

    /// <summary>Deletes the branding logo via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> DeleteBrandingLogoAsync(CancellationToken ct = default) =>
        _http.DeleteAsync("/api/internal/branding/logo", ct);

    /// <summary>Deletes the portal branding logo via the Imaging Core API.</summary>
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

    /// <summary>Fetches the partitioning scheme from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetPartitioningSchemeAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/partitioning-scheme", ct);

    /// <summary>Replaces the partitioning scheme via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> PutPartitioningSchemeAsync(PartitioningScheme scheme, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/partitioning-scheme", scheme, JsonOptions, ct);

    // ── Recovery image operations ──────────────────────────────────────────────

    /// <summary>Fetches the recovery image list from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetRecoveryImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/recovery-images", ct);

    /// <summary>Issues a SAS URL for a recovery image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetRecoveryImageSasAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/recovery-images/{recoveryImageId}/sas", null, ct);

    /// <summary>Deletes a recovery image via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> DeleteRecoveryImageAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/recovery-images/{recoveryImageId}", ct);

    /// <summary>Starts a staged recovery image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> StartRecoveryImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/recovery-images/upload/start", payload, JsonOptions, ct);

    /// <summary>Publishes a staged recovery image upload via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> PublishRecoveryImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/recovery-images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    // ── Session logs ────────────────────────────────────────────────────────────

    /// <summary>Fetches the uploaded diagnostic logs for a session from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetSessionLogsAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}/logs", ct);

    /// <summary>Requests a short-lived download URL for a session log file via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetSessionLogDownloadUrlAsync(Guid sessionId, string fileName, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}/logs/{Uri.EscapeDataString(fileName)}/download-url", ct);

    // ── Boot media certificate ────────────────────────────────────────────────

    /// <summary>Fetches the active boot media certificate metadata from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetActiveBootCertMetadataAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active", ct);

    /// <summary>Fetches the active boot media certificate PFX bytes from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetActiveBootCertPfxAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active/pfx", ct);

    /// <summary>Generates a new boot media certificate via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GenerateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/generate", payload, JsonOptions, ct);

    /// <summary>Rotates the boot media certificate via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> RotateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/rotate", payload, JsonOptions, ct);

    // ── Location catalog ─────────────────────────────────────────────────────────

    /// <summary>Fetches the location catalog from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetLocationsAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/locations", ct);

    /// <summary>Creates a location via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> CreateLocationAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/locations", payload, JsonOptions, ct);

    /// <summary>Deletes a location via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> DeleteLocationAsync(Guid locationId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/locations/{locationId}", ct);

    // ── User location preference ─────────────────────────────────────────────────

    /// <summary>Fetches a user's preferred location from the Imaging Core API.</summary>
    public Task<HttpResponseMessage> GetUserLocationPreferenceAsync(string userId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/user-preferences/{Uri.EscapeDataString(userId)}", ct);

    /// <summary>Sets or clears a user's preferred location via the Imaging Core API.</summary>
    public Task<HttpResponseMessage> PutUserLocationPreferenceAsync(string userId, object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync($"/api/internal/user-preferences/{Uri.EscapeDataString(userId)}", payload, JsonOptions, ct);
}
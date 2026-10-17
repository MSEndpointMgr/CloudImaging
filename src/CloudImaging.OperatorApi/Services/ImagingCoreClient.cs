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

    /// <param name="http">HttpClient pre-configured with the Imaging Core API base address and managed identity credential.</param>
    public ImagingCoreClient(HttpClient http) => _http = http;

    // ── Session operations ────────────────────────────────────────────────────

    /// <summary>Lists device sessions, optionally filtered.</summary>
    public Task<HttpResponseMessage> GetSessionsAsync(string? filter, CancellationToken ct = default) =>
        _http.GetAsync(filter is null ? "/api/internal/sessions" : $"/api/internal/sessions?filter={filter}", ct);

    /// <summary>Returns a single device session summary.</summary>
    public Task<HttpResponseMessage> GetSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}", ct);

    /// <summary>Couples an operator to a session via its passcode.</summary>
    public Task<HttpResponseMessage> CoupleSessionAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/couple", payload, JsonOptions, ct);

    /// <summary>Assigns an OS image to a coupled session.</summary>
    public Task<HttpResponseMessage> AssignSessionAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/assign", payload, JsonOptions, ct);

    /// <summary>Assigns an OS image to multiple coupled sessions in one call.</summary>
    public Task<HttpResponseMessage> BulkAssignAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/sessions/bulk-assign", payload, JsonOptions, ct);

    /// <summary>Removes a coupled session that was aborted before imaging started.</summary>
    public Task<HttpResponseMessage> CancelSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/sessions/{sessionId}", ct);

    /// <summary>Lists durable terminal-outcome session records, optionally bounded by a date range.</summary>
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

    /// <summary>Lists OS images in the catalog.</summary>
    public Task<HttpResponseMessage> GetImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/images", ct);

    /// <summary>Creates a new OS image catalog entry.</summary>
    public Task<HttpResponseMessage> CreateImageAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images", payload, JsonOptions, ct);

    /// <summary>Updates an OS image catalog entry.</summary>
    public Task<HttpResponseMessage> UpdateImageAsync(Guid imageId, object payload, CancellationToken ct = default) =>
        _http.PatchAsJsonAsync($"/api/internal/images/{imageId}", payload, JsonOptions, ct);

    /// <summary>Deletes an OS image catalog entry.</summary>
    public Task<HttpResponseMessage> DeleteImageAsync(Guid imageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/images/{imageId}", ct);

    /// <summary>Starts a staged OS image upload and returns an upload token/SAS.</summary>
    public Task<HttpResponseMessage> StartOsImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/images/upload/start", payload, JsonOptions, ct);

    /// <summary>Commits, validates and publishes a staged OS image upload.</summary>
    public Task<HttpResponseMessage> PublishOsImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    /// <summary>Deletes an uncommitted staged OS image blob.</summary>
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

    /// <summary>Lists boot (WinPE) images in the catalog.</summary>
    public Task<HttpResponseMessage> GetBootImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/boot-images", ct);

    /// <summary>Returns a single boot image.</summary>
    public Task<HttpResponseMessage> GetBootImageByIdAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/boot-images/{bootImageId}", ct);

    /// <summary>Issues a SAS URL for downloading a boot image.</summary>
    public Task<HttpResponseMessage> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/sas", null, ct);

    /// <summary>Starts a staged boot image upload and returns an upload token/SAS.</summary>
    public Task<HttpResponseMessage> StartBootImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/boot-images/upload/start", payload, JsonOptions, ct);

    /// <summary>Commits, validates and publishes a staged boot image upload.</summary>
    public Task<HttpResponseMessage> PublishBootImageUploadAsync(string token, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/boot-images/upload/{token}/publish", payload, JsonOptions, ct);

    /// <summary>Deletes a boot image.</summary>
    public Task<HttpResponseMessage> DeleteBootImageAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/boot-images/{bootImageId}", ct);

    /// <summary>Promotes a boot image to the active/default boot image.</summary>
    public Task<HttpResponseMessage> PromoteBootImageAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/promote", null, ct);

    /// <summary>Demotes a boot image from the active/default boot image.</summary>
    public Task<HttpResponseMessage> DemoteBootImageAsync(Guid bootImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/boot-images/{bootImageId}/demote", null, ct);

    // ── Branding ──────────────────────────────────────────────────────────────

    /// <summary>Returns the current branding configuration.</summary>
    public Task<HttpResponseMessage> GetBrandingAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding", ct);

    /// <summary>Updates the branding configuration.</summary>
    public Task<HttpResponseMessage> UpdateBrandingAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding", payload, JsonOptions, ct);

    /// <summary>Issues a SAS URL for uploading/downloading the client branding logo.</summary>
    public Task<HttpResponseMessage> GetBrandingLogoSasAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/sas", ct);

    /// <summary>Uploads the client branding logo.</summary>
    public Task<HttpResponseMessage> UploadBrandingLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/logo", payload, JsonOptions, ct);

    /// <summary>Uploads the portal branding logo.</summary>
    public Task<HttpResponseMessage> UploadBrandingPortalLogoAsync(object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/branding/portal-logo", payload, JsonOptions, ct);

    /// <summary>Returns the client branding logo bytes.</summary>
    public Task<HttpResponseMessage> GetBrandingLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/logo/content", ct);

    /// <summary>Returns the portal branding logo bytes.</summary>
    public Task<HttpResponseMessage> GetBrandingPortalLogoContentAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/branding/portal-logo/content", ct);

    /// <summary>Deletes the client branding logo.</summary>
    public Task<HttpResponseMessage> DeleteBrandingLogoAsync(CancellationToken ct = default) =>
        _http.DeleteAsync("/api/internal/branding/logo", ct);

    /// <summary>Deletes the portal branding logo.</summary>
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

    /// <summary>Returns the current partitioning scheme.</summary>
    public Task<HttpResponseMessage> GetPartitioningSchemeAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/partitioning-scheme", ct);

    /// <summary>Replaces the current partitioning scheme.</summary>
    public Task<HttpResponseMessage> PutPartitioningSchemeAsync(PartitioningScheme scheme, CancellationToken ct = default) =>
        _http.PutAsJsonAsync("/api/internal/partitioning-scheme", scheme, JsonOptions, ct);

    // ── Recovery image operations ──────────────────────────────────────────────

    /// <summary>Lists active recovery (WinRE) images.</summary>
    public Task<HttpResponseMessage> GetRecoveryImagesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/recovery-images", ct);

    /// <summary>Issues a SAS URL for a recovery image.</summary>
    public Task<HttpResponseMessage> GetRecoveryImageSasAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.PostAsync($"/api/internal/recovery-images/{recoveryImageId}/sas", null, ct);

    /// <summary>Deletes a recovery image.</summary>
    public Task<HttpResponseMessage> DeleteRecoveryImageAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/recovery-images/{recoveryImageId}", ct);

    /// <summary>Starts a staged recovery image upload.</summary>
    public Task<HttpResponseMessage> StartRecoveryImageUploadAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/recovery-images/upload/start", payload, JsonOptions, ct);

    /// <summary>Commits and publishes a staged recovery image upload.</summary>
    public Task<HttpResponseMessage> PublishRecoveryImageUploadAsync(string uploadId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/recovery-images/upload/{uploadId}/publish", payload, JsonOptions, ct);

    // ── Session logs ────────────────────────────────────────────────────────────

    /// <summary>Lists uploaded diagnostic logs for a session.</summary>
    public Task<HttpResponseMessage> GetSessionLogsAsync(Guid sessionId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}/logs", ct);

    /// <summary>Issues a short-lived download URL for a session log.</summary>
    public Task<HttpResponseMessage> GetSessionLogDownloadUrlAsync(Guid sessionId, string fileName, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/sessions/{sessionId}/logs/{Uri.EscapeDataString(fileName)}/download-url", ct);

    // ── Boot media certificate ────────────────────────────────────────────────

    /// <summary>Returns metadata for the currently active boot media signing certificate.</summary>
    public Task<HttpResponseMessage> GetActiveBootCertMetadataAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active", ct);

    /// <summary>Returns the PFX bytes for the currently active boot media signing certificate.</summary>
    public Task<HttpResponseMessage> GetActiveBootCertPfxAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/cert/active/pfx", ct);

    /// <summary>Generates a new boot media signing certificate.</summary>
    public Task<HttpResponseMessage> GenerateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/generate", payload, JsonOptions, ct);

    /// <summary>Rotates the active boot media signing certificate.</summary>
    public Task<HttpResponseMessage> RotateCertAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/cert/rotate", payload, JsonOptions, ct);

    // ── Location catalog ─────────────────────────────────────────────────────────

    /// <summary>Lists all location labels.</summary>
    public Task<HttpResponseMessage> GetLocationsAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/locations", ct);

    /// <summary>Creates a new location label.</summary>
    public Task<HttpResponseMessage> CreateLocationAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/locations", payload, JsonOptions, ct);

    /// <summary>Updates a location label's name, region and country.</summary>
    public Task<HttpResponseMessage> UpdateLocationAsync(Guid locationId, object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync($"/api/internal/locations/{locationId}", payload, JsonOptions, ct);

    /// <summary>Deletes a location label.</summary>
    public Task<HttpResponseMessage> DeleteLocationAsync(Guid locationId, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/locations/{locationId}", ct);

    // ── User location preference ─────────────────────────────────────────────────

    /// <summary>Returns the user's preferred location, or 404.</summary>
    public Task<HttpResponseMessage> GetUserLocationPreferenceAsync(string userId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/user-preferences/{Uri.EscapeDataString(userId)}", ct);

    /// <summary>Sets or clears the user's preferred location.</summary>
    public Task<HttpResponseMessage> PutUserLocationPreferenceAsync(string userId, object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync($"/api/internal/user-preferences/{Uri.EscapeDataString(userId)}", payload, JsonOptions, ct);

    // ── Autopilot registration ─────────────────────────────────────────────────────

    /// <summary>Lists pending Autopilot self-registration requests.</summary>
    public Task<HttpResponseMessage> ListAutopilotRegistrationsAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/autopilot/registrations", ct);

    /// <summary>Lists already-handled (approved/rejected) Autopilot registration requests, optionally bounded by a date range.</summary>
    public Task<HttpResponseMessage> ListHandledAutopilotRegistrationsAsync(string? from, string? to, CancellationToken ct = default)
    {
        var query = string.Join('&', new[]
        {
            "view=history",
            from is not null ? $"from={Uri.EscapeDataString(from)}" : null,
            to is not null ? $"to={Uri.EscapeDataString(to)}" : null,
        }.Where(p => p is not null));
        return _http.GetAsync($"/api/internal/autopilot/registrations?{query}", ct);
    }

    /// <summary>Returns a single Autopilot registration request.</summary>
    public Task<HttpResponseMessage> GetAutopilotRegistrationAsync(Guid requestId, CancellationToken ct = default) =>
        _http.GetAsync($"/api/internal/autopilot/registrations/{requestId}", ct);

    /// <summary>Forwards an approve, reject or retry decision (<paramref name="action"/>).</summary>
    public Task<HttpResponseMessage> DecideAutopilotRegistrationAsync(Guid requestId, string action, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/autopilot/registrations/{requestId}/{action}", payload, JsonOptions, ct);

    /// <summary>Lists Autopilot group tags.</summary>
    public Task<HttpResponseMessage> ListAutopilotGroupTagsAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/autopilot/group-tags", ct);

    /// <summary>Creates a new Autopilot group tag.</summary>
    public Task<HttpResponseMessage> CreateAutopilotGroupTagAsync(object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync("/api/internal/autopilot/group-tags", payload, JsonOptions, ct);

    /// <summary>Updates an Autopilot group tag.</summary>
    public Task<HttpResponseMessage> UpdateAutopilotGroupTagAsync(Guid id, object payload, CancellationToken ct = default) =>
        _http.PutAsJsonAsync($"/api/internal/autopilot/group-tags/{id}", payload, JsonOptions, ct);

    /// <summary>Deletes an Autopilot group tag.</summary>
    public Task<HttpResponseMessage> DeleteAutopilotGroupTagAsync(Guid id, CancellationToken ct = default) =>
        _http.DeleteAsync($"/api/internal/autopilot/group-tags/{id}", ct);

    // ── Pre-flight overrides ───────────────────────────────────────────────────────

    /// <summary>Lists active pre-flight overrides.</summary>
    public Task<HttpResponseMessage> ListPreFlightOverridesAsync(CancellationToken ct = default) =>
        _http.GetAsync("/api/internal/preflight-overrides", ct);

    /// <summary>Approves a pre-flight override for a session.</summary>
    public Task<HttpResponseMessage> ApprovePreFlightOverrideAsync(Guid sessionId, object payload, CancellationToken ct = default) =>
        _http.PostAsJsonAsync($"/api/internal/sessions/{sessionId}/preflight-override", payload, JsonOptions, ct);

    /// <summary>Revokes a serial number's active pre-flight override.</summary>
    public Task<HttpResponseMessage> RevokePreFlightOverrideAsync(string serialNumber, string revokedBy, CancellationToken ct = default) =>
        _http.DeleteAsync(
            $"/api/internal/preflight-overrides?serialNumber={Uri.EscapeDataString(serialNumber)}&revokedBy={Uri.EscapeDataString(revokedBy)}",
            ct);
}

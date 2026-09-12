using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Typed HTTP client for Media Builder → Operator API calls (T068, FR-062).
/// Used to retrieve boot image metadata, SAS URLs, and branding logo SAS.
/// Authentication: Entra ID Bearer token from <see cref="EntraAuthenticationService"/>.
/// </summary>
public sealed partial class OperatorApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<OperatorApiClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="OperatorApiClient"/> class.</summary>
    /// <param name="http">The HTTP client used for Operator API requests.</param>
    /// <param name="logger">The logger instance.</param>
    public OperatorApiClient(HttpClient http, ILogger<OperatorApiClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>Sets the Entra Bearer token for all subsequent requests.</summary>
    public void SetAccessToken(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ── Boot images ───────────────────────────────────────────────────────────

    /// <summary>Retrieves the catalog of available boot images from the Operator API.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of available boot images.</returns>
    public async Task<IReadOnlyList<BootImageDto>> GetBootImagesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/boot-images", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BootImageDto>>(JsonOptions, ct)
            ?? [];
    }

    /// <summary>Retrieves the SAS URL for downloading a specific boot image.</summary>
    /// <param name="bootImageId">The unique identifier of the boot image.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SAS URL and hash for the requested boot image.</returns>
    public async Task<BootImageSasDto> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/boot-images/{bootImageId}/sas", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BootImageSasDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty SAS response from Operator API.");
    }

    // ── Branding logo ─────────────────────────────────────────────────────────

    /// <summary>Retrieves the SAS URL for the branding logo, or null if none is configured.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SAS URL and expiry for the branding logo, or null if not found.</returns>
    public async Task<BrandingLogoSasDto?> GetBrandingLogoSasAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/branding/logo/sas", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingLogoSasDto>(JsonOptions, ct);
    }

    // ── Boot media certificate ────────────────────────────────────────────────

    /// <summary>Retrieves the boot media certificate as a PFX byte array.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The PFX certificate bytes.</returns>
    public async Task<byte[]> GetBootMediaCertPfxAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/bootmedia/certificate/pfx", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Retrieves boot media certificate metadata (no PFX bytes) so callers can check whether an
    /// active certificate is configured without downloading the certificate itself (T151,
    /// FR-050a). Returns null when no certificate is configured (Operator API returns 404).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The certificate metadata, or null if no certificate is configured.</returns>
    public async Task<BootMediaCertificateMetadataDto?> GetBootMediaCertMetadataAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/bootmedia/certificate/metadata", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BootMediaCertificateMetadataDto>(JsonOptions, ct);
    }

    // ── Endpoint configuration ────────────────────────────────────────────────

    /// <summary>
    /// Retrieves environment-level endpoint configuration (currently the Device Gateway API
    /// base URL) so boot-image generation can stamp it into the Client's appsettings.json
    /// instead of relying on a manually maintained config file.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The endpoint configuration.</returns>
    public async Task<EndpointConfigurationDto> GetEndpointConfigurationAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/configuration/endpoints", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EndpointConfigurationDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty endpoint configuration response from Operator API.");
    }

    // ── Locations ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the admin-managed location catalog (Location Labels feature) so the technician
    /// can optionally tag the USB boot media being prepared with a site label.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of available locations.</returns>
    public async Task<IReadOnlyList<LocationDto>> GetLocationsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/locations", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<LocationDto>>(JsonOptions, ct)
            ?? [];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator API request: {Method} {Path}.")]
    private static partial void LogRequest(ILogger logger, string method, string path);
}

/// <summary>Minimal DTO for boot image list/SAS responses.</summary>
public sealed class BootImageDto
{
    /// <summary>Unique identifier of the boot image.</summary>
    public Guid   BootImageId       { get; init; }
    /// <summary>Version string of the boot image.</summary>
    public string Version           { get; init; } = string.Empty;
    /// <summary>Size of the boot image in bytes.</summary>
    public long   SizeBytes         { get; init; }
    /// <summary>SHA-256 hash of the boot image contents.</summary>
    public string Sha256Hash        { get; init; } = string.Empty;
    /// <summary>Whether this is the latest published boot image.</summary>
    public bool   IsLatestPublished { get; init; }
    /// <summary>Whether this boot image is currently active.</summary>
    public bool   IsActive          { get; init; }
}

/// <summary>SAS URL details for downloading a boot image.</summary>
public sealed class BootImageSasDto
{
    /// <summary>The SAS token URL to download the boot image.</summary>
    public string SasTokenUrl { get; init; } = string.Empty;
    /// <summary>SHA-256 hash of the boot image contents.</summary>
    public string Sha256Hash  { get; init; } = string.Empty;
    /// <summary>When the SAS URL expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>SAS URL details for downloading the branding logo.</summary>
public sealed class BrandingLogoSasDto
{
    /// <summary>The SAS token URL to download the branding logo.</summary>
    public string SasTokenUrl { get; init; } = string.Empty;
    /// <summary>When the SAS URL expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Environment-level endpoint configuration (currently just the Device Gateway API URL).</summary>
public sealed class EndpointConfigurationDto
{
    /// <summary>The Device Gateway API base URL.</summary>
    public string DeviceGatewayApiBaseUrl { get; init; } = string.Empty;
}

/// <summary>Boot media certificate metadata (no PFX bytes), returned by GET /api/bootmedia/certificate/metadata.</summary>
public sealed class BootMediaCertificateMetadataDto
{
    /// <summary>The certificate thumbprint for display.</summary>
    public string? ThumbprintDisplay { get; init; }
    /// <summary>The certificate subject.</summary>
    public string? Subject           { get; init; }
    /// <summary>When the certificate was issued.</summary>
    public DateTimeOffset? IssuedAt  { get; init; }
    /// <summary>When the certificate expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Whether this certificate is currently active.</summary>
    public bool IsActive             { get; init; }
}

/// <summary>A selectable entry from the admin-managed location catalog (Location Labels feature).</summary>
public sealed class LocationDto
{
    /// <summary>Unique identifier of the location.</summary>
    public Guid   LocationId { get; init; }
    /// <summary>Display name of the location.</summary>
    public string Name       { get; init; } = string.Empty;
}

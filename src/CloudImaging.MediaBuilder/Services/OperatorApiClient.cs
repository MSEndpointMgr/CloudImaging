using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudImaging.Contracts.Models;
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

    /// <summary>Builds the client over an injected <see cref="HttpClient"/> pointed at the Operator API.</summary>
    public OperatorApiClient(HttpClient http, ILogger<OperatorApiClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>Sets the Entra Bearer token for all subsequent requests.</summary>
    public void SetAccessToken(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ── Boot images ───────────────────────────────────────────────────────────

    /// <summary>Retrieves the published boot image catalog.</summary>
    public async Task<IReadOnlyList<BootImageDto>> GetBootImagesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/boot-images", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BootImageDto>>(JsonOptions, ct)
            ?? [];
    }

    /// <summary>Requests a time-limited SAS URL to download the given boot image's WIM.</summary>
    public async Task<BootImageSasDto> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/boot-images/{bootImageId}/sas", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BootImageSasDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty SAS response from Operator API.");
    }

    // ── Branding logo ─────────────────────────────────────────────────────────

    /// <summary>Requests a time-limited SAS URL for the configured branding logo; <c>null</c> when no logo is configured.</summary>
    public async Task<BrandingLogoSasDto?> GetBrandingLogoSasAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/branding/logo/sas", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingLogoSasDto>(JsonOptions, ct);
    }

    // ── Boot media certificate ────────────────────────────────────────────────

    /// <summary>Downloads the active boot media certificate's PFX bytes.</summary>
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
    /// <summary>Client version baked into the boot image.</summary>
    public string Version           { get; init; } = string.Empty;
    /// <summary>Size of the WIM in bytes.</summary>
    public long   SizeBytes         { get; init; }
    /// <summary>SHA-256 hash of the WIM, used for integrity verification after download.</summary>
    public string Sha256Hash        { get; init; } = string.Empty;
    /// <summary>True when this is the most recently published boot image.</summary>
    public bool   IsLatestPublished { get; init; }
    /// <summary>True when the boot image is active (not retired/disabled).</summary>
    public bool   IsActive          { get; init; }

    /// <summary>False while in pre-production testing. Missing from older APIs, which only had production images.</summary>
    public bool   IsProduction      { get; init; } = true;

    /// <summary>Defaults to x64 for catalog entries that predate architecture tracking (todo/arm64-support.md).</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
}

/// <summary>SAS URL and integrity hash for downloading a boot image's WIM.</summary>
public sealed class BootImageSasDto
{
    /// <summary>Time-limited SAS URL to download the WIM blob.</summary>
    public string SasTokenUrl { get; init; } = string.Empty;
    /// <summary>SHA-256 hash of the WIM, used for integrity verification after download.</summary>
    public string Sha256Hash  { get; init; } = string.Empty;
    /// <summary>When the SAS URL expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>SAS URL for downloading the configured branding logo.</summary>
public sealed class BrandingLogoSasDto
{
    /// <summary>Time-limited SAS URL to download the branding logo blob.</summary>
    public string SasTokenUrl { get; init; } = string.Empty;
    /// <summary>When the SAS URL expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Environment-level endpoint configuration (currently just the Device Gateway API URL).</summary>
public sealed class EndpointConfigurationDto
{
    /// <summary>Base URL of the Device Gateway API for this environment.</summary>
    public string DeviceGatewayApiBaseUrl { get; init; } = string.Empty;
}

/// <summary>Boot media certificate metadata (no PFX bytes), returned by GET /api/bootmedia/certificate/metadata.</summary>
public sealed class BootMediaCertificateMetadataDto
{
    /// <summary>Human-readable certificate thumbprint for display.</summary>
    public string? ThumbprintDisplay { get; init; }
    /// <summary>Certificate subject name.</summary>
    public string? Subject           { get; init; }
    /// <summary>When the certificate was issued.</summary>
    public DateTimeOffset? IssuedAt  { get; init; }
    /// <summary>When the certificate expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>True when the certificate is currently active.</summary>
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

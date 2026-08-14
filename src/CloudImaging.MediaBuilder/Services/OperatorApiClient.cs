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

    public OperatorApiClient(HttpClient http, ILogger<OperatorApiClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>Sets the Entra Bearer token for all subsequent requests.</summary>
    public void SetAccessToken(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ── Boot images ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BootImageDto>> GetBootImagesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/boot-images", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BootImageDto>>(JsonOptions, ct)
            ?? [];
    }

    public async Task<BootImageSasDto> GetBootImageSasAsync(Guid bootImageId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/boot-images/{bootImageId}/sas", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BootImageSasDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty SAS response from Operator API.");
    }

    // ── Branding logo ─────────────────────────────────────────────────────────

    public async Task<BrandingLogoSasDto?> GetBrandingLogoSasAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/branding/logo/sas", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingLogoSasDto>(JsonOptions, ct);
    }

    // ── Boot media certificate ────────────────────────────────────────────────

    public async Task<byte[]> GetBootMediaCertPfxAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/bootmedia/certificate/pfx", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator API request: {Method} {Path}.")]
    private static partial void LogRequest(ILogger logger, string method, string path);
}

/// <summary>Minimal DTO for boot image list/SAS responses.</summary>
public sealed class BootImageDto
{
    public Guid   BootImageId       { get; init; }
    public string Version           { get; init; } = string.Empty;
    public long   SizeBytes         { get; init; }
    public string Sha256Hash        { get; init; } = string.Empty;
    public bool   IsLatestPublished { get; init; }
    public bool   IsActive          { get; init; }
}

public sealed class BootImageSasDto
{
    public string SasTokenUrl { get; init; } = string.Empty;
    public string Sha256Hash  { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class BrandingLogoSasDto
{
    public string SasTokenUrl { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Environment-level endpoint configuration (currently just the Device Gateway API URL).</summary>
public sealed class EndpointConfigurationDto
{
    public string DeviceGatewayApiBaseUrl { get; init; } = string.Empty;
}

using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Loads the boot-media mTLS certificate at application startup and configures
/// it on the <see cref="HttpClientHandler"/> for Device Gateway API connections (T169, FR-071).
///
/// On TLS handshake failure or HTTP 401 cert-mismatch the caller should transition
/// <see cref="ViewModels.SessionInitViewModel"/> to cert-error state.
/// </summary>
public sealed class BootMediaCertificateLoader
{
    private readonly SessionStartupCoordinator _coordinator;
    private readonly ILogger<BootMediaCertificateLoader> _logger;

    public BootMediaCertificateLoader(
        SessionStartupCoordinator coordinator,
        ILogger<BootMediaCertificateLoader> logger)
    {
        _coordinator = coordinator;
        _logger      = logger;
    }

    /// <summary>
    /// Configures the mTLS certificate on <paramref name="handler"/> and returns a result.
    /// Returns <c>false</c> if the certificate could not be loaded; the caller should
    /// display an error and NOT proceed to session registration.
    /// </summary>
    public (bool Success, string? ErrorMessage) Load(HttpClientHandler handler)
    {
        var result = _coordinator.ConfigureMtlsCertificate(handler);
        return (result.Success, result.ErrorMessage);
    }
}

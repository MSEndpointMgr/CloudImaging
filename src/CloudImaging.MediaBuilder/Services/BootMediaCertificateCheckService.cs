using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Checks whether an active boot media certificate is configured in the Cloud Imaging Portal,
/// via GET /api/bootmedia/certificate/metadata (T151, FR-050a). Generating a boot image
/// without a boot media certificate would produce media that can never establish mTLS with
/// the Device Gateway API, so <see cref="ViewModels.OperationSelectionViewModel"/> uses this to
/// keep the Generate Boot Image card disabled until a certificate is confirmed present.
/// </summary>
public sealed partial class BootMediaCertificateCheckService
{
    private readonly OperatorApiClient _operatorApiClient;
    private readonly ILogger<BootMediaCertificateCheckService> _logger;

    public BootMediaCertificateCheckService(OperatorApiClient operatorApiClient, ILogger<BootMediaCertificateCheckService> logger)
    {
        _operatorApiClient = operatorApiClient;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when an active boot media certificate is configured (metadata endpoint
    /// returns 200 with IsActive = true). Returns false when none is configured (404) or when
    /// the check itself fails (network error, unauthenticated, etc.) — a failed check is
    /// treated the same as "not configured" so Generate Boot Image stays safely disabled
    /// rather than silently permitting generation without a confirmed certificate.
    /// </summary>
    public async Task<bool> IsCertificateConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            var metadata = await _operatorApiClient.GetBootMediaCertMetadataAsync(ct);
            return metadata is { IsActive: true };
        }
        catch (Exception ex)
        {
            LogCheckFailed(_logger, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot media certificate metadata check failed; Generate Boot Image will be disabled.")]
    private static partial void LogCheckFailed(ILogger logger, Exception ex);
}

using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>Outcome of a boot media certificate check. A denied call is deliberately distinct
/// from "none configured", since the two need completely different remediation.</summary>
public enum BootMediaCertificateStatus
{
    Configured,
    NotConfigured,
    AccessDenied,
    CheckFailed,
}

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
    /// Reports whether an active boot media certificate is configured. Every non-success
    /// outcome keeps Generate Boot Image disabled, but each is reported distinctly so the UI
    /// never tells the user to create a certificate when the real problem is that the Operator
    /// API refused the call.
    /// </summary>
    public async Task<BootMediaCertificateStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var metadata = await _operatorApiClient.GetBootMediaCertMetadataAsync(ct);
            return metadata is { IsActive: true }
                ? BootMediaCertificateStatus.Configured
                : BootMediaCertificateStatus.NotConfigured;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            LogAccessDenied(_logger, (int)ex.StatusCode.Value, ex);
            return BootMediaCertificateStatus.AccessDenied;
        }
        catch (Exception ex)
        {
            LogCheckFailed(_logger, ex);
            return BootMediaCertificateStatus.CheckFailed;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operator API denied the boot media certificate metadata check ({StatusCode}). The signed-in user is most likely missing the CloudImaging.MediaBuilderAccess app role on the Cloud Imaging Operator API enterprise application.")]
    private static partial void LogAccessDenied(ILogger logger, int statusCode, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot media certificate metadata check failed; Generate Boot Image will be disabled.")]
    private static partial void LogCheckFailed(ILogger logger, Exception ex);
}

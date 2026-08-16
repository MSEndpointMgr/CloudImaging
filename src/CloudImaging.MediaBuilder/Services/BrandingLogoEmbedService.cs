using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Downloads the portal-configured branding logo so it can be embedded into the boot image WIM
/// alongside the Cloud Imaging Client (T064, FR-002a, FR-051).
///
/// Only downloads the logo's bytes — it does NOT write them into the mounted WIM itself. That
/// has to happen inside <see cref="BootImageGenerationService.GenerateAsync"/>, which may be
/// running in a separate, UAC-elevated worker process with no <see cref="OperatorApiClient"/>
/// of its own (see <see cref="BootImageGenerationService.RunElevatedWorkerAsync"/>). Callers
/// (<see cref="BootImageGenerationService.GenerateElevatedAsync"/>) download the bytes here, in
/// the always-authenticated parent process, then thread them through the same file-based IPC
/// used for the boot media certificate.
///
/// Retrieval failure (no branding logo configured in the portal, network error, etc.) is always
/// non-fatal: the Cloud Imaging Client falls back to the MSEndpointMgr default logo at runtime
/// when none is found alongside its executable (FR-002a), so this step simply returns null
/// rather than aborting boot image generation.
/// </summary>
public sealed partial class BrandingLogoEmbedService
{
    /// <summary>Path, relative to the Client's executable directory, where the Client looks for an embedded branding logo (FR-002a).</summary>
    public const string LogoRelativePath = "branding\\logo.png";

    private readonly HttpClient _http;
    private readonly OperatorApiClient _operatorApiClient;
    private readonly ILogger<BrandingLogoEmbedService> _logger;

    public BrandingLogoEmbedService(
        HttpClient http,
        OperatorApiClient operatorApiClient,
        ILogger<BrandingLogoEmbedService> logger)
    {
        _http              = http;
        _operatorApiClient = operatorApiClient;
        _logger            = logger;
    }

    /// <summary>
    /// Retrieves the branding logo SAS URL from the Operator API and downloads its bytes.
    /// Returns <c>null</c> when no branding logo is configured (the Operator API returns 404)
    /// or the request fails for any reason.
    /// </summary>
    public async Task<byte[]?> TryDownloadLogoAsync(CancellationToken ct = default)
    {
        try
        {
            var sas = await _operatorApiClient.GetBrandingLogoSasAsync(ct);
            if (sas is null)
            {
                LogNoLogoConfigured(_logger);
                return null;
            }

            var bytes = await _http.GetByteArrayAsync(sas.SasTokenUrl, ct);
            LogLogoDownloaded(_logger, bytes.Length);
            return bytes;
        }
        catch (Exception ex)
        {
            LogLogoDownloadFailed(_logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No branding logo is configured in the portal; the Client will use its default logo.")]
    private static partial void LogNoLogoConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding logo downloaded ({Bytes} bytes) for embedding.")]
    private static partial void LogLogoDownloaded(ILogger logger, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Branding logo retrieval failed — the Client will use its default logo.")]
    private static partial void LogLogoDownloadFailed(ILogger logger, Exception ex);
}

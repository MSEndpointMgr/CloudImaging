using System.Net;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal boot media certificate endpoints (T171, FR-062, FR-070).
/// Accessible only via Private Link — not exposed publicly.
///
/// GET /api/internal/cert/active         — returns active cert metadata (no PFX)
/// GET /api/internal/cert/active/pfx     — returns PFX bytes for the active cert
/// </summary>
public sealed partial class BootMediaCertificateFunctions
{
    private readonly BootMediaCertificateRepository _certRepo;
    private readonly KeyVaultCertificateService _kvService;
    private readonly ILogger<BootMediaCertificateFunctions> _logger;

    public BootMediaCertificateFunctions(
        BootMediaCertificateRepository certRepo,
        KeyVaultCertificateService kvService,
        ILogger<BootMediaCertificateFunctions> logger)
    {
        _certRepo = certRepo;
        _kvService = kvService;
        _logger = logger;
    }

    // ── GET /api/internal/cert/active ────────────────────────────────────────

    [Function("GetActiveCertMetadata")]
    public async Task<HttpResponseData> GetActiveCertMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/cert/active")] HttpRequestData req,
        FunctionContext context)
    {
        var cert = await _certRepo.GetActiveAsync(context.CancellationToken);
        if (cert is null)
        {
            LogNoCertRegistered(_logger);
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        LogCertMetadataReturned(_logger, cert.Thumbprint);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            thumbprint = cert.Thumbprint,
            notBefore = cert.NotBefore,
            notAfter = cert.NotAfter,
            isActive = cert.IsActive,
            keyVaultSecretName = cert.KeyVaultSecretName,
        }), context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/cert/active/pfx ────────────────────────────────────

    [Function("GetActiveCertPfx")]
    public async Task<HttpResponseData> GetActiveCertPfx(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/cert/active/pfx")] HttpRequestData req,
        FunctionContext context)
    {
        var cert = await _certRepo.GetActiveAsync(context.CancellationToken);
        if (cert is null)
        {
            LogNoCertRegistered(_logger);
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var pfxBytes = await _kvService.RetrievePfxAsync(cert.KeyVaultSecretName, context.CancellationToken);

        LogPfxRetrieved(_logger, cert.Thumbprint);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/x-pkcs12");
        await response.WriteBytesAsync(pfxBytes);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No active boot media certificate is registered.")]
    private static partial void LogNoCertRegistered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot media cert metadata returned for thumbprint {Thumbprint}.")]
    private static partial void LogCertMetadataReturned(ILogger logger, string thumbprint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot media PFX retrieved for thumbprint {Thumbprint}.")]
    private static partial void LogPfxRetrieved(ILogger logger, string thumbprint);
}

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;
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
/// GET  /api/internal/cert/active         — returns active cert metadata (no PFX), plus the previous
///                                          certificate while it is still accepted
/// GET  /api/internal/cert/active/pfx     — returns PFX bytes for the active cert
/// GET  /api/internal/cert/accepted       — every certificate the Device Gateway accepts now (public parts only)
/// POST /api/internal/cert/previous/stop  — stops accepting the previous certificate now
/// </summary>
public sealed partial class BootMediaCertificateFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BootMediaCertificateRepository _certRepo;
    private readonly KeyVaultCertificateService _kvService;
    private readonly ILogger<BootMediaCertificateFunctions> _logger;

    /// <param name="certRepo">Boot media certificate repository.</param>
    /// <param name="kvService">Key Vault certificate service used to read PFX bytes.</param>
    /// <param name="logger">Logger for this function group.</param>
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

    /// <summary>GET internal/cert/active. Returns active boot media certificate metadata (no PFX).</summary>
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

        var previous = (await _certRepo.GetAcceptedAsync(DateTimeOffset.UtcNow, context.CancellationToken))
            .FirstOrDefault(c => !c.IsActive);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            thumbprint = cert.Thumbprint,
            notBefore = cert.NotBefore,
            notAfter = cert.NotAfter,
            isActive = cert.IsActive,
            keyVaultSecretName = cert.KeyVaultSecretName,
            previous = previous is null ? null : new
            {
                thumbprint = previous.Thumbprint,
                notAfter = previous.NotAfter,
                acceptedUntil = previous.AcceptedUntil,
            },
        }), context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/cert/accepted ────────────────────────────────────────

    /// <summary>GET internal/cert/accepted. The certificates the Device Gateway accepts now, active first. Public parts only.</summary>
    [Function("GetAcceptedCertificates")]
    public async Task<HttpResponseData> GetAcceptedCertificates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/cert/accepted")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var accepted = new List<AcceptedBootMediaCertificate>();
        foreach (var cert in await _certRepo.GetAcceptedAsync(DateTimeOffset.UtcNow, ct))
        {
            var der = cert.CertificateDer;
            if (string.IsNullOrEmpty(der))
            {
                // Certificates issued before the public part was kept: read it from Key Vault once.
                var pfx = await _kvService.RetrievePfxAsync(cert.KeyVaultSecretName, ct);
                using var loaded = X509CertificateLoader.LoadPkcs12(pfx, password: null);
                der = Convert.ToBase64String(loaded.Export(X509ContentType.Cert));
                await _certRepo.SetCertificateDerAsync(cert.Thumbprint, der, ct);
            }
            accepted.Add(new AcceptedBootMediaCertificate(cert.Thumbprint, cert.IsActive, cert.IsActive ? null : cert.AcceptedUntil, der));
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(accepted, JsonOptions), ct);
        return response;
    }

    // ── POST /api/internal/cert/previous/stop ──────────────────────────────────

    /// <summary>POST internal/cert/previous/stop. Stops accepting the certificate the last rotation replaced.</summary>
    [Function("StopAcceptingPreviousCertificate")]
    public async Task<HttpResponseData> StopAcceptingPrevious(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/cert/previous/stop")] HttpRequestData req,
        FunctionContext context)
    {
        var stopped = await _certRepo.StopAcceptingPreviousAsync(DateTimeOffset.UtcNow, context.CancellationToken);
        return req.CreateResponse(stopped > 0 ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
    }

    // ── GET /api/internal/cert/active/pfx ────────────────────────────────────

    /// <summary>GET internal/cert/active/pfx. Returns PFX bytes for the active boot media certificate.</summary>
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

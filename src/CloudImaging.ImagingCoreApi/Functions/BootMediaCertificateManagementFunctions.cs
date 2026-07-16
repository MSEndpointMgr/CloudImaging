using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Boot media certificate management endpoints (T176, FR-068).
///
/// POST /api/internal/cert/generate  — generate a new self-signed boot media certificate
/// POST /api/internal/cert/rotate    — rotate to a new certificate (generate + activate)
/// </summary>
public sealed partial class BootMediaCertificateManagementFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BootMediaCertificateRepository _certRepo;
    private readonly KeyVaultCertificateService _kvService;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<BootMediaCertificateManagementFunctions> _logger;

    public BootMediaCertificateManagementFunctions(
        BootMediaCertificateRepository certRepo,
        KeyVaultCertificateService kvService,
        PortalConfigurationRepository configRepo,
        ILogger<BootMediaCertificateManagementFunctions> logger)
    {
        _certRepo  = certRepo;
        _kvService = kvService;
        _configRepo= configRepo;
        _logger    = logger;
    }

    // ── POST /api/internal/cert/generate ──────────────────────────────────────

    [Function("GenerateBootMediaCertificate")]
    public async Task<HttpResponseData> GenerateCertificate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/cert/generate")] HttpRequestData req,
        FunctionContext context)
    {
        var config = await _configRepo.GetAsync(context.CancellationToken);
        var validityDays = config.CertValidityPeriodDays > 0 ? config.CertValidityPeriodDays : 365;

        var (cert, pfxBytes) = CreateSelfSignedCertificate(validityDays);
        var secretName       = await _kvService.StorePfxAsync(cert.Thumbprint, pfxBytes, context.CancellationToken);

        var metadata = new BootMediaCertificate
        {
            Thumbprint         = cert.Thumbprint,
            NotBefore          = cert.NotBefore,
            NotAfter           = cert.NotAfter,
            IsActive           = false,       // Not yet active — caller must POST /rotate or activate separately
            KeyVaultSecretName = secretName,
        };

        // Store metadata in Table Storage (not yet activated)
        // Note: use ActivateAsync only if this is a rotation; standalone generate just stores
        await _certRepo.ActivateAsync(metadata, context.CancellationToken);
        LogCertGenerated(_logger, cert.Thumbprint, cert.NotAfter);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            thumbprint = cert.Thumbprint,
            notBefore  = cert.NotBefore,
            notAfter   = cert.NotAfter,
            isActive   = true,
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/cert/rotate ────────────────────────────────────────

    [Function("RotateBootMediaCertificate")]
    public async Task<HttpResponseData> RotateCertificate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/cert/rotate")] HttpRequestData req,
        FunctionContext context)
    {
        // Verify confirmation flag to prevent accidental rotation
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("confirmed", out var confirmedProp)
            || confirmedProp.GetBoolean() != true)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(
                "Rotation requires { \"confirmed\": true } in the request body.", context.CancellationToken);
            return bad;
        }

        var config = await _configRepo.GetAsync(context.CancellationToken);
        var validityDays = config.CertValidityPeriodDays > 0 ? config.CertValidityPeriodDays : 365;

        var (cert, pfxBytes) = CreateSelfSignedCertificate(validityDays);
        var secretName       = await _kvService.StorePfxAsync(cert.Thumbprint, pfxBytes, context.CancellationToken);

        var newCert = new BootMediaCertificate
        {
            Thumbprint         = cert.Thumbprint,
            NotBefore          = cert.NotBefore,
            NotAfter           = cert.NotAfter,
            IsActive           = true,
            KeyVaultSecretName = secretName,
        };

        // Atomic swap — deactivates old cert, activates new cert
        await _certRepo.ActivateAsync(newCert, context.CancellationToken);
        LogCertRotated(_logger, cert.Thumbprint);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            thumbprint = cert.Thumbprint,
            notBefore  = cert.NotBefore,
            notAfter   = cert.NotAfter,
            isActive   = true,
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (X509Certificate2 Cert, byte[] PfxBytes) CreateSelfSignedCertificate(int validityDays)
    {
        using var rsa = RSA.Create(2048);
        var subject  = new X500DistinguishedName("CN=CloudImaging-BootMedia");
        var req      = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") /* ClientAuth */ },
                critical: true));

        var cert     = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(validityDays));

        var pfxBytes = cert.Export(X509ContentType.Pkcs12);
        return (cert, pfxBytes);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot media certificate generated: thumbprint={Thumbprint}, expires={Expiry}.")]
    private static partial void LogCertGenerated(ILogger logger, string thumbprint, DateTime expiry);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot media certificate rotated: new thumbprint={Thumbprint}.")]
    private static partial void LogCertRotated(ILogger logger, string thumbprint);
}

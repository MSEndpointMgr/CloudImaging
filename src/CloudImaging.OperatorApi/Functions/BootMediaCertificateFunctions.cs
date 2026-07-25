using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Boot media certificate endpoints for Operator API (T184, FR-062).
///
/// GET /api/bootmedia/certificate/metadata — returns metadata (thumbprint, subject, validity)
///    of the active boot media certificate; MUST NOT return PFX bytes.
///    Accessible by CloudImaging.MediaBuilderAccess service role only.
///
/// GET /api/bootmedia/certificate/pfx — returns PFX bytes for the active cert.
///    Accessible by CloudImaging.MediaBuilderAccess service role only (T172).
/// </summary>
public sealed partial class BootMediaCertificateFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BootMediaCertificateFunctions> _logger;

    public BootMediaCertificateFunctions(
        ImagingCoreClient coreClient,
        ILogger<BootMediaCertificateFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    // ── GET /api/bootmedia/certificate/metadata ────────────────────────────

    [Function("GetBootMediaCertificateMetadata")]
    public async Task<HttpResponseData> GetCertificateMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootmedia/certificate/metadata")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetActiveBootCertMetadataAsync(context.CancellationToken);

        if (coreResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync(
                "No active boot media certificate is configured.", context.CancellationToken);
            return notFound;
        }

        if (!coreResponse.IsSuccessStatusCode)
        {
            LogUpstreamError(_logger, (int)coreResponse.StatusCode);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        // Parse the upstream response and strip any PFX/private-key material (FR-062)
        using var coreJson = await coreResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(coreJson, cancellationToken: context.CancellationToken);
        var root = doc.RootElement;

        // Build metadata-only response — NEVER include PFX bytes or private key
        var metadata = new
        {
            thumbprintDisplay = GetStringOrNull(root, "thumbprint") is string t && t.Length >= 8
                ? t[^8..].ToUpperInvariant() + "…"
                : GetStringOrNull(root, "thumbprint"),
            subject = GetStringOrNull(root, "keyVaultSecretName") ?? "Unknown",
            issuedAt = root.TryGetProperty("notBefore", out var nb) ? nb.GetString() : null,
            expiresAt = root.TryGetProperty("notAfter", out var na) ? na.GetString() : null,
            isActive = root.TryGetProperty("isActive", out var ia) ? ia.GetBoolean() : false,
        };

        LogMetadataReturned(_logger);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(metadata), context.CancellationToken);
        return response;
    }

    // ── GET /api/bootmedia/certificate/pfx ───────────────────────────────────

    [Function("GetBootMediaCertificatePfx")]
    public async Task<HttpResponseData> GetCertificatePfx(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootmedia/certificate/pfx")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetActiveBootCertPfxAsync(context.CancellationToken);

        if (coreResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        if (!coreResponse.IsSuccessStatusCode)
        {
            LogUpstreamError(_logger, (int)coreResponse.StatusCode);
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }

        var pfxBytes = await coreResponse.Content.ReadAsByteArrayAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/x-pkcs12");
        response.Headers.Add("Content-Disposition", "attachment; filename=\"bootmedia.pfx\"");
        await response.WriteBytesAsync(pfxBytes);
        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetStringOrNull(JsonElement el, string key) =>
        el.TryGetProperty(key, out var prop) ? prop.GetString() : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media certificate metadata returned.")]
    private static partial void LogMetadataReturned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "ImagingCoreApi returned HTTP {StatusCode} for cert request.")]
    private static partial void LogUpstreamError(ILogger logger, int statusCode);

    // ── POST /api/cert/generate (T177) ────────────────────────────────────────

    [Function("GenerateBootMediaCertificate")]
    public async Task<HttpResponseData> GenerateCertificate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cert/generate")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GenerateCertAsync(new { }, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    // ── POST /api/cert/rotate (T177) ──────────────────────────────────────────

    [Function("RotateBootMediaCertificate")]
    public async Task<HttpResponseData> RotateCertificate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cert/rotate")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = System.Text.Json.JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());
        var coreResponse = await _coreClient.RotateCertAsync(payload!, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyJsonAsync(
        HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(await coreResponse.Content.ReadAsStringAsync(ct), ct);
        }
        return response;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/wifi-networks: the managed Wi-Fi networks for the device's location. mTLS plus a
/// proof-of-possession over the location and a single-use nonce; no session token, because the
/// Client refreshes its networks right after the startup checks, before any session exists.
///
/// The response is a <see cref="ManagedWifiEnvelope"/> encrypted to the presented certificate, so
/// the passwords are never in plain text outside TLS and the Client can write the bytes straight
/// to its USB media. When the device presents the certificate a rotation replaced, the envelope is
/// also encrypted to the active one, so it stays readable after the media updates to a new boot image.
/// </summary>
public sealed partial class ManagedWifiNetworkFunctions
{
    private const int DefaultMaxSkewSeconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ImagingCoreClient _coreClient;
    private readonly DeviceSessionNonceStore _nonceStore;
    private readonly BootMediaCertificateThumbprintCache _certificates;
    private readonly TimeSpan _maxSignatureSkew;
    private readonly ILogger<ManagedWifiNetworkFunctions> _logger;

    /// <param name="coreClient">Imaging Core API client the networks are read from.</param>
    /// <param name="nonceStore">Single-use nonce store guarding proof-of-possession replay.</param>
    /// <param name="certificates">Accepted boot media certificates, for the active one's public key.</param>
    /// <param name="configuration">Used to resolve the configured proof-of-possession max clock skew.</param>
    /// <param name="logger">Logger for this function.</param>
    public ManagedWifiNetworkFunctions(ImagingCoreClient coreClient, DeviceSessionNonceStore nonceStore, BootMediaCertificateThumbprintCache certificates, IConfiguration configuration, ILogger<ManagedWifiNetworkFunctions> logger)
    {
        _coreClient = coreClient;
        _nonceStore = nonceStore;
        _certificates = certificates;
        _logger = logger;

        var skewSeconds = configuration.GetValue<int?>("MtlsProofOfPossession:MaxSkewSeconds") ?? DefaultMaxSkewSeconds;
        _maxSignatureSkew = TimeSpan.FromSeconds(skewSeconds > 0 ? skewSeconds : DefaultMaxSkewSeconds);
    }

    /// <summary>POST v1/wifi-networks. Returns the location's networks, encrypted to the presented certificate.</summary>
    [Function("GetManagedWifiNetworks")]
    public async Task<HttpResponseData> GetManagedWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/wifi-networks")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        ManagedWifiRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ManagedWifiRequest>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid request payload.", ct);
        }
        if (request is null || string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "SerialNumber is required.", ct);
        }

        if (!context.Items.TryGetValue(MtlsCertificateValidationMiddleware.ClientCertificateItemKey, out var certObj) || certObj is not X509Certificate2 clientCert)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Client certificate context is missing.", ct);
        }

        var popResult = DevicePayloadSignatureVerifier.VerifyWifi(clientCert, request, _maxSignatureSkew, DateTimeOffset.UtcNow);
        if (popResult != DevicePayloadSignatureVerifier.Result.Valid)
        {
            LogProofOfPossessionRejected(_logger, popResult, request.SerialNumber);
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, $"Proof-of-possession verification failed ({popResult}).", ct);
        }

        bool firstUse;
        try
        {
            firstUse = await _nonceStore.TryConsumeAsync(request.ProofOfPossession!.Nonce, DateTimeOffset.UtcNow.Add(_maxSignatureSkew), ct);
        }
        catch (Exception ex)
        {
            LogNonceStoreUnavailable(_logger, ex);
            return await ProblemAsync(req, HttpStatusCode.ServiceUnavailable, "Wi-Fi networks are temporarily unavailable. Please retry.", ct);
        }
        if (!firstUse)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Proof-of-possession has already been used.", ct);
        }

        using var coreResponse = await _coreClient.GetManagedWifiNetworksAsync(request.LocationId, ct);
        if (!coreResponse.IsSuccessStatusCode)
        {
            LogUpstreamFailed(_logger, (int)coreResponse.StatusCode);
            return await ProblemAsync(req, HttpStatusCode.ServiceUnavailable, "Wi-Fi networks are temporarily unavailable. Please retry.", ct);
        }

        var set = await coreResponse.Content.ReadFromJsonAsync<ManagedWifiNetworkSet>(JsonOptions, ct);
        if (set is null)
        {
            return await ProblemAsync(req, HttpStatusCode.ServiceUnavailable, "Wi-Fi networks are temporarily unavailable. Please retry.", ct);
        }

        LogDelivered(_logger, set.Networks.Count, request.LocationId, request.SerialNumber);
        var recipients = new List<X509Certificate2> { clientCert };
        var active = (await _certificates.GetAcceptedAsync(ct)).FirstOrDefault(c => c.IsActive);
        using var activeCert = active is { CertificateDer: { Length: > 0 } der } && !string.Equals(active.Thumbprint, clientCert.Thumbprint, StringComparison.OrdinalIgnoreCase)
            ? X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der))
            : null;
        if (activeCert is not null)
        {
            recipients.Add(activeCert);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteBytesAsync(ManagedWifiEnvelope.Encrypt(set, recipients), ct);
        return response;
    }

    private static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { title = status.ToString(), status = (int)status, detail }, JsonOptions), ct);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi proof-of-possession rejected ({Reason}) for device {SerialNumber}.")]
    private static partial void LogProofOfPossessionRejected(ILogger logger, DevicePayloadSignatureVerifier.Result reason, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nonce store unavailable; failing closed on a Wi-Fi network request.")]
    private static partial void LogNonceStoreUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Imaging Core returned HTTP {StatusCode} for managed Wi-Fi networks.")]
    private static partial void LogUpstreamFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered {Count} Wi-Fi networks for location {LocationId} to device {SerialNumber}.")]
    private static partial void LogDelivered(ILogger logger, int count, Guid? locationId, string serialNumber);
}

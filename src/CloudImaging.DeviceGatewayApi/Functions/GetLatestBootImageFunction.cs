using System.Net;
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
/// POST /api/v1/boot-image/latest: the device-facing boot image self-update check (T071b, FR-059a).
///
/// mTLS plus a proof-of-possession over what is asked for and a single-use nonce, like the other
/// token-free endpoints. The answer is a download link for a boot image, and every boot image carries
/// the boot media private key, so a caller has to prove it already holds that key. There is no
/// unsigned variant: boot media whose Client cannot sign the check must be prepared again.
///
/// No device-session token (exempt in <see cref="DeviceSessionTokenValidationMiddleware"/>): the
/// check runs at every boot, whether or not a session is ever created. It finds the catalog entry
/// flagged <c>isLatestPublished</c> for the requested architecture and base and issues a SAS URL for
/// it. A current image id is sent only by media prepared for testing: while that image is
/// pre-production the answer is 204 No Content, so the test media is never "updated" back to the
/// production image.
///
/// Response shape (on success):
/// {
///   "bootImageId": "...",
///   "version": "...",
///   "sha256Hash": "...",
///   "sasTokenUrl": "...",
///   "architecture": "x64",
///   "base": "WindowsPE"
/// }
/// </summary>
public sealed partial class GetLatestBootImageFunction
{
    private const int DefaultMaxSkewSeconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ImagingCoreClient _coreClient;
    private readonly DeviceSessionNonceStore _nonceStore;
    private readonly TimeSpan _maxSignatureSkew;
    private readonly ILogger<GetLatestBootImageFunction> _logger;

    /// <param name="coreClient">Imaging Core API client used to look up the boot image catalog.</param>
    /// <param name="nonceStore">Single-use nonce store guarding proof-of-possession replay.</param>
    /// <param name="configuration">Used to resolve the configured proof-of-possession max clock skew.</param>
    /// <param name="logger">Logger for this function.</param>
    public GetLatestBootImageFunction(ImagingCoreClient coreClient, DeviceSessionNonceStore nonceStore, IConfiguration configuration, ILogger<GetLatestBootImageFunction> logger)
    {
        _coreClient = coreClient;
        _nonceStore = nonceStore;
        _logger = logger;

        var skewSeconds = configuration.GetValue<int?>("MtlsProofOfPossession:MaxSkewSeconds") ?? DefaultMaxSkewSeconds;
        _maxSignatureSkew = TimeSpan.FromSeconds(skewSeconds > 0 ? skewSeconds : DefaultMaxSkewSeconds);
    }

    /// <summary>POST v1/boot-image/latest. Verifies the signed request, then returns a SAS URL for the latest published boot image for the media.</summary>
    [Function("CheckLatestBootImage")]
    public async Task<HttpResponseData> Check(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boot-image/latest")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        LatestBootImageRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LatestBootImageRequest>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid request payload.", ct);
        }
        if (request is null || string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "SerialNumber is required.", ct);
        }
        if (!Enum.IsDefined(request.Base))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "base must be \"WindowsPE\" or \"WindowsRE\".", ct);
        }

        if (!context.Items.TryGetValue(MtlsCertificateValidationMiddleware.ClientCertificateItemKey, out var certObj) || certObj is not X509Certificate2 clientCert)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Client certificate context is missing.", ct);
        }

        var popResult = DevicePayloadSignatureVerifier.VerifyBootImage(clientCert, request, _maxSignatureSkew, DateTimeOffset.UtcNow);
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
            return await ProblemAsync(req, HttpStatusCode.ServiceUnavailable, "The boot image update check is temporarily unavailable. Please retry.", ct);
        }
        if (!firstUse)
        {
            return await ProblemAsync(req, HttpStatusCode.Unauthorized, "Proof-of-possession has already been used.", ct);
        }

        return await LookUpAsync(req, MachineArchitecturePlatform.Slug(request.Architecture), request.Base.ToString(), request.CurrentBootImageId, ct);
    }

    private async Task<HttpResponseData> LookUpAsync(HttpRequestData req, string requestedArchitecture, string requestedBase, Guid? currentBootImageId, CancellationToken ct)
    {
        var listResponse = await _coreClient.GetBootImagesAsync(ct);
        if (!listResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)listResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Boot image catalog is temporarily unavailable.", ct);
            return err;
        }

        using var listJson = await listResponse.Content.ReadAsStreamAsync(ct);
        using var listDoc = await JsonDocument.ParseAsync(listJson, cancellationToken: ct);

        if (LatestImageSelector.IsPreProduction(listDoc.RootElement, currentBootImageId))
        {
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        var latest = LatestImageSelector.FindLatestForArchitecture(listDoc.RootElement, requestedArchitecture, requestedBase);

        if (latest is null)
        {
            return await ProblemAsync(req, HttpStatusCode.NotFound, "No boot image is published for this architecture and base.", ct);
        }

        var bootImageId = latest.Value.GetProperty("bootImageId").GetGuid();
        var version = latest.Value.GetProperty("version").GetString()!;

        var sasResponse = await _coreClient.GetBootImageSasUrlAsync(bootImageId, ct);
        if (!sasResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)sasResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Boot image download URL is temporarily unavailable.", ct);
            return err;
        }

        using var sasJson = await sasResponse.Content.ReadAsStreamAsync(ct);
        using var sasDoc = await JsonDocument.ParseAsync(sasJson, cancellationToken: ct);
        var sasRoot = sasDoc.RootElement;

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            bootImageId,
            version,
            sha256Hash = sasRoot.GetProperty("sha256Hash").GetString(),
            sasTokenUrl = sasRoot.GetProperty("sasTokenUrl").GetString(),
            architecture = requestedArchitecture,
            @base = requestedBase,
        }), ct);
        return response;
    }

    private static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { title = status.ToString(), status = (int)status, detail }, JsonOptions), ct);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ImagingCoreApi returned status {StatusCode} for the latest boot image lookup.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image check proof-of-possession rejected ({Reason}) for device {SerialNumber}.")]
    private static partial void LogProofOfPossessionRejected(ILogger logger, DevicePayloadSignatureVerifier.Result reason, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nonce store unavailable; failing closed on a boot image update check.")]
    private static partial void LogNonceStoreUnavailable(ILogger logger, Exception ex);
}

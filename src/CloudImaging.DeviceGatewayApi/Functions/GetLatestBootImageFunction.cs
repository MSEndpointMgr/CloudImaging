using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/boot-image/latest — Device-facing boot image self-update check (T071b, FR-059a).
///
/// Requires the boot-media mTLS client certificate (validated by
/// <see cref="Middleware.MtlsCertificateValidationMiddleware"/> for every request, per FR-011)
/// but — unlike every other endpoint on this API — does NOT require a device-session bearer
/// token: it is exempt in <see cref="Middleware.DeviceSessionTokenValidationMiddleware"/>. This
/// check happens independently of session bootstrap, so the Cloud Imaging Client can run it in
/// the background at every boot regardless of whether a session is ever created.
///
/// Forwards to ImagingCoreApi's existing boot image catalog endpoints (T066/T067), finds the
/// entry flagged <c>isLatestPublished</c> for the requested <c>?architecture=x64|arm64</c>
/// (x64 when omitted, since older Clients never send it), and issues a SAS URL for it.
///
/// <c>?currentBootImageId=</c> is sent only by a stick prepared for testing. When that image is
/// still in pre-production, the answer is 204 No Content so the test stick is never "updated"
/// back to the production image.
///
/// Response shape (on success):
/// {
///   "bootImageId": "...",
///   "version": "...",
///   "sha256Hash": "...",
///   "sasTokenUrl": "...",
///   "architecture": "x64"
/// }
/// </summary>
public sealed partial class GetLatestBootImageFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<GetLatestBootImageFunction> _logger;

    public GetLatestBootImageFunction(ImagingCoreClient coreClient, ILogger<GetLatestBootImageFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetLatestBootImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boot-image/latest")] HttpRequestData req,
        FunctionContext context)
    {
        if (!LatestImageSelector.TryReadArchitecture(req.Query["architecture"], out var requestedArchitecture))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("architecture must be \"x64\" or \"arm64\".", context.CancellationToken);
            return bad;
        }

        var listResponse = await _coreClient.GetBootImagesAsync(context.CancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)listResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Boot image catalog is temporarily unavailable.", context.CancellationToken);
            return err;
        }

        using var listJson = await listResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var listDoc = await JsonDocument.ParseAsync(listJson, cancellationToken: context.CancellationToken);

        Guid? currentBootImageId = Guid.TryParse(req.Query["currentBootImageId"], out var current) ? current : null;
        if (LatestImageSelector.IsPreProduction(listDoc.RootElement, currentBootImageId))
        {
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        var latest = LatestImageSelector.FindLatestForArchitecture(listDoc.RootElement, requestedArchitecture);

        if (latest is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var bootImageId = latest.Value.GetProperty("bootImageId").GetGuid();
        var version = latest.Value.GetProperty("version").GetString()!;
        var architecture = requestedArchitecture;

        var sasResponse = await _coreClient.GetBootImageSasUrlAsync(bootImageId, context.CancellationToken);
        if (!sasResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)sasResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Boot image download URL is temporarily unavailable.", context.CancellationToken);
            return err;
        }

        using var sasJson = await sasResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var sasDoc = await JsonDocument.ParseAsync(sasJson, cancellationToken: context.CancellationToken);
        var sasRoot = sasDoc.RootElement;

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            bootImageId,
            version,
            sha256Hash = sasRoot.GetProperty("sha256Hash").GetString(),
            sasTokenUrl = sasRoot.GetProperty("sasTokenUrl").GetString(),
            architecture,
        }), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ImagingCoreApi returned status {StatusCode} for the latest boot image lookup.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode);
}

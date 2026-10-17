using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/recovery-image/latest?architecture=x64|arm64: device-facing lookup of the currently
/// published recovery (WinRE) image for the device's architecture (x64 when omitted), mirroring
/// <see cref="GetLatestBootImageFunction"/>. There is no per-session manual assignment.
///
/// Requires a valid device-session Bearer token (DeviceSessionTokenValidationMiddleware), since —
/// unlike the boot image check — this only makes sense once a session/imaging pipeline exists.
///
/// Response shape (on success):
/// {
///   "version": "...",
///   "sha256Hash": "...",
///   "sasTokenUrl": "...",
///   "architecture": "x64"
/// }
/// </summary>
public sealed partial class GetLatestRecoveryImageFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<GetLatestRecoveryImageFunction> _logger;

    /// <param name="coreClient">Imaging Core API client used to look up the recovery image catalog.</param>
    /// <param name="logger">Logger for this function.</param>
    public GetLatestRecoveryImageFunction(ImagingCoreClient coreClient, ILogger<GetLatestRecoveryImageFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET v1/recovery-image/latest. Returns a SAS URL for the currently published recovery (WinRE) image.</summary>
    [Function("GetLatestRecoveryImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/recovery-image/latest")] HttpRequestData req,
        FunctionContext context)
    {
        if (!LatestImageSelector.TryReadArchitecture(req.Query["architecture"], out var architecture))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("architecture must be \"x64\" or \"arm64\".", context.CancellationToken);
            return bad;
        }

        var listResponse = await _coreClient.GetRecoveryImagesAsync(context.CancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)listResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Recovery image catalog is temporarily unavailable.", context.CancellationToken);
            return err;
        }

        using var listJson = await listResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var listDoc = await JsonDocument.ParseAsync(listJson, cancellationToken: context.CancellationToken);

        var latest = LatestImageSelector.FindLatestForArchitecture(listDoc.RootElement, architecture);

        if (latest is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var recoveryImageId = latest.Value.GetProperty("recoveryImageId").GetGuid();
        var version = latest.Value.GetProperty("version").GetString()!;

        var sasResponse = await _coreClient.GetRecoveryImageSasUrlAsync(recoveryImageId, context.CancellationToken);
        if (!sasResponse.IsSuccessStatusCode)
        {
            LogCoreApiError(_logger, (int)sasResponse.StatusCode);
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("Recovery image download URL is temporarily unavailable.", context.CancellationToken);
            return err;
        }

        using var sasJson = await sasResponse.Content.ReadAsStreamAsync(context.CancellationToken);
        using var sasDoc = await JsonDocument.ParseAsync(sasJson, cancellationToken: context.CancellationToken);
        var sasRoot = sasDoc.RootElement;

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            version,
            sha256Hash = sasRoot.GetProperty("sha256Hash").GetString(),
            sasTokenUrl = sasRoot.GetProperty("sasTokenUrl").GetString(),
            architecture,
        }), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ImagingCoreApi returned status {StatusCode} for the latest recovery image lookup.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode);
}

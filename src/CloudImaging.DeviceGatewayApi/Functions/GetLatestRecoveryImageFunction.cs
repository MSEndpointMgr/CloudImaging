using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// GET /api/v1/recovery-image/latest — Device-facing lookup of the currently published recovery
/// (WinRE) image, mirroring <see cref="GetLatestBootImageFunction"/>. The Client always downloads
/// and applies the "latest published" recovery image — there is no per-session manual assignment,
/// matching the boot-image self-update convention.
///
/// Requires a valid device-session Bearer token (DeviceSessionTokenValidationMiddleware), since —
/// unlike the boot image check — this only makes sense once a session/imaging pipeline exists.
///
/// Response shape (on success):
/// {
///   "version": "...",
///   "sha256Hash": "...",
///   "sasTokenUrl": "..."
/// }
/// </summary>
public sealed partial class GetLatestRecoveryImageFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<GetLatestRecoveryImageFunction> _logger;

    public GetLatestRecoveryImageFunction(ImagingCoreClient coreClient, ILogger<GetLatestRecoveryImageFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetLatestRecoveryImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/recovery-image/latest")] HttpRequestData req,
        FunctionContext context)
    {
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

        JsonElement? latest = null;
        foreach (var image in listDoc.RootElement.EnumerateArray())
        {
            if (image.TryGetProperty("isLatestPublished", out var flag) && flag.GetBoolean())
            {
                latest = image;
                break;
            }
        }

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
        }), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ImagingCoreApi returned status {StatusCode} for the latest recovery image lookup.")]
    private static partial void LogCoreApiError(ILogger logger, int statusCode);
}

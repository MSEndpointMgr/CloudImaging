using System.Net;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions/{sessionId}/sas/refresh — Proxy SAS token refresh to ImagingCoreApi (T051, FR-025).
/// Requires valid device-session Bearer token.
/// </summary>
public sealed partial class RefreshSasTokenFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<RefreshSasTokenFunction> _logger;

    public RefreshSasTokenFunction(
        ImagingCoreClient coreClient,
        ILogger<RefreshSasTokenFunction> logger)
    {
        _coreClient = coreClient;
        _logger     = logger;
    }

    [Function("RefreshSasToken")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions/{sessionId}/sas/refresh")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var coreResponse = await _coreClient.RefreshSasTokenAsync(sessionGuid, context.CancellationToken);

        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.IsSuccessStatusCode)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
            await response.WriteStringAsync(content, context.CancellationToken);
        }

        LogSasRelayed(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SAS refresh relayed for session {SessionId} — ImagingCore returned HTTP {StatusCode}.")]
    private static partial void LogSasRelayed(ILogger logger, Guid sessionId, int statusCode);
}

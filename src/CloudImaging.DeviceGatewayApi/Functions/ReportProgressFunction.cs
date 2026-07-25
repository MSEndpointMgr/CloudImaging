using System.Net;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions/{sessionId}/progress — Relay imaging progress from device to ImagingCoreApi (T050, FR-007).
/// Requires valid device-session Bearer token.
/// </summary>
public sealed partial class ReportProgressFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<ReportProgressFunction> _logger;

    public ReportProgressFunction(
        ImagingCoreClient coreClient,
        ILogger<ReportProgressFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("ReportProgress")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions/{sessionId}/progress")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());

        var coreResponse = await _coreClient.ReportProgressAsync(sessionGuid, payload!, context.CancellationToken);

        LogProgressRelayed(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Progress relayed for session {SessionId} — ImagingCore returned HTTP {StatusCode}.")]
    private static partial void LogProgressRelayed(ILogger logger, Guid sessionId, int statusCode);
}

using System.Net;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Functions;

/// <summary>
/// POST /api/v1/sessions/{sessionId}/logs/upload-url — Device-facing request for a short-lived
/// write SAS URL the Client can PUT its current local diagnostic log to. Called best-effort by
/// <see cref="ViewModels.ImagingWorkflowViewModel"/>-equivalent client logic on any terminal
/// imaging failure, so support can inspect the full log without depending on the technician
/// retrieving it from WinPE before reboot. Requires a valid device-session Bearer token
/// (DeviceSessionTokenValidationMiddleware).
///
/// Response shape (on success):
/// {
///   "fileName": "client-20260817T120000Z.log",
///   "uploadUrl": "...",
///   "expiresAt": "..."
/// }
/// </summary>
public sealed partial class RequestSessionLogUploadUrlFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<RequestSessionLogUploadUrlFunction> _logger;

    public RequestSessionLogUploadUrlFunction(ImagingCoreClient coreClient, ILogger<RequestSessionLogUploadUrlFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("RequestSessionLogUploadUrl")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/sessions/{sessionId}/logs/upload-url")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.RequestSessionLogUploadUrlAsync(sessionGuid, context.CancellationToken);
        LogUploadUrlRelayed(_logger, sessionGuid, (int)coreResponse.StatusCode);

        var response = req.CreateResponse((HttpStatusCode)(int)coreResponse.StatusCode);
        var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
        if (!string.IsNullOrEmpty(content))
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(content, context.CancellationToken);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Session log upload URL requested for session {SessionId} — ImagingCore returned HTTP {StatusCode}.")]
    private static partial void LogUploadUrlRelayed(ILogger logger, Guid sessionId, int statusCode);
}

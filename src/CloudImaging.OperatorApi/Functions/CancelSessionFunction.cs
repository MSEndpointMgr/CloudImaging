using System.Net;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// DELETE /api/sessions/{sessionId} — Operator-facing endpoint to remove a coupled session that
/// was aborted before imaging started (e.g. a rebooted device or restarted VM).
/// Requires <c>CloudImaging.PortalAccess</c> role (enforced by middleware).
/// Proxies to ImagingCoreApi DELETE /api/internal/sessions/{sessionId} over Private Link.
/// </summary>
public sealed partial class CancelSessionFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<CancelSessionFunction> _logger;

    public CancelSessionFunction(
        ImagingCoreClient coreClient,
        ILogger<CancelSessionFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function(nameof(CancelSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.CancelSessionAsync(sessionGuid, context.CancellationToken);

        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
            await response.WriteStringAsync(content, context.CancellationToken);
        }

        LogCancelResult(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} cancel proxied to Imaging Core API with status {StatusCode}.")]
    private static partial void LogCancelResult(ILogger logger, Guid sessionId, int statusCode);
}

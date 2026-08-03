using System.Net;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Portal-facing session query endpoints (FR-031). Require <c>CloudImaging.PortalAccess</c>
/// (enforced by <see cref="Middleware.AppRoleAuthorizationMiddleware"/>). Both proxy to the
/// Imaging Core API over Private Link and return the Core session summary projection verbatim.
///
/// GET /api/sessions        — list device sessions for the portal devices view
/// GET /api/sessions/{id}   — get a single device session summary
/// </summary>
public sealed partial class SessionQueryFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<SessionQueryFunctions> _logger;

    public SessionQueryFunctions(
        ImagingCoreClient coreClient,
        ILogger<SessionQueryFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetSessions")]
    public async Task<HttpResponseData> GetSessions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions")] HttpRequestData req,
        FunctionContext context)
    {
        var filter = System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("filter");
        var coreResponse = await _coreClient.GetSessionsAsync(filter, context.CancellationToken);
        LogSessionsProxy(_logger, (int)coreResponse.StatusCode);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("GetSession")]
    public async Task<HttpResponseData> GetSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.GetSessionAsync(sessionGuid, context.CancellationToken);
        LogSessionProxy(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyJsonAsync(
        HttpRequestData req, HttpResponseMessage upstream, CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)(int)upstream.StatusCode);
        var content = await upstream.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "List sessions proxy returned HTTP {StatusCode}.")]
    private static partial void LogSessionsProxy(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Get session {SessionId} proxy returned HTTP {StatusCode}.")]
    private static partial void LogSessionProxy(ILogger logger, Guid sessionId, int statusCode);
}

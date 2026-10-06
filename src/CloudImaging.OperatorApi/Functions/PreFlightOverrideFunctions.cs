using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Portal-facing proxies for administrator pre-flight overrides. PortalAccess only; the portal
/// backend enforces the Administrator role for approve and revoke and supplies the acting user.
///
/// GET    /api/preflight-overrides
/// POST   /api/sessions/{id}/preflight-override
/// DELETE /api/preflight-overrides?serialNumber=&amp;revokedBy=
/// </summary>
public sealed partial class PreFlightOverrideFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<PreFlightOverrideFunctions> _logger;

    public PreFlightOverrideFunctions(ImagingCoreClient coreClient, ILogger<PreFlightOverrideFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("ListPreFlightOverrides")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preflight-overrides")] HttpRequestData req,
        FunctionContext context)
    {
        using var coreResponse = await _coreClient.ListPreFlightOverridesAsync(context.CancellationToken);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("ApprovePreFlightOverride")]
    public async Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{id}/preflight-override")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var sessionId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        JsonElement payload;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.ApprovePreFlightOverrideAsync(sessionId, payload, context.CancellationToken);
        LogApproval(_logger, sessionId, (int)coreResponse.StatusCode);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("RevokePreFlightOverride")]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "preflight-overrides")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var serialNumber = query.Get("serialNumber");
        var revokedBy = query.Get("revokedBy");
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(revokedBy))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.RevokePreFlightOverrideAsync(serialNumber, revokedBy, context.CancellationToken);
        return await ProxyAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyAsync(HttpRequestData req, HttpResponseMessage upstream, CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)(int)upstream.StatusCode);
        var content = await upstream.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            response.Headers.Add("Content-Type", upstream.Content.Headers.ContentType?.MediaType ?? "application/json");
            await response.WriteStringAsync(content, ct);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pre-flight override approval for session {SessionId} returned HTTP {StatusCode}.")]
    private static partial void LogApproval(ILogger logger, Guid sessionId, int statusCode);
}

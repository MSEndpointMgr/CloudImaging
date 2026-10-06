using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Portal-facing Autopilot registration endpoints. PortalAccess only (not in the Media Builder
/// allowlist); the portal backend enforces the Approver/Administrator/Technician user roles and
/// supplies the deciding user's identity from their validated token.
///
/// GET    /api/autopilot/registrations
/// GET    /api/autopilot/registrations?view=history&amp;from=&amp;to=
/// GET    /api/autopilot/registrations/{id}
/// POST   /api/autopilot/registrations/{id}/approve|reject|retry
/// GET    /api/autopilot/group-tags
/// POST   /api/autopilot/group-tags
/// PUT    /api/autopilot/group-tags/{id}
/// DELETE /api/autopilot/group-tags/{id}
/// </summary>
public sealed partial class AutopilotFunctions
{
    private static readonly HashSet<string> DecisionActions = new(StringComparer.OrdinalIgnoreCase) { "approve", "reject", "retry" };

    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<AutopilotFunctions> _logger;

    public AutopilotFunctions(ImagingCoreClient coreClient, ILogger<AutopilotFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("ListAutopilotRegistrations")]
    public async Task<HttpResponseData> ListRegistrations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "autopilot/registrations")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        using var coreResponse = string.Equals(query.Get("view"), "history", StringComparison.OrdinalIgnoreCase)
            ? await _coreClient.ListHandledAutopilotRegistrationsAsync(query.Get("from"), query.Get("to"), context.CancellationToken)
            : await _coreClient.ListAutopilotRegistrationsAsync(context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("GetAutopilotRegistration")]
    public async Task<HttpResponseData> GetRegistration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "autopilot/registrations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.GetAutopilotRegistrationAsync(requestId, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("DecideAutopilotRegistration")]
    public async Task<HttpResponseData> Decide(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "autopilot/registrations/{id}/{action}")] HttpRequestData req,
        string id,
        string action,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var requestId) || !DecisionActions.Contains(action))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.DecideAutopilotRegistrationAsync(requestId, action.ToLowerInvariant(), payload, context.CancellationToken);
        LogDecision(_logger, requestId, action, (int)coreResponse.StatusCode);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("ListAutopilotGroupTags")]
    public async Task<HttpResponseData> ListGroupTags(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "autopilot/group-tags")] HttpRequestData req,
        FunctionContext context)
    {
        using var coreResponse = await _coreClient.ListAutopilotGroupTagsAsync(context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("CreateAutopilotGroupTag")]
    public async Task<HttpResponseData> CreateGroupTag(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "autopilot/group-tags")] HttpRequestData req,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.CreateAutopilotGroupTagAsync(payload, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("UpdateAutopilotGroupTag")]
    public async Task<HttpResponseData> UpdateGroupTag(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "autopilot/group-tags/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var tagId) || payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.UpdateAutopilotGroupTagAsync(tagId, payload, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("DeleteAutopilotGroupTag")]
    public async Task<HttpResponseData> DeleteGroupTag(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "autopilot/group-tags/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var tagId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var coreResponse = await _coreClient.DeleteAutopilotGroupTagAsync(tagId, context.CancellationToken);
        return await ProxyJsonAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<JsonElement?> ReadPayloadAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseData> ProxyJsonAsync(HttpRequestData req, HttpResponseMessage upstream, CancellationToken ct)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} {Action} proxy returned HTTP {StatusCode}.")]
    private static partial void LogDecision(ILogger logger, Guid requestId, string action, int statusCode);
}

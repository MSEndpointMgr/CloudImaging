using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// POST /api/sessions/{sessionId}/assign — Operator-facing single-session image assign endpoint (T040a, FR-025).
/// Requires <c>CloudImaging.PortalAccess</c> role (enforced by middleware).
/// Proxies to ImagingCoreApi POST /api/internal/sessions/{sessionId}/assign over Private Link.
/// </summary>
public sealed partial class AssignSessionFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<AssignSessionFunction> _logger;

    public AssignSessionFunction(
        ImagingCoreClient coreClient,
        ILogger<AssignSessionFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function(nameof(AssignSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/assign")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());

        var coreResponse = await _coreClient.AssignSessionAsync(sessionGuid, payload!, context.CancellationToken);

        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
            await response.WriteStringAsync(content, context.CancellationToken);
        }

        LogAssignResult(_logger, sessionGuid, (int)coreResponse.StatusCode);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Assign session {SessionId} proxy returned HTTP {StatusCode}.")]
    private static partial void LogAssignResult(ILogger logger, Guid sessionId, int statusCode);
}

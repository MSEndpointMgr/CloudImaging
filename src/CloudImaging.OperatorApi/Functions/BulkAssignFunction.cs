using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// POST /api/sessions/bulk-assign — Bulk image assignment proxy (T076, FR-035).
/// Requires CloudImaging.PortalAccess role (enforced by AppRoleAuthorizationMiddleware).
/// Proxies to ImagingCoreApi POST /api/internal/sessions/bulk-assign.
/// </summary>
public sealed partial class BulkAssignFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<BulkAssignFunction> _logger;

    public BulkAssignFunction(
        ImagingCoreClient coreClient,
        ILogger<BulkAssignFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function(nameof(BulkAssignFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/bulk-assign")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());

        var coreResponse = await _coreClient.BulkAssignAsync(payload!, context.CancellationToken);
        LogBulkResult(_logger, (int)coreResponse.StatusCode);

        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(
                await coreResponse.Content.ReadAsStringAsync(context.CancellationToken),
                context.CancellationToken);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Bulk assign proxied — ImagingCore HTTP {StatusCode}.")]
    private static partial void LogBulkResult(ILogger logger, int statusCode);
}

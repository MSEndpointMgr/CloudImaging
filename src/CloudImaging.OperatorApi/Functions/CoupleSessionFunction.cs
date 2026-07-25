using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// POST /api/sessions/couple — Operator-facing passcode coupling endpoint (T040, FR-021).
/// Requires <c>CloudImaging.PortalAccess</c> role (enforced by middleware).
/// Proxies to ImagingCoreApi POST /api/internal/sessions/couple over Private Link.
/// </summary>
public sealed partial class CoupleSessionFunction
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<CoupleSessionFunction> _logger;

    public CoupleSessionFunction(
        ImagingCoreClient coreClient,
        ILogger<CoupleSessionFunction> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function(nameof(CoupleSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/couple")] HttpRequestData req,
        FunctionContext context)
    {
        // Forward the request body ({passcode}) to ImagingCoreApi
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(body.RootElement.GetRawText());

        var coreResponse = await _coreClient.CoupleSessionAsync(payload!, context.CancellationToken);

        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(context.CancellationToken);
            await response.WriteStringAsync(content, context.CancellationToken);
        }

        LogCoupleResult(_logger, (int)coreResponse.StatusCode);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Couple session proxy returned HTTP {StatusCode}.")]
    private static partial void LogCoupleResult(ILogger logger, int statusCode);
}

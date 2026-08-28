using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Location catalog proxy endpoints (Location Labels feature).
///
/// GET    /api/locations      — lists all locations; also allowed for CloudImaging.MediaBuilderAccess
///                               so Media Builder can populate the location picker
/// POST   /api/locations      — creates a location (PortalAccess only — portal server gates Administrator)
/// DELETE /api/locations/{id} — deletes a location (PortalAccess only)
/// </summary>
public sealed partial class LocationFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<LocationFunctions> _logger;

    public LocationFunctions(ImagingCoreClient coreClient, ILogger<LocationFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function("GetLocations")]
    public async Task<HttpResponseData> GetLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "locations")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetLocationsAsync(context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("CreateLocation")]
    public async Task<HttpResponseData> CreateLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "locations")] HttpRequestData req,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var coreResponse = await _coreClient.CreateLocationAsync(payload!, context.CancellationToken);
        LogCreated(_logger, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("DeleteLocation")]
    public async Task<HttpResponseData> DeleteLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "locations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var locationId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.DeleteLocationAsync(locationId, context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyResponseAsync(
        HttpRequestData req,
        HttpResponseMessage coreResponse,
        CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Location created — HTTP {StatusCode}.")]
    private static partial void LogCreated(ILogger logger, int statusCode);
}

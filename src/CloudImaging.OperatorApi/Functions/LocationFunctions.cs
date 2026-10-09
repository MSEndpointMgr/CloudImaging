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
/// GET    /api/locations: lists all locations; also allowed for CloudImaging.MediaBuilderAccess
///        so Media Builder can populate the location picker
/// POST   /api/locations: creates a location (PortalAccess only; portal server gates Administrator)
/// PUT    /api/locations/{id}: updates a location's name, region and country (PortalAccess only)
/// DELETE /api/locations/{id}: deletes a location (PortalAccess only)
/// </summary>
public sealed partial class LocationFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<LocationFunctions> _logger;

    /// <param name="coreClient">Imaging Core API client the location requests are forwarded to.</param>
    /// <param name="logger">Logger for this function group.</param>
    public LocationFunctions(ImagingCoreClient coreClient, ILogger<LocationFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET locations. Lists all location labels.</summary>
    [Function("GetLocations")]
    public async Task<HttpResponseData> GetLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "locations")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetLocationsAsync(context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>POST locations. Creates a new location label.</summary>
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

    /// <summary>PUT locations/{id}. Updates a location label's name, region and country.</summary>
    [Function("UpdateLocation")]
    public async Task<HttpResponseData> UpdateLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "locations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var locationId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var coreResponse = await _coreClient.UpdateLocationAsync(locationId, payload!, context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>DELETE locations/{id}. Deletes a location label.</summary>
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
        var content = await coreResponse.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            // Core reports validation failures (e.g. an invalid country code) as plain text; keep them for the portal.
            var isJson = coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            response.Headers.Add("Content-Type", isJson ? "application/json" : "text/plain");
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Location created — HTTP {StatusCode}.")]
    private static partial void LogCreated(ILogger logger, int statusCode);
}

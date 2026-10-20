using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Wi-Fi network proxy endpoints.
///
/// GET    /api/wifi-networks: lists networks, without passwords (PortalAccess)
/// POST   /api/wifi-networks: creates a network (PortalAccess; portal server gates Administrator)
/// PUT    /api/wifi-networks/order: sets the order (PortalAccess)
/// PUT    /api/wifi-networks/{id}: updates a network (PortalAccess)
/// DELETE /api/wifi-networks/{id}: deletes a network (PortalAccess)
/// PUT    /api/locations/{id}/wifi-networks: sets a location's networks (PortalAccess)
/// GET    /api/wifi-networks/managed?locationId=: a location's networks with passwords, for writing
///        to USB media. CloudImaging.MediaBuilderAccess only; PortalAccess alone is refused.
/// </summary>
public sealed partial class WifiNetworkFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<WifiNetworkFunctions> _logger;

    /// <param name="coreClient">Imaging Core API client the requests are forwarded to.</param>
    /// <param name="logger">Logger for this function group.</param>
    public WifiNetworkFunctions(ImagingCoreClient coreClient, ILogger<WifiNetworkFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>GET wifi-networks. Lists saved networks, without passwords.</summary>
    [Function("GetWifiNetworks")]
    public async Task<HttpResponseData> GetWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "wifi-networks")] HttpRequestData req,
        FunctionContext context) =>
        await ProxyResponseAsync(req, await _coreClient.GetWifiNetworksAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>POST wifi-networks. Creates a network.</summary>
    [Function("CreateWifiNetwork")]
    public async Task<HttpResponseData> CreateWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "wifi-networks")] HttpRequestData req,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.CreateWifiNetworkAsync(payload, context.CancellationToken);
        LogChanged(_logger, "created", (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>PUT wifi-networks/order. Sets the order devices try networks in.</summary>
    [Function("SetWifiNetworkOrder")]
    public async Task<HttpResponseData> SetWifiNetworkOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "wifi-networks/order")] HttpRequestData req,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.SetWifiNetworkOrderAsync(payload, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>PUT wifi-networks/{id}. Updates a network; an omitted password keeps the stored one.</summary>
    [Function("UpdateWifiNetwork")]
    public async Task<HttpResponseData> UpdateWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "wifi-networks/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var networkId) || payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.UpdateWifiNetworkAsync(networkId, payload, context.CancellationToken);
        LogChanged(_logger, "updated", (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>DELETE wifi-networks/{id}. Deletes a network and its password.</summary>
    [Function("DeleteWifiNetwork")]
    public async Task<HttpResponseData> DeleteWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "wifi-networks/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var networkId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var coreResponse = await _coreClient.DeleteWifiNetworkAsync(networkId, context.CancellationToken);
        LogChanged(_logger, "deleted", (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    /// <summary>PUT locations/{id}/wifi-networks. Sets which networks a location receives.</summary>
    [Function("SetLocationWifiNetworks")]
    public async Task<HttpResponseData> SetLocationWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "locations/{id}/wifi-networks")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var payload = await ReadPayloadAsync(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var locationId) || payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        return await ProxyResponseAsync(req, await _coreClient.SetLocationWifiNetworksAsync(locationId, payload, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET wifi-networks/managed?locationId=. A location's networks with passwords. MediaBuilderAccess only.</summary>
    [Function("GetManagedWifiNetworksForMediaBuilder")]
    public async Task<HttpResponseData> GetManagedWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "wifi-networks/managed")] HttpRequestData req,
        FunctionContext context)
    {
        Guid? locationId = null;
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["locationId"];
        if (!string.IsNullOrEmpty(raw))
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }
            locationId = parsed;
        }

        var coreResponse = await _coreClient.GetManagedWifiNetworksForMediaBuilderAsync(locationId, context.CancellationToken);
        LogManagedRead(_logger, locationId, (int)coreResponse.StatusCode);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<object?> ReadPayloadAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        }
        catch (JsonException) { return null; }
    }

    private static async Task<HttpResponseData> ProxyResponseAsync(HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        using (coreResponse)
        {
            var response = req.CreateResponse((HttpStatusCode)(int)coreResponse.StatusCode);
            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(content))
            {
                // Core reports validation failures as plain text; keep them for the portal.
                var isJson = coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                response.Headers.Add("Content-Type", isJson ? "application/json" : "text/plain");
                await response.WriteStringAsync(content, ct);
            }
            return response;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi network {Action}; Imaging Core returned HTTP {StatusCode}.")]
    private static partial void LogChanged(ILogger logger, string action, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media Builder read the managed Wi-Fi networks for location {LocationId}; Imaging Core returned HTTP {StatusCode}.")]
    private static partial void LogManagedRead(ILogger logger, Guid? locationId, int statusCode);
}

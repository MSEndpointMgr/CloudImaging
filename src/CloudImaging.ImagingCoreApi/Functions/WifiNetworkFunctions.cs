using System.Net;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Saved Wi-Fi networks that devices on a Windows RE boot image join automatically.
///
/// GET    /api/internal/wifi-networks: lists networks, without passwords
/// POST   /api/internal/wifi-networks: creates a network; the password goes to Key Vault
/// PUT    /api/internal/wifi-networks/order: sets the order devices try networks in
/// PUT    /api/internal/wifi-networks/{id}: updates a network; omit the password to keep it
/// DELETE /api/internal/wifi-networks/{id}: deletes a network and its password
/// PUT    /api/internal/locations/{id}/wifi-networks: sets which networks a location receives
/// GET    /api/internal/wifi-networks/managed?locationId=&amp;purpose=: a location's networks with
///        passwords, for Media Builder and the Device Gateway only; never routed to the portal
/// </summary>
public sealed class WifiNetworkFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ManagedPurposes = new(StringComparer.Ordinal) { "media-builder", "device-gateway" };

    private readonly WifiNetworkCatalog _catalog;

    /// <param name="catalog">Wi-Fi network rules and storage.</param>
    public WifiNetworkFunctions(WifiNetworkCatalog catalog) => _catalog = catalog;

    /// <summary>GET internal/wifi-networks. Lists networks in order, without passwords.</summary>
    [Function("GetWifiNetworks")]
    public async Task<HttpResponseData> GetWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/wifi-networks")] HttpRequestData req,
        FunctionContext context) =>
        await JsonAsync(req, HttpStatusCode.OK, await _catalog.ListAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>POST internal/wifi-networks. Creates a network and stores its password.</summary>
    [Function("CreateWifiNetwork")]
    public async Task<HttpResponseData> CreateWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/wifi-networks")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var input = await ReadAsync<WifiNetworkInput>(req, ct);
        if (input is null)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, "The request body is not a valid Wi-Fi network.", ct);
        }
        return await ResultAsync(req, await _catalog.CreateAsync(input, ct), HttpStatusCode.Created, ct);
    }

    /// <summary>PUT internal/wifi-networks/{id}. Updates a network; an omitted password keeps the stored one.</summary>
    [Function("UpdateWifiNetwork")]
    public async Task<HttpResponseData> UpdateWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/wifi-networks/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var networkId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var input = await ReadAsync<WifiNetworkInput>(req, ct);
        if (input is null)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, "The request body is not a valid Wi-Fi network.", ct);
        }
        return await ResultAsync(req, await _catalog.UpdateAsync(networkId, input, ct), HttpStatusCode.OK, ct);
    }

    /// <summary>DELETE internal/wifi-networks/{id}. Deletes a network and its password.</summary>
    [Function("DeleteWifiNetwork")]
    public async Task<HttpResponseData> DeleteWifiNetwork(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/wifi-networks/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var networkId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        await _catalog.DeleteAsync(networkId, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>PUT internal/wifi-networks/order. Body <c>{ "networkIds": [...] }</c> listing every network once.</summary>
    [Function("SetWifiNetworkOrder")]
    public async Task<HttpResponseData> SetWifiNetworkOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/wifi-networks/order")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var input = await ReadAsync<NetworkIdsInput>(req, ct);
        var error = await _catalog.SetOrderAsync(input?.NetworkIds, ct);
        return error is null ? req.CreateResponse(HttpStatusCode.NoContent) : await TextAsync(req, HttpStatusCode.BadRequest, error, ct);
    }

    /// <summary>PUT internal/locations/{id}/wifi-networks. Body <c>{ "networkIds": [...] }</c>: the networks the location receives.</summary>
    [Function("SetLocationWifiNetworks")]
    public async Task<HttpResponseData> SetLocationWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/locations/{id}/wifi-networks")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var locationId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var input = await ReadAsync<NetworkIdsInput>(req, ct);
        var result = await _catalog.SetLocationNetworksAsync(locationId, input?.NetworkIds, ct);
        return result.NotFound || result.Error is not null
            ? await ResultAsync(req, result, HttpStatusCode.NoContent, ct)
            : req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>GET internal/wifi-networks/managed?locationId=&amp;purpose=. A location's networks with passwords, in order.</summary>
    [Function("GetManagedWifiNetworks")]
    public async Task<HttpResponseData> GetManagedWifiNetworks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/wifi-networks/managed")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var purpose = query["purpose"];
        if (purpose is null || !ManagedPurposes.Contains(purpose))
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, "purpose must be media-builder or device-gateway.", ct);
        }

        Guid? locationId = null;
        if (!string.IsNullOrEmpty(query["locationId"]))
        {
            if (!Guid.TryParse(query["locationId"], out var parsed))
            {
                return await TextAsync(req, HttpStatusCode.BadRequest, "locationId is not a valid id.", ct);
            }
            locationId = parsed;
        }

        return await JsonAsync(req, HttpStatusCode.OK, await _catalog.BuildManagedSetAsync(locationId, purpose, ct), ct);
    }

    private static async Task<HttpResponseData> ResultAsync(HttpRequestData req, WifiNetworkResult result, HttpStatusCode success, CancellationToken ct)
    {
        if (result.NotFound)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }
        if (result.Error is not null)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, result.Error, ct);
        }
        return await JsonAsync(req, success, result.Network!, ct);
    }

    private static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException) { return null; }
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData req, HttpStatusCode status, object body, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
        return response;
    }

    private static async Task<HttpResponseData> TextAsync(HttpRequestData req, HttpStatusCode status, string message, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "text/plain");
        await response.WriteStringAsync(message, ct);
        return response;
    }
}

/// <summary>A list of network ids, for ordering and per-location selection.</summary>
internal sealed class NetworkIdsInput
{
    public IReadOnlyList<Guid>? NetworkIds { get; init; }
}

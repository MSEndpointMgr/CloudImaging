using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal location-catalog CRUD endpoints consumed by the Operator API (Location Labels
/// feature). Simple admin-managed labels — no rotation/entry-count cap, unlike BootImages.
///
/// GET    /api/internal/locations: lists all locations
/// POST   /api/internal/locations: creates a new location
/// PUT    /api/internal/locations/{id}: updates a location's name and country
/// DELETE /api/internal/locations/{id}: deletes a location and removes it from Wi-Fi networks
///
/// A location's region comes from its country (<see cref="RegionConfiguration"/>). Saving a location
/// clears any region typed before that, so responses carry the resolved <c>effectiveRegion</c>.
/// </summary>
public sealed partial class LocationFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocationRepository _repo;
    private readonly RegionConfigurationRepository _regions;
    private readonly WifiNetworkRepository _wifiNetworks;
    private readonly SetupProfileService _setupProfiles;
    private readonly ILogger<LocationFunctions> _logger;

    /// <param name="repo">Location repository.</param>
    /// <param name="regions">Region configuration, used to resolve each location's region.</param>
    /// <param name="wifiNetworks">Wi-Fi networks, cleaned up when a location is deleted.</param>
    /// <param name="setupProfiles">Setup profiles, which lose a deleted location from their targeting.</param>
    /// <param name="logger">Logger for this function group.</param>
    public LocationFunctions(LocationRepository repo, RegionConfigurationRepository regions, WifiNetworkRepository wifiNetworks, SetupProfileService setupProfiles, ILogger<LocationFunctions> logger)
    {
        _repo = repo;
        _regions = regions;
        _wifiNetworks = wifiNetworks;
        _setupProfiles = setupProfiles;
        _logger = logger;
    }

    // ── GET /api/internal/locations ────────────────────────────────────────────

    /// <summary>GET internal/locations. Lists all locations.</summary>
    [Function("GetLocations")]
    public async Task<HttpResponseData> GetLocations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/locations")] HttpRequestData req,
        FunctionContext context)
    {
        var locations = new List<Location>();
        await foreach (var location in _repo.ListAllAsync(context.CancellationToken))
        {
            locations.Add(location);
        }
        locations.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var regions = await _regions.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(locations.Select(l => LocationView.From(l, regions)), JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/locations ───────────────────────────────────────────

    /// <summary>POST internal/locations. Creates a new location.</summary>
    [Function("CreateLocation")]
    public async Task<HttpResponseData> CreateLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/locations")] HttpRequestData req,
        FunctionContext context)
    {
        // Deserialize into a plain DTO rather than the `Location` entity directly: `Location.LocationId`
        // is a `required` member, and callers (the portal) only ever send `{ "name": "..." }` — System.Text.Json
        // throws JsonException for a missing required property, which was surfacing as an unconditional
        // 400 Bad Request for every create attempt.
        CreateLocationRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateLocationRequest>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Name is required.", context.CancellationToken);
            return bad;
        }

        var attributeError = ValidateAttributes(payload);
        if (attributeError is not null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(attributeError, context.CancellationToken);
            return bad;
        }

        var toCreate = new Location
        {
            LocationId = payload.LocationId is null || payload.LocationId == Guid.Empty ? Guid.NewGuid() : payload.LocationId.Value,
            Name = payload.Name.Trim(),
            CountryCode = NormalizeCode(payload.CountryCode),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var created = await _repo.CreateAsync(toCreate, context.CancellationToken);
        LogCreated(_logger, created.LocationId, created.Name);

        var regions = await _regions.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(LocationView.From(created, regions), JsonOptions), context.CancellationToken);
        return response;
    }

    // ── PUT /api/internal/locations/{id} ───────────────────────────────────────

    /// <summary>PUT internal/locations/{id}. Updates a location's name and country, clearing any typed region.</summary>
    [Function("UpdateLocation")]
    public async Task<HttpResponseData> UpdateLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/locations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var locationId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        CreateLocationRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateLocationRequest>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        var error = payload is null || string.IsNullOrWhiteSpace(payload.Name) ? "Name is required." : ValidateAttributes(payload);
        if (error is not null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(error, context.CancellationToken);
            return bad;
        }

        var existing = await _repo.GetByIdAsync(locationId, context.CancellationToken);
        if (existing is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var updated = new Location
        {
            LocationId = locationId,
            Name = payload!.Name!.Trim(),
            CountryCode = NormalizeCode(payload.CountryCode),
            CreatedAt = existing.CreatedAt,
        };
        if (!await _repo.UpdateAsync(updated, context.CancellationToken))
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }
        LogUpdated(_logger, locationId);

        var regions = await _regions.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(LocationView.From(updated, regions), JsonOptions), context.CancellationToken);
        return response;
    }

    // ── DELETE /api/internal/locations/{id} ────────────────────────────────────

    /// <summary>DELETE internal/locations/{id}. Deletes a location.</summary>
    [Function("DeleteLocation")]
    public async Task<HttpResponseData> DeleteLocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/locations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var locationId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var ct = context.CancellationToken;
        var refused = await _setupProfiles.CheckLocationDeletionAsync(locationId, ct);
        if (refused is not null)
        {
            return await SetupProfileFunctions.ProblemAsync(req, new SetupProfileResult(SetupProfileOutcome.Conflict, Detail: refused), ct);
        }

        var existing = await _repo.GetByIdAsync(locationId, ct);
        await _repo.DeleteAsync(locationId, ct);
        var networks = await _wifiNetworks.RemoveLocationAsync(locationId, ct);
        var changedBy = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["changedBy"];
        await _setupProfiles.RemoveDeletedLocationAsync(locationId, existing?.Name ?? "A location", changedBy, ct);
        LogDeleted(_logger, locationId, networks.Count);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    internal static string? ValidateAttributes(CreateLocationRequest payload)
    {
        var country = NormalizeCode(payload.CountryCode);
        return country is not null && !CountryRegionDefaults.IsKnownCountry(country)
            ? "Country code must be a two-letter ISO-3166 code."
            : null;
    }

    private static string? NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} ({Name}) created.")]
    private static partial void LogCreated(ILogger logger, Guid locationId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} updated.")]
    private static partial void LogUpdated(ILogger logger, Guid locationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} deleted and removed from {NetworkCount} Wi-Fi networks.")]
    private static partial void LogDeleted(ILogger logger, Guid locationId, int networkCount);
}

/// <summary>Request body accepted by <see cref="LocationFunctions.CreateLocation"/>: <c>{ "name": "..." }</c>.
/// <see cref="LocationId"/> is optional and normally omitted — the server assigns a new one.</summary>
internal sealed class CreateLocationRequest
{
    public Guid? LocationId { get; init; }
    public string? Name { get; init; }
    public string? CountryCode { get; init; }
}

/// <summary>A location as returned to callers: the stored fields plus the region it resolves to.</summary>
internal sealed record LocationView(Guid LocationId, string Name, string? Region, string? CountryCode, DateTimeOffset CreatedAt, string? EffectiveRegion, bool RegionIsLegacy)
{
    public static LocationView From(Location location, RegionConfiguration regions) => new(
        location.LocationId,
        location.Name,
        location.Region,
        location.CountryCode,
        location.CreatedAt,
        regions.EffectiveRegion(location),
        !string.IsNullOrWhiteSpace(location.Region));
}

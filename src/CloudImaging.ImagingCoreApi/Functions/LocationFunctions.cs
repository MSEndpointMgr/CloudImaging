using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal location-catalog CRUD endpoints consumed by the Operator API (Location Labels
/// feature). Simple admin-managed labels — no rotation/entry-count cap, unlike BootImages.
///
/// GET    /api/internal/locations      — lists all locations
/// POST   /api/internal/locations      — creates a new location
/// DELETE /api/internal/locations/{id} — deletes a location
/// </summary>
public sealed partial class LocationFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocationRepository _repo;
    private readonly ILogger<LocationFunctions> _logger;

    public LocationFunctions(LocationRepository repo, ILogger<LocationFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ── GET /api/internal/locations ────────────────────────────────────────────

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

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(locations, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/locations ───────────────────────────────────────────

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

        var toCreate = new Location
        {
            LocationId = payload.LocationId is null || payload.LocationId == Guid.Empty ? Guid.NewGuid() : payload.LocationId.Value,
            Name = payload.Name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var created = await _repo.CreateAsync(toCreate, context.CancellationToken);
        LogCreated(_logger, created.LocationId, created.Name);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(created, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── DELETE /api/internal/locations/{id} ────────────────────────────────────

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

        await _repo.DeleteAsync(locationId, context.CancellationToken);
        LogDeleted(_logger, locationId);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} ({Name}) created.")]
    private static partial void LogCreated(ILogger logger, Guid locationId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} deleted.")]
    private static partial void LogDeleted(ILogger logger, Guid locationId);
}

/// <summary>Request body accepted by <see cref="LocationFunctions.CreateLocation"/>: <c>{ "name": "..." }</c>.
/// <see cref="LocationId"/> is optional and normally omitted — the server assigns a new one.</summary>
internal sealed class CreateLocationRequest
{
    public Guid? LocationId { get; init; }
    public string? Name { get; init; }
}

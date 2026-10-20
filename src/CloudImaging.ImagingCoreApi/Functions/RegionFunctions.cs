using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal endpoints for the tenant's country-to-region mapping (Locations, Regions tab).
///
/// GET /api/internal/regions: regions with usage counts, plus every country's current and default region
/// PUT /api/internal/regions: replaces the regions and country overrides
/// </summary>
public sealed class RegionFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RegionConfigurationRepository _regions;
    private readonly LocationRepository _locations;

    /// <param name="regions">Region configuration repository.</param>
    /// <param name="locations">Location repository, used to count locations per region.</param>
    public RegionFunctions(RegionConfigurationRepository regions, LocationRepository locations)
    {
        _regions = regions;
        _locations = locations;
    }

    /// <summary>GET internal/regions. Returns the regions, their usage and every country's mapping.</summary>
    [Function("GetRegions")]
    public async Task<HttpResponseData> GetRegions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/regions")] HttpRequestData req,
        FunctionContext context)
    {
        var configuration = await _regions.GetAsync(context.CancellationToken);
        var locations = new List<Location>();
        await foreach (var location in _locations.ListAllAsync(context.CancellationToken))
        {
            locations.Add(location);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(BuildView(configuration, locations), JsonOptions), context.CancellationToken);
        return response;
    }

    /// <summary>PUT internal/regions. Validates and saves the regions and country overrides.</summary>
    [Function("PutRegions")]
    public async Task<HttpResponseData> PutRegions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/regions")] HttpRequestData req,
        FunctionContext context)
    {
        RegionConfiguration? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<RegionConfiguration>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, "Invalid JSON payload.", context);
        }

        if (payload is null)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, "A region configuration is required.", context);
        }

        var normalized = WithoutRedundantOverrides(payload.Normalized());
        var error = normalized.Validate();
        if (error is not null)
        {
            return await TextAsync(req, HttpStatusCode.BadRequest, error, context);
        }

        await _regions.UpsertAsync(normalized, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    internal static RegionsView BuildView(RegionConfiguration configuration, IReadOnlyCollection<Location> locations)
    {
        var countries = CountryRegionDefaults.CountryCodes
            .Select(code => new CountryRegionView(code, configuration.RegionIdFor(code), CountryRegionDefaults.RegionIdFor(code)))
            .ToList();
        var regions = configuration.Regions
            .Select(r => new RegionView(
                r.RegionId,
                r.Code,
                countries.Count(c => c.RegionId == r.RegionId),
                locations.Count(l => string.Equals(configuration.EffectiveRegion(l), r.Code, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        return new RegionsView(regions, countries, configuration.LastModifiedAt);
    }

    /// <summary>Drops overrides that match the built-in default, so only real exceptions are stored.</summary>
    internal static RegionConfiguration WithoutRedundantOverrides(RegionConfiguration configuration) => new()
    {
        Regions = configuration.Regions,
        CountryOverrides = configuration.CountryOverrides
            .Where(kv => !CountryRegionDefaults.IsKnownCountry(kv.Key) || kv.Value != CountryRegionDefaults.RegionIdFor(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        LastModifiedAt = configuration.LastModifiedAt,
    };

    private static async Task<HttpResponseData> TextAsync(HttpRequestData req, HttpStatusCode status, string message, FunctionContext context)
    {
        var response = req.CreateResponse(status);
        await response.WriteStringAsync(message, context.CancellationToken);
        return response;
    }
}

/// <summary>Response of GET internal/regions.</summary>
internal sealed record RegionsView(IReadOnlyList<RegionView> Regions, IReadOnlyList<CountryRegionView> Countries, DateTimeOffset LastModifiedAt);

/// <summary>A region with the number of countries mapped to it and locations resolving to it.</summary>
internal sealed record RegionView(string RegionId, string Code, int CountryCount, int LocationCount);

/// <summary>A country's current region and its built-in default (null = no region).</summary>
internal sealed record CountryRegionView(string CountryCode, string? RegionId, string? DefaultRegionId);

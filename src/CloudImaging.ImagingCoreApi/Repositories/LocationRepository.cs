using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for the admin-managed location catalog (Location Labels feature).
/// PartitionKey: "location" | RowKey: locationId. No entry-count cap or rotation — unlike
/// BootImages, there is no natural upper bound on the number of sites a customer has.
/// </summary>
public sealed class LocationRepository
{
    private const string TableName = "Locations";
    private const string Partition = "location";
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the locations table.</param>
    public LocationRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Creates a new location.</summary>
    public async Task<Location> CreateAsync(Location location, CancellationToken ct = default)
    {
        var newLocation = new Location
        {
            LocationId = location.LocationId,
            Name = location.Name,
            Region = location.Region,
            CountryCode = location.CountryCode,
            CreatedAt = location.CreatedAt == default ? DateTimeOffset.UtcNow : location.CreatedAt,
        };
        await _table.AddEntityAsync(ToEntity(newLocation), ct);
        return newLocation;
    }

    /// <summary>Replaces an existing location. Returns false when it does not exist.</summary>
    public async Task<bool> UpdateAsync(Location location, CancellationToken ct = default)
    {
        try
        {
            await _table.UpdateEntityAsync(ToEntity(location), ETag.All, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    /// <summary>Returns a single location by id, or null if not found.</summary>
    public async Task<Location?> GetByIdAsync(Guid locationId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, locationId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists all locations.</summary>
    public IAsyncEnumerable<Location> ListAllAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    /// <summary>Deletes a location. No-op if it does not exist.</summary>
    public async Task DeleteAsync(Guid locationId, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, locationId.ToString(), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — nothing left to do.
        }
    }

    private static TableEntity ToEntity(Location l) => new(Partition, l.LocationId.ToString())
    {
        ["Name"] = l.Name,
        ["Region"] = l.Region,
        ["CountryCode"] = l.CountryCode,
        ["CreatedAt"] = l.CreatedAt,
    };

    private static Location FromEntity(TableEntity e) => new()
    {
        LocationId = Guid.Parse(e.RowKey),
        Name = e.GetString("Name") ?? string.Empty,
        Region = e.GetString("Region"),
        CountryCode = e.GetString("CountryCode"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
    };
}

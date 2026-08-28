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

    public LocationRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task<Location> CreateAsync(Location location, CancellationToken ct = default)
    {
        var newLocation = new Location
        {
            LocationId = location.LocationId,
            Name = location.Name,
            CreatedAt = location.CreatedAt == default ? DateTimeOffset.UtcNow : location.CreatedAt,
        };
        await _table.AddEntityAsync(ToEntity(newLocation), ct);
        return newLocation;
    }

    public async Task<Location?> GetByIdAsync(Guid locationId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, locationId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public IAsyncEnumerable<Location> ListAllAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

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
        ["CreatedAt"] = l.CreatedAt,
    };

    private static Location FromEntity(TableEntity e) => new()
    {
        LocationId = Guid.Parse(e.RowKey),
        Name = e.GetString("Name") ?? string.Empty,
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
    };
}

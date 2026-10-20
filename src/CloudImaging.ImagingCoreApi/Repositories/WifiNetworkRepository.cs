using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for saved Wi-Fi networks. PartitionKey: "network" | RowKey: networkId.
/// Passwords are not stored here; see <see cref="Services.WifiPasswordStore"/>.
/// </summary>
public sealed class WifiNetworkRepository
{
    private const string TableName = "WifiNetworks";
    private const string Partition = "network";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the Wi-Fi networks table.</param>
    public WifiNetworkRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Lists all networks in the order devices try them.</summary>
    public async Task<List<WifiNetwork>> ListAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        var networks = new List<WifiNetwork>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            networks.Add(FromEntity(entity));
        }
        networks.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.CreatedAt.CompareTo(b.CreatedAt));
        return networks;
    }

    /// <summary>Returns a network by id, or null if not found.</summary>
    public async Task<WifiNetwork?> GetByIdAsync(Guid networkId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, networkId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Adds a new network.</summary>
    public async Task AddAsync(WifiNetwork network, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(network), ct);

    /// <summary>Replaces an existing network. Returns false when it does not exist.</summary>
    public async Task<bool> UpdateAsync(WifiNetwork network, CancellationToken ct = default)
    {
        try
        {
            await _table.UpdateEntityAsync(ToEntity(network), ETag.All, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    /// <summary>Deletes a network. No-op if it does not exist.</summary>
    public async Task DeleteAsync(Guid networkId, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, networkId.ToString(), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    /// <summary>Removes a deleted location from every network's location list. Returns the networks that changed.</summary>
    public async Task<List<WifiNetwork>> RemoveLocationAsync(Guid locationId, CancellationToken ct = default)
    {
        var changed = new List<WifiNetwork>();
        foreach (var network in await ListAsync(ct))
        {
            if (!network.LocationIds.Contains(locationId))
            {
                continue;
            }
            var updated = With(network, locationIds: [.. network.LocationIds.Where(id => id != locationId)]);
            if (await UpdateAsync(updated, ct))
            {
                changed.Add(updated);
            }
        }
        return changed;
    }

    /// <summary>Copies <paramref name="network"/>, replacing the given values.</summary>
    public static WifiNetwork With(WifiNetwork network, IReadOnlyList<Guid>? locationIds = null, int? order = null) => new()
    {
        NetworkId = network.NetworkId,
        Ssid = network.Ssid,
        Hidden = network.Hidden,
        Security = network.Security,
        AllLocations = network.AllLocations,
        LocationIds = locationIds ?? network.LocationIds,
        Order = order ?? network.Order,
        Enabled = network.Enabled,
        PasswordChangedAt = network.PasswordChangedAt,
        PasswordChangedBy = network.PasswordChangedBy,
        CreatedAt = network.CreatedAt,
    };

    private static TableEntity ToEntity(WifiNetwork n) => new(Partition, n.NetworkId.ToString())
    {
        ["Ssid"] = n.Ssid,
        ["Hidden"] = n.Hidden,
        ["Security"] = n.Security.ToString(),
        ["AllLocations"] = n.AllLocations,
        ["LocationIdsJson"] = JsonSerializer.Serialize(n.LocationIds, JsonOptions),
        ["Order"] = n.Order,
        ["Enabled"] = n.Enabled,
        ["PasswordChangedAt"] = n.PasswordChangedAt,
        ["PasswordChangedBy"] = n.PasswordChangedBy,
        ["CreatedAt"] = n.CreatedAt,
    };

    private static WifiNetwork FromEntity(TableEntity e) => new()
    {
        NetworkId = Guid.Parse(e.RowKey),
        Ssid = e.GetString("Ssid") ?? string.Empty,
        Hidden = e.GetBoolean("Hidden") ?? false,
        Security = Enum.TryParse<WifiSecurityType>(e.GetString("Security"), out var security) ? security : WifiSecurityType.Wpa2Personal,
        AllLocations = e.GetBoolean("AllLocations") ?? false,
        LocationIds = ParseLocationIds(e.GetString("LocationIdsJson")),
        Order = e.GetInt32("Order") ?? 0,
        Enabled = e.GetBoolean("Enabled") ?? true,
        PasswordChangedAt = e.GetDateTimeOffset("PasswordChangedAt") ?? DateTimeOffset.MinValue,
        PasswordChangedBy = e.GetString("PasswordChangedBy"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
    };

    private static List<Guid> ParseLocationIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
    }
}

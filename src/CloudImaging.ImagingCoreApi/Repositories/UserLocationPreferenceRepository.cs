using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for per-portal-user location preferences (Location Labels feature).
/// PartitionKey: "user-preference" | RowKey: userId (Entra object id). One row per user.
/// </summary>
public sealed class UserLocationPreferenceRepository
{
    private const string TableName = "UserLocationPreferences";
    private const string Partition = "user-preference";
    private readonly TableClient _table;

    public UserLocationPreferenceRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task<UserLocationPreference?> GetAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, userId, cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<UserLocationPreference> UpsertAsync(UserLocationPreference preference, CancellationToken ct = default)
    {
        var updated = new UserLocationPreference
        {
            UserId = preference.UserId,
            LocationId = preference.LocationId,
            LocationName = preference.LocationName,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _table.UpsertEntityAsync(ToEntity(updated), TableUpdateMode.Replace, ct);
        return updated;
    }

    private static TableEntity ToEntity(UserLocationPreference p) => new(Partition, p.UserId)
    {
        ["LocationId"] = p.LocationId?.ToString(),
        ["LocationName"] = p.LocationName,
        ["UpdatedAt"] = p.UpdatedAt,
    };

    private static UserLocationPreference FromEntity(TableEntity e) => new()
    {
        UserId = e.RowKey,
        LocationId = e.GetString("LocationId") is string lid ? Guid.Parse(lid) : null,
        LocationName = e.GetString("LocationName"),
        UpdatedAt = e.GetDateTimeOffset("UpdatedAt") ?? DateTimeOffset.UtcNow,
    };
}

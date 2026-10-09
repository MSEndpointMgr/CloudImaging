using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for admin-defined Autopilot group tags.
/// PartitionKey: "tag" | RowKey: definition id.
/// </summary>
public sealed class AutopilotGroupTagRepository
{
    private const string TableName = "AutopilotGroupTags";
    private const string Partition = "tag";
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the Autopilot group tags table.</param>
    public AutopilotGroupTagRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Creates a new group tag definition.</summary>
    public async Task AddAsync(AutopilotGroupTagDefinition definition, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(definition), ct);

    /// <summary>Updates an existing group tag definition. Returns false if it no longer exists.</summary>
    public async Task<bool> UpdateAsync(AutopilotGroupTagDefinition definition, CancellationToken ct = default)
    {
        try
        {
            await _table.UpdateEntityAsync(ToEntity(definition), ETag.All, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    /// <summary>Returns a single group tag definition by id, or null if not found.</summary>
    public async Task<AutopilotGroupTagDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, id.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists all group tag definitions, sorted by name.</summary>
    public async Task<List<AutopilotGroupTagDefinition>> ListAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        var definitions = new List<AutopilotGroupTagDefinition>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            definitions.Add(FromEntity(entity));
        }
        definitions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return definitions;
    }

    /// <summary>Deletes a group tag definition. No-op if it does not exist.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, id.ToString(), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone.
        }
    }

    private static TableEntity ToEntity(AutopilotGroupTagDefinition d) => new(Partition, d.Id.ToString())
    {
        ["Name"] = d.Name,
        ["Kind"] = d.Kind.ToString(),
        ["Value"] = d.Value,
        ["Description"] = d.Description,
        ["CreatedAt"] = d.CreatedAt,
    };

    private static AutopilotGroupTagDefinition FromEntity(TableEntity e) => new()
    {
        Id = Guid.Parse(e.RowKey),
        Name = e.GetString("Name") ?? string.Empty,
        Kind = Enum.TryParse<AutopilotGroupTagKind>(e.GetString("Kind"), out var kind) ? kind : AutopilotGroupTagKind.Static,
        Value = e.GetString("Value") ?? string.Empty,
        Description = e.GetString("Description"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
    };
}

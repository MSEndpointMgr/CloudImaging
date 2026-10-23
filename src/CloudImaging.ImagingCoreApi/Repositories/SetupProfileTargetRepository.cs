using Azure;
using Azure.Data.Tables;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>Result of a targeting change: success, or the profile that already owns a requested target.</summary>
/// <param name="ConflictTarget">Target key that is already owned, or null on success.</param>
/// <param name="ConflictOwner">Profile that owns <paramref name="ConflictTarget"/>.</param>
public sealed record SetupProfileTargetChangeResult(string? ConflictTarget, Guid? ConflictOwner)
{
    /// <summary>A successful change.</summary>
    public static SetupProfileTargetChangeResult Success { get; } = new(null, null);

    /// <summary>True when the change was applied.</summary>
    public bool Succeeded => ConflictTarget is null;
}

/// <summary>
/// Targeting index for setup profiles. PartitionKey: "target" | RowKey: location id ("D" format)
/// or "all-devices" | field ProfileId. One row per target is what makes "a location belongs to at
/// most one profile" atomic: two saves claiming the same location race on an insert, and exactly
/// one wins.
/// </summary>
public sealed class SetupProfileTargetRepository
{
    /// <summary>Row key of the All devices target.</summary>
    public const string AllDevicesKey = "all-devices";

    private const string TableName = "SetupProfileTargets";
    private const string Partition = "target";
    private const int MaxActionsPerTransaction = 100;
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the targeting table.</param>
    public SetupProfileTargetRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Row key for a location.</summary>
    public static string LocationKey(Guid locationId) => locationId.ToString("D");

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Returns the profile that owns a target key, or null.</summary>
    public async Task<Guid?> GetOwnerAsync(string targetKey, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, targetKey, cancellationToken: ct);
            return ReadOwner(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Returns every target key with its owning profile.</summary>
    public async Task<Dictionary<string, Guid>> ListAllAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        var owners = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            if (ReadOwner(entity) is { } owner)
            {
                owners[entity.RowKey] = owner;
            }
        }
        return owners;
    }

    /// <summary>Returns the target keys a profile owns.</summary>
    public async Task<List<string>> ListByProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var owner = profileId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and ProfileId eq {owner}");
        var keys = new List<string>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            keys.Add(entity.RowKey);
        }
        return keys;
    }

    /// <summary>
    /// Adds and removes a profile's targets as one entity group transaction (chunked above 100
    /// actions, with earlier chunks undone when a later one fails). An add on a target another
    /// profile owns changes nothing and returns that owner.
    /// </summary>
    public async Task<SetupProfileTargetChangeResult> ApplyChangesAsync(Guid profileId, IReadOnlyCollection<string> adds, IReadOnlyCollection<string> deletes, CancellationToken ct = default)
    {
        if (adds.Count == 0 && deletes.Count == 0)
        {
            return SetupProfileTargetChangeResult.Success;
        }

        // Most conflicts are visible up front; the insert itself still guards against a race.
        foreach (var key in adds)
        {
            if (await GetOwnerAsync(key, ct) is { } owner && owner != profileId)
            {
                return new SetupProfileTargetChangeResult(key, owner);
            }
        }

        var actions = deletes
            .Select(key => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(Partition, key), ETag.All))
            .Concat(adds.Select(key => new TableTransactionAction(TableTransactionActionType.Add, NewRow(key, profileId))))
            .ToList();

        var applied = new List<TableTransactionAction>();
        foreach (var chunk in actions.Chunk(MaxActionsPerTransaction))
        {
            try
            {
                await _table.SubmitTransactionAsync(chunk, ct);
                applied.AddRange(chunk);
            }
            catch (TableTransactionFailedException)
            {
                await UndoAsync(applied, profileId, ct);
                foreach (var key in adds)
                {
                    if (await GetOwnerAsync(key, ct) is { } owner && owner != profileId)
                    {
                        return new SetupProfileTargetChangeResult(key, owner);
                    }
                }
                throw;
            }
        }
        return SetupProfileTargetChangeResult.Success;
    }

    /// <summary>Reverses a change previously applied with <see cref="ApplyChangesAsync"/>.</summary>
    public Task<SetupProfileTargetChangeResult> RevertChangesAsync(Guid profileId, IReadOnlyCollection<string> adds, IReadOnlyCollection<string> deletes, CancellationToken ct = default) =>
        ApplyChangesAsync(profileId, deletes, adds, ct);

    /// <summary>Deletes one target row. No-op when it does not exist.</summary>
    public async Task DeleteAsync(string targetKey, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, targetKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private async Task UndoAsync(List<TableTransactionAction> applied, Guid profileId, CancellationToken ct)
    {
        foreach (var action in applied)
        {
            if (action.ActionType == TableTransactionActionType.Add)
            {
                await DeleteAsync(action.Entity.RowKey, ct);
            }
            else
            {
                await _table.UpsertEntityAsync(NewRow(action.Entity.RowKey, profileId), TableUpdateMode.Replace, ct);
            }
        }
    }

    private static TableEntity NewRow(string key, Guid profileId) => new(Partition, key)
    {
        ["ProfileId"] = profileId.ToString(),
    };

    private static Guid? ReadOwner(TableEntity entity) =>
        Guid.TryParse(entity.GetString("ProfileId"), out var owner) ? owner : null;
}

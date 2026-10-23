using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>A stored setup profile row. Targeting lives in <see cref="SetupProfileTargetRepository"/>.</summary>
public sealed record SetupProfileRecord
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Current version number.</summary>
    public int CurrentVersion { get; init; }

    /// <summary>Failure handling of the current version.</summary>
    public SetupFailureHandling FailureHandling { get; init; }

    /// <summary>Registry values in the current version.</summary>
    public int RegistryValueCount { get; init; }

    /// <summary>Scripts in the current version.</summary>
    public int ScriptCount { get; init; }

    /// <summary>True when the current version has an answer file.</summary>
    public bool HasAnswerFile { get; init; }

    /// <summary>When the profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who created the profile.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Last change of content, name or targeting.</summary>
    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>Who made the last change.</summary>
    public string? LastModifiedBy { get; init; }

    /// <summary>Set when the profile was deleted.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Who deleted the profile.</summary>
    public string? DeletedBy { get; init; }

    /// <summary>Row ETag for optimistic concurrency.</summary>
    public ETag ETag { get; init; }

    /// <summary>True when the profile was deleted.</summary>
    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>
/// Table Storage repository for setup profiles. PartitionKey: "profile" | RowKey: profileId.
/// Deleted profiles stay as tombstones so pinned sessions and history can still name them.
/// </summary>
public sealed class SetupProfileRepository
{
    private const string TableName = "SetupProfiles";
    private const string Partition = "profile";
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the setup profiles table.</param>
    public SetupProfileRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Returns a profile by id, including tombstones, or null.</summary>
    public async Task<SetupProfileRecord?> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, profileId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists profiles that are not deleted, by name.</summary>
    public async Task<List<SetupProfileRecord>> ListActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        var profiles = new List<SetupProfileRecord>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            var record = FromEntity(entity);
            if (!record.IsDeleted)
            {
                profiles.Add(record);
            }
        }
        profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return profiles;
    }

    /// <summary>Lists deleted profiles, for the version sweep.</summary>
    public async Task<List<SetupProfileRecord>> ListDeletedAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        var profiles = new List<SetupProfileRecord>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            var record = FromEntity(entity);
            if (record.IsDeleted)
            {
                profiles.Add(record);
            }
        }
        return profiles;
    }

    /// <summary>Returns the non-deleted profile with this name, ignoring case, or null.</summary>
    public async Task<SetupProfileRecord?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var key = name.Trim().ToUpperInvariant();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and NameKey eq {key}");
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            var record = FromEntity(entity);
            if (!record.IsDeleted)
            {
                return record;
            }
        }
        return null;
    }

    /// <summary>Adds a new profile row.</summary>
    public async Task AddAsync(SetupProfileRecord record, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(record), ct);

    /// <summary>Replaces a profile row if its ETag still matches. Returns false on a concurrent change or when it is gone.</summary>
    public async Task<bool> ReplaceAsync(SetupProfileRecord record, CancellationToken ct = default)
    {
        try
        {
            await _table.UpdateEntityAsync(ToEntity(record), record.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { return false; }
    }

    /// <summary>Deletes a profile row. Only used to undo a create whose later steps failed.</summary>
    public async Task DeleteAsync(Guid profileId, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, profileId.ToString(), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private static TableEntity ToEntity(SetupProfileRecord r) => new(Partition, r.ProfileId.ToString())
    {
        ["Name"] = r.Name,
        ["NameKey"] = r.Name.Trim().ToUpperInvariant(),
        ["CurrentVersion"] = r.CurrentVersion,
        ["FailureHandling"] = r.FailureHandling.ToString(),
        ["RegistryValueCount"] = r.RegistryValueCount,
        ["ScriptCount"] = r.ScriptCount,
        ["HasAnswerFile"] = r.HasAnswerFile,
        ["CreatedAt"] = r.CreatedAt,
        ["CreatedBy"] = r.CreatedBy,
        ["LastModifiedAt"] = r.LastModifiedAt,
        ["LastModifiedBy"] = r.LastModifiedBy,
        ["DeletedAt"] = r.DeletedAt,
        ["DeletedBy"] = r.DeletedBy,
    };

    private static SetupProfileRecord FromEntity(TableEntity e) => new()
    {
        ProfileId = Guid.Parse(e.RowKey),
        Name = e.GetString("Name") ?? string.Empty,
        CurrentVersion = e.GetInt32("CurrentVersion") ?? 0,
        FailureHandling = Enum.TryParse<SetupFailureHandling>(e.GetString("FailureHandling"), out var handling) ? handling : SetupFailureHandling.CompleteWithWarnings,
        RegistryValueCount = e.GetInt32("RegistryValueCount") ?? 0,
        ScriptCount = e.GetInt32("ScriptCount") ?? 0,
        HasAnswerFile = e.GetBoolean("HasAnswerFile") ?? false,
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
        CreatedBy = e.GetString("CreatedBy"),
        LastModifiedAt = e.GetDateTimeOffset("LastModifiedAt") ?? DateTimeOffset.MinValue,
        LastModifiedBy = e.GetString("LastModifiedBy"),
        DeletedAt = e.GetDateTimeOffset("DeletedAt"),
        DeletedBy = e.GetString("DeletedBy"),
        ETag = e.ETag,
    };
}

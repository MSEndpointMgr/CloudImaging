using System.Security.Cryptography;
using System.Text;
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
/// Partition "name" holds one reservation row per name (RowKey: hash of the upper-cased name), so
/// two profiles can never claim the same name at once.
/// </summary>
public sealed class SetupProfileRepository
{
    private const string TableName = "SetupProfiles";
    private const string Partition = "profile";
    private const string NamePartition = "name";
    private static readonly TimeSpan AbandonedReservationAge = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// Claims <paramref name="name"/> (ignoring case) for a profile. Returns false when another
    /// profile holds it. A claim whose holder was deleted, renamed or never stored is taken over.
    /// </summary>
    public async Task<bool> TryReserveNameAsync(string name, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        var entity = new TableEntity(NamePartition, NameRowKey(name)) { ["ProfileId"] = profileId.ToString(), ["ReservedAt"] = now };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _table.AddEntityAsync(entity, ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409) { }

            TableEntity existing;
            try
            {
                existing = (await _table.GetEntityAsync<TableEntity>(NamePartition, entity.RowKey, cancellationToken: ct)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { continue; }

            var holderId = Guid.TryParse(existing.GetString("ProfileId"), out var id) ? id : Guid.Empty;
            if (holderId == profileId)
            {
                return true;
            }
            var holder = holderId == Guid.Empty ? null : await GetAsync(holderId, ct);
            var abandoned = holder is null && now - (existing.GetDateTimeOffset("ReservedAt") ?? DateTimeOffset.MinValue) > AbandonedReservationAge;
            var stale = holder is not null && (holder.IsDeleted || !string.Equals(holder.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!abandoned && !stale)
            {
                return false;
            }
            try
            {
                await _table.UpdateEntityAsync(entity, existing.ETag, TableUpdateMode.Replace, ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412) { }
        }
        return false;
    }

    /// <summary>Releases a name claim if <paramref name="profileId"/> holds it. Best effort.</summary>
    public async Task ReleaseNameAsync(string name, Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var existing = (await _table.GetEntityAsync<TableEntity>(NamePartition, NameRowKey(name), cancellationToken: ct)).Value;
            if (existing.GetString("ProfileId") == profileId.ToString())
            {
                await _table.DeleteEntityAsync(NamePartition, existing.RowKey, existing.ETag, ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { }
    }

    private static string NameRowKey(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToUpperInvariant())));

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

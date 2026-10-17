using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for the recovery (WinRE) image catalog.
/// PartitionKey: "catalog" | RowKey: recoveryImageId.
/// Mirrors <see cref="BootImageRepository"/>: max 5 active entries and one "latest published"
/// per architecture, so an ARM64 publish never displaces the x64 WinRE.
/// </summary>
public sealed class RecoveryImageRepository
{
    private const string TableName = "RecoveryImages";
    private const string Partition = "catalog";
    /// <summary>Maximum number of active recovery image catalog entries allowed per architecture.</summary>
    public const int MaxActiveEntriesPerArchitecture = 5;
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the recovery images table.</param>
    public RecoveryImageRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Entries to demote and to clear latest on when publishing a new image of <paramref name="architecture"/>.</summary>
    public static (IReadOnlyList<RecoveryImage> ToDemote, IReadOnlyList<RecoveryImage> ToClearLatest) PlanPublish(
        IEnumerable<RecoveryImage> activeEntries, MachineArchitecture architecture)
    {
        var sameArchitecture = activeEntries.Where(i => i.Architecture == architecture).ToList();
        var toDemote = sameArchitecture.Count >= MaxActiveEntriesPerArchitecture
            ? sameArchitecture.OrderBy(i => i.UploadedAt).Take(sameArchitecture.Count - MaxActiveEntriesPerArchitecture + 1).ToList()
            : [];
        return (toDemote, sameArchitecture.Where(i => i.IsLatestPublished).ToList());
    }

    /// <summary>Publishes a new recovery image, demoting the oldest entries of the same architecture past the cap and clearing the previous latest-published flag.</summary>
    public async Task<RecoveryImage> PublishAsync(RecoveryImage image, CancellationToken ct = default)
    {
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);
        var (toDemote, toClearLatest) = PlanPublish(activeEntries, image.Architecture);

        foreach (var oldest in toDemote)
        {
            var demoted = new TableEntity(Partition, oldest.RecoveryImageId.ToString());
            demoted["IsActive"] = false;
            await _table.UpdateEntityAsync(demoted, ETag.All, TableUpdateMode.Merge, ct);
        }

        foreach (var existing in toClearLatest)
        {
            var patch = new TableEntity(Partition, existing.RecoveryImageId.ToString());
            patch["IsLatestPublished"] = false;
            await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
        }

        var newImage = new RecoveryImage
        {
            RecoveryImageId = image.RecoveryImageId,
            Version = image.Version,
            Description = image.Description,
            SizeBytes = image.SizeBytes,
            StoragePath = image.StoragePath,
            UploadedAt = image.UploadedAt == default ? DateTimeOffset.UtcNow : image.UploadedAt,
            Sha256Hash = image.Sha256Hash,
            Architecture = image.Architecture,
            IsLatestPublished = true,
            IsActive = true,
        };
        await _table.AddEntityAsync(ToEntity(newImage), ct);
        return newImage;
    }

    /// <summary>Returns a single recovery image by id, or null if not found.</summary>
    public async Task<RecoveryImage?> GetByIdAsync(Guid recoveryImageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, recoveryImageId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists active recovery image catalog entries.</summary>
    public IAsyncEnumerable<RecoveryImage> ListActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and IsActive eq true");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    /// <summary>Deletes a recovery image catalog entry.</summary>
    public async Task DeleteAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        await _table.DeleteEntityAsync(Partition, recoveryImageId.ToString(), cancellationToken: ct);

    private static TableEntity ToEntity(RecoveryImage i) => new(Partition, i.RecoveryImageId.ToString())
    {
        ["Version"] = i.Version,
        ["Description"] = i.Description,
        ["SizeBytes"] = i.SizeBytes,
        ["StoragePath"] = i.StoragePath,
        ["Sha256Hash"] = i.Sha256Hash,
        ["Architecture"] = MachineArchitecturePlatform.Slug(i.Architecture),
        ["IsLatestPublished"] = i.IsLatestPublished,
        ["IsActive"] = i.IsActive,
        ["UploadedAt"] = i.UploadedAt,
    };

    private static RecoveryImage FromEntity(TableEntity e) => new()
    {
        RecoveryImageId = Guid.Parse(e.RowKey),
        Version = e.GetString("Version") ?? string.Empty,
        Description = e.GetString("Description"),
        SizeBytes = e.GetInt64("SizeBytes") ?? 0L,
        StoragePath = e.GetString("StoragePath") ?? string.Empty,
        Sha256Hash = e.GetString("Sha256Hash") ?? string.Empty,
        Architecture = MachineArchitecturePlatform.ParseSlugOrDefault(e.GetString("Architecture")),
        IsLatestPublished = e.GetBoolean("IsLatestPublished") ?? false,
        IsActive = e.GetBoolean("IsActive") ?? false,
        UploadedAt = e.GetDateTimeOffset("UploadedAt") ?? DateTimeOffset.UtcNow,
    };
}

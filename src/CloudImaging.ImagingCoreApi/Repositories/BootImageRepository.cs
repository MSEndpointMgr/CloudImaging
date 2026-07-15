using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for boot image catalog (FR-063).
/// PartitionKey: "catalog" | RowKey: bootImageId.
/// Enforces maximum of 5 active entries (FR-063) — oldest is auto-demoted on 6th publish.
/// </summary>
public sealed class BootImageRepository
{
    private const string TableName = "BootImages";
    private const string Partition = "catalog";
    private const int MaxActiveEntries = 5;
    private readonly TableClient _table;

    public BootImageRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task<BootImage> PublishAsync(BootImage image, CancellationToken ct = default)
    {
        // Demote oldest active entry if catalog is full (FR-063)
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);
        if (activeEntries.Count >= MaxActiveEntries)
        {
            var oldest = activeEntries.OrderBy(b => b.CreatedAt).First();
            var demoted = new TableEntity(Partition, oldest.BootImageId.ToString());
            demoted["IsActive"] = false;
            await _table.UpdateEntityAsync(demoted, ETag.All, TableUpdateMode.Merge, ct);
        }

        // Mark all existing active entries as non-latest, then insert new one as latest
        foreach (var existing in activeEntries.Where(b => b.IsLatestPublished))
        {
            var patch = new TableEntity(Partition, existing.BootImageId.ToString());
            patch["IsLatestPublished"] = false;
            await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
        }

        var newImage = image with { IsLatestPublished = true, IsActive = true };
        await _table.AddEntityAsync(ToEntity(newImage), ct);
        return newImage;
    }

    public async Task<BootImage?> GetByIdAsync(Guid bootImageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, bootImageId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public IAsyncEnumerable<BootImage> ListActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq '{Partition}' and IsActive eq true");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    public async Task DeleteAsync(Guid bootImageId, CancellationToken ct = default) =>
        await _table.DeleteEntityAsync(Partition, bootImageId.ToString(), cancellationToken: ct);

    private static TableEntity ToEntity(BootImage b) => new(Partition, b.BootImageId.ToString())
    {
        ["Version"]           = b.Version,
        ["SizeBytes"]         = b.SizeBytes,
        ["StoragePath"]       = b.StoragePath,
        ["ManifestVersion"]   = b.ManifestVersion,
        ["Sha256Hash"]        = b.Sha256Hash,
        ["IsLatestPublished"] = b.IsLatestPublished,
        ["IsActive"]          = b.IsActive,
        ["CreatedAt"]         = b.CreatedAt,
    };

    private static BootImage FromEntity(TableEntity e) => new()
    {
        BootImageId       = Guid.Parse(e.RowKey),
        Version           = e.GetString("Version") ?? string.Empty,
        SizeBytes         = e.GetInt64("SizeBytes") ?? 0L,
        StoragePath       = e.GetString("StoragePath") ?? string.Empty,
        ManifestVersion   = e.GetString("ManifestVersion") ?? string.Empty,
        Sha256Hash        = e.GetString("Sha256Hash") ?? string.Empty,
        IsLatestPublished = e.GetBoolean("IsLatestPublished") ?? false,
        IsActive          = e.GetBoolean("IsActive") ?? false,
        CreatedAt         = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
    };
}

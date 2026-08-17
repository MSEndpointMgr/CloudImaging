using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for the recovery (WinRE) image catalog.
/// PartitionKey: "catalog" | RowKey: recoveryImageId.
/// Mirrors <see cref="BootImageRepository"/>: max 5 active entries, "latest published" auto-select.
/// </summary>
public sealed class RecoveryImageRepository
{
    private const string TableName = "RecoveryImages";
    private const string Partition = "catalog";
    private const int MaxActiveEntries = 5;
    private readonly TableClient _table;

    public RecoveryImageRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task<RecoveryImage> PublishAsync(RecoveryImage image, CancellationToken ct = default)
    {
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);
        if (activeEntries.Count >= MaxActiveEntries)
        {
            var oldest = activeEntries.OrderBy(i => i.UploadedAt).First();
            var demoted = new TableEntity(Partition, oldest.RecoveryImageId.ToString());
            demoted["IsActive"] = false;
            await _table.UpdateEntityAsync(demoted, ETag.All, TableUpdateMode.Merge, ct);
        }

        foreach (var existing in activeEntries.Where(i => i.IsLatestPublished))
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
            IsLatestPublished = true,
            IsActive = true,
        };
        await _table.AddEntityAsync(ToEntity(newImage), ct);
        return newImage;
    }

    public async Task<RecoveryImage?> GetByIdAsync(Guid recoveryImageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, recoveryImageId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public IAsyncEnumerable<RecoveryImage> ListActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and IsActive eq true");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    public async Task DeleteAsync(Guid recoveryImageId, CancellationToken ct = default) =>
        await _table.DeleteEntityAsync(Partition, recoveryImageId.ToString(), cancellationToken: ct);

    private static TableEntity ToEntity(RecoveryImage i) => new(Partition, i.RecoveryImageId.ToString())
    {
        ["Version"] = i.Version,
        ["Description"] = i.Description,
        ["SizeBytes"] = i.SizeBytes,
        ["StoragePath"] = i.StoragePath,
        ["Sha256Hash"] = i.Sha256Hash,
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
        IsLatestPublished = e.GetBoolean("IsLatestPublished") ?? false,
        IsActive = e.GetBoolean("IsActive") ?? false,
        UploadedAt = e.GetDateTimeOffset("UploadedAt") ?? DateTimeOffset.UtcNow,
    };
}

using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for OS image catalog (FR-036, FR-037).
/// PartitionKey: "catalog" | RowKey: imageId.
/// Enforces a maximum of 500 active entries (per plan.md capacity budget) — unlike the Boot/
/// Recovery catalogs' 5-entry cap, the limit here is a generous ceiling rather than an
/// auto-demote-oldest rotation, since OS images are typically retained deliberately.
/// </summary>
public sealed class OsImageRepository
{
    private const string TableName = "OSImages";
    private const string Partition = "catalog";
    public const int MaxActiveEntries = 500;
    private readonly TableClient _table;

    public OsImageRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Number of active catalog entries, used to enforce <see cref="MaxActiveEntries"/>.</summary>
    public async Task<int> GetActiveCountAsync(CancellationToken ct = default) =>
        await ListActiveAsync(ct).CountAsync(ct);

    public async Task CreateAsync(OsImage image, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(image), ct);

    public async Task<OsImage?> GetByIdAsync(Guid imageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, imageId.ToString(), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task UpdateAsync(OsImage image, CancellationToken ct = default) =>
        await _table.UpdateEntityAsync(ToEntity(image), ETag.All, TableUpdateMode.Replace, ct);

    public async Task DeleteAsync(Guid imageId, CancellationToken ct = default) =>
        await _table.DeleteEntityAsync(Partition, imageId.ToString(), cancellationToken: ct);

    public IAsyncEnumerable<OsImage> ListActiveAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and IsActive eq true");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    private static TableEntity ToEntity(OsImage i) => new(Partition, i.ImageId.ToString())
    {
        ["Name"] = i.Name,
        ["Version"] = i.Version,
        ["Description"] = i.Description,
        ["StoragePath"] = i.StoragePath,
        ["SizeBytes"] = i.SizeBytes,
        ["Sha256Hash"] = i.Sha256Hash,
        ["IsInUse"] = i.IsInUse,
        ["IsActive"] = true,
        ["UploadedAt"] = i.UploadedAt,
    };

    private static OsImage FromEntity(TableEntity e) => new()
    {
        ImageId = Guid.Parse(e.RowKey),
        Name = e.GetString("Name") ?? string.Empty,
        Version = e.GetString("Version") ?? string.Empty,
        Description = e.GetString("Description"),
        StoragePath = e.GetString("StoragePath") ?? string.Empty,
        SizeBytes = e.GetInt64("SizeBytes") ?? 0L,
        Sha256Hash = e.GetString("Sha256Hash") ?? string.Empty,
        IsInUse = e.GetBoolean("IsInUse") ?? false,
        UploadedAt = e.GetDateTimeOffset("UploadedAt") ?? DateTimeOffset.UtcNow,
    };
}

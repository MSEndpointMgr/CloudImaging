using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for boot image catalog (FR-063).
/// PartitionKey: "catalog" | RowKey: bootImageId.
/// Uploads land in pre-production; <see cref="PromoteAsync"/> makes one production and latest.
/// Capacity and "latest" are scoped per architecture, so ARM64 never demotes or un-latests x64.
/// </summary>
public sealed class BootImageRepository
{
    private const string TableName = "BootImages";
    private const string Partition = "catalog";
    public const int MaxActiveEntriesPerArchitecture = 5;
    private readonly TableClient _table;

    public BootImageRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Oldest same-architecture entries to demote so a new upload fits. The latest production
    /// image is never demoted: devices would lose their self-update target.
    /// </summary>
    public static IReadOnlyList<BootImage> PlanCapacity(IEnumerable<BootImage> activeEntries, MachineArchitecture architecture)
    {
        var sameArchitecture = activeEntries.Where(b => b.Architecture == architecture).ToList();
        var excess = sameArchitecture.Count - MaxActiveEntriesPerArchitecture + 1;
        return excess > 0
            ? sameArchitecture.Where(b => !b.IsLatestPublished).OrderBy(b => b.CreatedAt).Take(excess).ToList()
            : [];
    }

    /// <summary>Same-architecture entries that lose the latest flag when <paramref name="promoted"/> is promoted.</summary>
    public static IReadOnlyList<BootImage> PlanPromote(IEnumerable<BootImage> activeEntries, BootImage promoted) =>
        activeEntries
            .Where(b => b.Architecture == promoted.Architecture && b.IsLatestPublished && b.BootImageId != promoted.BootImageId)
            .ToList();

    /// <summary>
    /// The production image that becomes latest again when <paramref name="demoted"/> (the current
    /// latest) is demoted: the most recently promoted other one of the same architecture, or null.
    /// </summary>
    public static BootImage? PlanDemote(IEnumerable<BootImage> activeEntries, BootImage demoted) =>
        demoted.IsLatestPublished
            ? activeEntries
                .Where(b => b.Architecture == demoted.Architecture && b.IsProduction && b.BootImageId != demoted.BootImageId)
                .OrderByDescending(b => b.PromotedAt ?? b.CreatedAt)
                .FirstOrDefault()
            : null;

    /// <summary>Adds a verified upload to the catalog in pre-production.</summary>
    public async Task<BootImage> PublishAsync(BootImage image, CancellationToken ct = default)
    {
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);

        foreach (var oldest in PlanCapacity(activeEntries, image.Architecture))
        {
            var demoted = new TableEntity(Partition, oldest.BootImageId.ToString());
            demoted["IsActive"] = false;
            await _table.UpdateEntityAsync(demoted, ETag.All, TableUpdateMode.Merge, ct);
        }

        var newImage = new BootImage
        {
            BootImageId = image.BootImageId,
            Version = image.Version,
            CreatedAt = image.CreatedAt == default ? DateTimeOffset.UtcNow : image.CreatedAt,
            SizeBytes = image.SizeBytes,
            StoragePath = image.StoragePath,
            ManifestVersion = image.ManifestVersion,
            Sha256Hash = image.Sha256Hash,
            Architecture = image.Architecture,
            IsLatestPublished = false,
            IsProduction = false,
            IsActive = true,
        };
        await _table.AddEntityAsync(ToEntity(newImage), ct);
        return newImage;
    }

    /// <summary>
    /// Moves an image to production and makes it the latest for its architecture. Idempotent.
    /// Returns null when the image does not exist or is no longer active.
    /// </summary>
    public async Task<BootImage?> PromoteAsync(Guid bootImageId, CancellationToken ct = default)
    {
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);
        var target = activeEntries.FirstOrDefault(b => b.BootImageId == bootImageId);
        if (target is null)
        {
            return null;
        }

        // Set the new latest first, so a failure part-way never leaves an architecture without one.
        var promote = new TableEntity(Partition, bootImageId.ToString())
        {
            ["IsProduction"] = true,
            ["IsLatestPublished"] = true,
            ["PromotedAt"] = DateTimeOffset.UtcNow,
        };
        await _table.UpdateEntityAsync(promote, ETag.All, TableUpdateMode.Merge, ct);

        foreach (var previous in PlanPromote(activeEntries, target))
        {
            var patch = new TableEntity(Partition, previous.BootImageId.ToString());
            patch["IsLatestPublished"] = false;
            await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
        }

        return await GetByIdAsync(bootImageId, ct);
    }

    /// <summary>
    /// Moves a production image back to pre-production. When it was the latest, the previously
    /// promoted image of its architecture becomes latest again (none may exist). Idempotent.
    /// Returns null when the image does not exist or is no longer active.
    /// </summary>
    public async Task<(BootImage Demoted, BootImage? RestoredLatest)?> DemoteAsync(Guid bootImageId, CancellationToken ct = default)
    {
        var activeEntries = await ListActiveAsync(ct).ToListAsync(ct);
        var target = activeEntries.FirstOrDefault(b => b.BootImageId == bootImageId);
        if (target is null)
        {
            return null;
        }

        // Restore the fallback first, so a failure part-way never leaves the architecture without a latest.
        var restored = PlanDemote(activeEntries, target);
        if (restored is not null)
        {
            var patch = new TableEntity(Partition, restored.BootImageId.ToString()) { ["IsLatestPublished"] = true };
            await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
        }

        var demote = new TableEntity(Partition, bootImageId.ToString())
        {
            ["IsProduction"] = false,
            ["IsLatestPublished"] = false,
        };
        await _table.UpdateEntityAsync(demote, ETag.All, TableUpdateMode.Merge, ct);

        var demoted = await GetByIdAsync(bootImageId, ct);
        return demoted is null ? null : (demoted, restored is null ? null : await GetByIdAsync(restored.BootImageId, ct));
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
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and IsActive eq true");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    public async Task DeleteAsync(Guid bootImageId, CancellationToken ct = default) =>
        await _table.DeleteEntityAsync(Partition, bootImageId.ToString(), cancellationToken: ct);

    private static TableEntity ToEntity(BootImage b) => new(Partition, b.BootImageId.ToString())
    {
        ["Version"] = b.Version,
        ["SizeBytes"] = b.SizeBytes,
        ["StoragePath"] = b.StoragePath,
        ["ManifestVersion"] = b.ManifestVersion,
        ["Sha256Hash"] = b.Sha256Hash,
        ["Architecture"] = MachineArchitecturePlatform.Slug(b.Architecture),
        ["IsLatestPublished"] = b.IsLatestPublished,
        ["IsProduction"] = b.IsProduction,
        ["PromotedAt"] = b.PromotedAt,
        ["IsActive"] = b.IsActive,
        ["CreatedAt"] = b.CreatedAt,
    };

    private static BootImage FromEntity(TableEntity e) => new()
    {
        BootImageId = Guid.Parse(e.RowKey),
        Version = e.GetString("Version") ?? string.Empty,
        SizeBytes = e.GetInt64("SizeBytes") ?? 0L,
        StoragePath = e.GetString("StoragePath") ?? string.Empty,
        ManifestVersion = e.GetString("ManifestVersion") ?? string.Empty,
        Sha256Hash = e.GetString("Sha256Hash") ?? string.Empty,
        // A missing/unrecognized value predates architecture tracking and MUST read back as x64
        // (see MachineArchitecture's own doc comment) — there is no ARM64 product history.
        Architecture = e.GetString("Architecture") switch
        {
            "arm64" => MachineArchitecture.Arm64,
            _ => MachineArchitecture.X64,
        },
        IsLatestPublished = e.GetBoolean("IsLatestPublished") ?? false,
        // Rows written before the pre-production stage existed were already live in production.
        IsProduction = e.GetBoolean("IsProduction") ?? true,
        PromotedAt = e.GetDateTimeOffset("PromotedAt"),
        IsActive = e.GetBoolean("IsActive") ?? false,
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
    };
}

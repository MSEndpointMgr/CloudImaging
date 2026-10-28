using Azure.Data.Tables;
using Azure.Storage.Blobs;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.ImagingCoreApi.Repositories.Drivers;

/// <summary>
/// The driver tables of data-model.md section 3 that need no rules beyond their keys. Tables with
/// rules (models and match keys, set pointers, runs) have their own repositories.
/// </summary>
public sealed class DriverStore
{
    /// <summary>Partition of the vendor settings rows.</summary>
    public const string VendorPartition = "vendor";

    /// <summary>Partition and row of the settings row.</summary>
    public const string SettingsPartition = "settings";

    /// <summary>Row key of the settings row.</summary>
    public const string SettingsRow = "default";

    /// <summary>Partition of the model list freshness rows.</summary>
    public const string ListPartition = "list";

    /// <summary>Partition of the rules.</summary>
    public const string RulePartition = "rule";

    /// <summary>Partition of the change log.</summary>
    public const string LogPartition = "log";

    /// <summary>Partition of the custom components.</summary>
    public const string CustomPartition = "custom";

    /// <summary>Partition of the staged uploads.</summary>
    public const string UploadPartition = "upload";

    /// <param name="tables">Table service.</param>
    public DriverStore(TableServiceClient tables)
    {
        VendorSettings = new(tables, DriverTables.VendorSettings);
        SyncSettings = new(tables, DriverTables.SyncSettings);
        CatalogModels = new(tables, DriverTables.CatalogModels);
        CatalogLists = new(tables, DriverTables.CatalogModels);
        Components = new(tables, DriverTables.Components);
        Overrides = new(tables, DriverTables.Overrides);
        Rules = new(tables, DriverTables.Rules);
        CriticalCategories = new(tables, DriverTables.CriticalCategories);
        Files = new(tables, DriverTables.Files);
        ChangeLog = new(tables, DriverTables.ChangeLog);
        ComponentVersions = new(tables, DriverTables.ComponentVersions);
        RollBacks = new(tables, DriverTables.RollBacks);
        SetBuilds = new(tables, DriverTables.SetBuilds);
        CustomComponents = new(tables, DriverTables.CustomComponents);
        CustomTargets = new(tables, DriverTables.CustomTargets);
        Replacements = new(tables, DriverTables.Replacements);
        CustomUploads = new(tables, DriverTables.CustomUploads);
    }

    /// <summary>Custom components (PK <c>custom</c>, RK custom id).</summary>
    public DriverJsonTable<DriverCustomComponentRecord> CustomComponents { get; }

    /// <summary>Custom component targets (PK custom id, RK model id).</summary>
    public DriverJsonTable<DriverCustomTargetRecord> CustomTargets { get; }

    /// <summary>Replacements (PK model id, RK lineage key), one per vendor lineage per model.</summary>
    public DriverJsonTable<DriverReplacementRecord> Replacements { get; }

    /// <summary>Staged uploads (PK <c>upload</c>, RK upload id).</summary>
    public DriverJsonTable<DriverCustomUploadRecord> CustomUploads { get; }

    /// <summary>Vendor Sync switches (PK <c>vendor</c>, RK vendor).</summary>
    public DriverJsonTable<DriverVendorSettingRecord> VendorSettings { get; }

    /// <summary>Sync and storage settings (PK <c>settings</c>, RK <c>default</c>).</summary>
    public DriverJsonTable<DriverSyncSettingsRecord> SyncSettings { get; }

    /// <summary>Vendor model lists (PK vendor, RK catalog model key).</summary>
    public DriverJsonTable<DriverCatalogModelRecord> CatalogModels { get; }

    /// <summary>Model list freshness (PK <c>list</c>, RK vendor), in the same table.</summary>
    public DriverJsonTable<DriverCatalogListRecord> CatalogLists { get; }

    /// <summary>Components (PK model and release, RK lineage key).</summary>
    public DriverJsonTable<DriverComponentRecord> Components { get; }

    /// <summary>Overrides (PK model and release, RK lineage key).</summary>
    public DriverJsonTable<DriverOverrideRecord> Overrides { get; }

    /// <summary>Rules (PK <c>rule</c>, RK rule id).</summary>
    public DriverJsonTable<DriverRuleRecord> Rules { get; }

    /// <summary>Critical categories (PK vendor, RK category).</summary>
    public DriverJsonTable<DriverCriticalCategoryRecord> CriticalCategories { get; }

    /// <summary>Stored files (PK vendor, RK component id and version).</summary>
    public DriverJsonTable<DriverFileRecord> Files { get; }

    /// <summary>Change log (PK <c>log</c>, RK inverted ticks).</summary>
    public DriverJsonTable<DriverChangeLogRecord> ChangeLog { get; }

    /// <summary>Component versions (PK model and release, RK lineage and inverted first-use ticks).</summary>
    public DriverJsonTable<DriverComponentVersionRecord> ComponentVersions { get; }

    /// <summary>Component roll backs (PK model and release, RK lineage key).</summary>
    public DriverJsonTable<DriverRollBackRecord> RollBacks { get; }

    /// <summary>Set builds (PK model and release, RK version).</summary>
    public DriverJsonTable<DriverSetBuildRecord> SetBuilds { get; }

    /// <summary>Creates every table of this store.</summary>
    public async Task EnsureTablesExistAsync(CancellationToken ct = default)
    {
        await VendorSettings.EnsureExistsAsync(ct);
        await SyncSettings.EnsureExistsAsync(ct);
        await CatalogModels.EnsureExistsAsync(ct);
        await Components.EnsureExistsAsync(ct);
        await Overrides.EnsureExistsAsync(ct);
        await Rules.EnsureExistsAsync(ct);
        await CriticalCategories.EnsureExistsAsync(ct);
        await Files.EnsureExistsAsync(ct);
        await ChangeLog.EnsureExistsAsync(ct);
        await ComponentVersions.EnsureExistsAsync(ct);
        await RollBacks.EnsureExistsAsync(ct);
        await SetBuilds.EnsureExistsAsync(ct);
        await CustomComponents.EnsureExistsAsync(ct);
        await CustomTargets.EnsureExistsAsync(ct);
        await Replacements.EnsureExistsAsync(ct);
        await CustomUploads.EnsureExistsAsync(ct);
    }

    /// <summary>Reads the settings row, or the defaults when it does not exist yet.</summary>
    public async Task<Stored<DriverSyncSettingsRecord>> GetSyncSettingsAsync(CancellationToken ct = default) =>
        await SyncSettings.GetAsync(SettingsPartition, SettingsRow, ct)
        ?? new Stored<DriverSyncSettingsRecord>(SettingsRow, new DriverSyncSettingsRecord(), default);

    /// <summary>Writes one change log row.</summary>
    public async Task LogAsync(DateTimeOffset at, string by, string? objectId, string area, string subject, string change, CancellationToken ct = default)
    {
        var record = new DriverChangeLogRecord { At = at, By = by, ObjectId = objectId, Area = area, Subject = subject, Change = change };

        // Two changes in the same tick would collide on the row key; the suffix keeps both.
        var rowKey = $"{DriverTables.InvertedTicks(at)}_{Guid.NewGuid():N}";
        await ChangeLog.UpsertAsync(LogPartition, rowKey, record, ct);
    }

    /// <summary>Whether a vendor's Sync switch is on.</summary>
    public async Task<bool> IsSyncEnabledAsync(DriverVendor vendor, CancellationToken ct = default) =>
        (await VendorSettings.GetAsync(VendorPartition, vendor.ToString(), ct))?.Value.SyncEnabled ?? false;
}

/// <summary>Resolved set documents in the <c>drivers</c> container (<c>sets/&lt;modelId&gt;/&lt;release&gt;/&lt;version&gt;.json</c>).</summary>
public sealed class DriverSetDocumentRepository(BlobContainerClient container)
{
    /// <summary>Blob name of a set document.</summary>
    public static string BlobName(Guid modelId, WindowsRelease release, int version) =>
        $"sets/{modelId:D}/{release}/{version:000000}.json";

    /// <summary>Writes a set document; set documents never change once written.</summary>
    public async Task SaveAsync(DriverSetDocument document, CancellationToken ct = default) =>
        await SaveAsync(document, null, ct);

    /// <summary>
    /// Writes a set document only when the stored one still has <paramref name="expected"/>
    /// (<see cref="Azure.ETag"/> default: none may exist). False when another build wrote it first.
    /// </summary>
    public async Task<bool> SaveAsync(DriverSetDocument document, Azure.ETag? expected, CancellationToken ct = default)
    {
        var data = BinaryData.FromObjectAsJson(document, DriverJson.Options);
        var conditions = expected is null ? null : expected.Value == default ? new Azure.Storage.Blobs.Models.BlobRequestConditions { IfNoneMatch = Azure.ETag.All } : new Azure.Storage.Blobs.Models.BlobRequestConditions { IfMatch = expected.Value };
        try
        {
            await container.GetBlobClient(BlobName(document.ModelId, document.Release, document.Version))
                .UploadAsync(data, new Azure.Storage.Blobs.Models.BlobUploadOptions { Conditions = conditions }, ct);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    /// <summary>The stored document's ETag, or the default ETag when there is none.</summary>
    public async Task<Azure.ETag> GetETagAsync(Guid modelId, WindowsRelease release, int version, CancellationToken ct = default)
    {
        try
        {
            return (await container.GetBlobClient(BlobName(modelId, release, version)).GetPropertiesAsync(cancellationToken: ct)).Value.ETag;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return default;
        }
    }

    /// <summary>Reads a set document, or null when missing.</summary>
    public async Task<DriverSetDocument?> GetAsync(Guid modelId, WindowsRelease release, int version, CancellationToken ct = default)
    {
        try
        {
            var result = await container.GetBlobClient(BlobName(modelId, release, version)).DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<DriverSetDocument>(DriverJson.Options);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Deletes a set document.</summary>
    public async Task DeleteAsync(Guid modelId, WindowsRelease release, int version, CancellationToken ct = default) =>
        await container.GetBlobClient(BlobName(modelId, release, version)).DeleteIfExistsAsync(cancellationToken: ct);

    /// <summary>Every stored set document.</summary>
    public async Task<List<(Guid ModelId, WindowsRelease Release, int Version)>> ListAsync(CancellationToken ct = default)
    {
        var documents = new List<(Guid, WindowsRelease, int)>();
        await foreach (var blob in container.GetBlobsAsync(prefix: "sets/", cancellationToken: ct))
        {
            var parts = blob.Name.Split('/');
            if (parts.Length == 4
                && Guid.TryParse(parts[1], out var modelId)
                && Enum.TryParse<WindowsRelease>(parts[2], out var release)
                && int.TryParse(Path.GetFileNameWithoutExtension(parts[3]), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
            {
                documents.Add((modelId, release, version));
            }
        }

        return documents;
    }
}

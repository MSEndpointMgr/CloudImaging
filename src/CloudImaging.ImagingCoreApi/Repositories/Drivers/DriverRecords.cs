using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;

namespace CloudImaging.ImagingCoreApi.Repositories.Drivers;

/// <summary>Table names and key helpers of data-model.md section 3.</summary>
public static class DriverTables
{
    /// <summary>Vendor Sync switches.</summary>
    public const string VendorSettings = "DriverVendorSettings";

    /// <summary>Sync and storage settings.</summary>
    public const string SyncSettings = "DriverSyncSettings";

    /// <summary>Vendor model lists.</summary>
    public const string CatalogModels = "DriverCatalogModels";

    /// <summary>Onboarded models.</summary>
    public const string Models = "DriverModels";

    /// <summary>Match key reservations.</summary>
    public const string MatchKeys = "DriverMatchKeys";

    /// <summary>Components per model and release.</summary>
    public const string Components = "DriverComponents";

    /// <summary>Per-model actions.</summary>
    public const string Overrides = "DriverOverrides";

    /// <summary>Rules.</summary>
    public const string Rules = "DriverRules";

    /// <summary>Critical categories per vendor.</summary>
    public const string CriticalCategories = "DriverCriticalCategories";

    /// <summary>Set pointers per model and release.</summary>
    public const string SetPointers = "DriverSetPointers";

    /// <summary>Stored files.</summary>
    public const string Files = "DriverFiles";

    /// <summary>Runs and the active-run lock.</summary>
    public const string SyncRuns = "DriverSyncRuns";

    /// <summary>Run items.</summary>
    public const string SyncRunItems = "DriverSyncRunItems";

    /// <summary>Change log.</summary>
    public const string ChangeLog = "DriverChangeLog";

    /// <summary>Component version history.</summary>
    public const string ComponentVersions = "DriverComponentVersions";

    /// <summary>Component roll backs.</summary>
    public const string RollBacks = "DriverRollBacks";

    /// <summary>Custom components.</summary>
    public const string CustomComponents = "DriverCustomComponents";

    /// <summary>Custom component targets.</summary>
    public const string CustomTargets = "DriverCustomTargets";

    /// <summary>One replacement per vendor lineage per model.</summary>
    public const string Replacements = "DriverReplacements";

    /// <summary>Staged custom component uploads.</summary>
    public const string CustomUploads = "DriverCustomUploads";

    /// <summary>Set builds, for the Downloads tab.</summary>
    public const string SetBuilds = "DriverSetBuilds";

    /// <summary>Blob container for catalogs, files and set documents.</summary>
    public const string Container = "drivers";

    /// <summary>Partition key of a model and release pair.</summary>
    public static string Pair(Guid modelId, WindowsRelease release) => $"{modelId:D}_{release}";

    /// <summary>Row key that sorts newest first.</summary>
    public static string InvertedTicks(DateTimeOffset at) => (DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks).ToString("D19", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Upper-case match key as stored.</summary>
    public static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

    /// <summary>Row key of a category: escaped, since names such as "Diagnostic/Utility" hold characters a key may not.</summary>
    public static string CategoryKey(string category) => Uri.EscapeDataString(category.Trim());
}

/// <summary>A vendor's Sync switch (3.1).</summary>
public sealed record DriverVendorSettingRecord
{
    /// <summary>Sync enabled.</summary>
    public bool SyncEnabled { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>Sync and storage settings (3.2).</summary>
public sealed record DriverSyncSettingsRecord
{
    /// <summary>Schedule.</summary>
    public DriverSyncSchedule Schedule { get; init; } = DriverSyncSchedule.Daily;

    /// <summary>Weekdays.</summary>
    public int Weekdays { get; init; }

    /// <summary>Repeat weeks.</summary>
    public int RepeatWeeks { get; init; } = 1;

    /// <summary>Anchor week.</summary>
    public string? AnchorWeek { get; init; }

    /// <summary>Local time.</summary>
    public string LocalTime { get; init; } = "02:00";

    /// <summary>Time zone.</summary>
    public string TimeZone { get; init; } = "Etc/UTC";

    /// <summary>Wait for approval of new components.</summary>
    public bool WaitForApprovalOfNewComponents { get; init; }

    /// <summary>Previous versions to keep.</summary>
    public int PreviousVersionsToKeep { get; init; } = 1;

    /// <summary>Last scheduled slot utc.</summary>
    public DateTimeOffset? LastScheduledSlotUtc { get; init; }

    /// <summary>Last run started at.</summary>
    public DateTimeOffset? LastRunStartedAt { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>A Dell per-SKU catalog reference.</summary>
public sealed record DriverSkuCatalog(string Sku, string Path, string? Sha256);

/// <summary>A model from a vendor's model list (3.3).</summary>
public sealed record DriverCatalogModelRecord
{
    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Match keys.</summary>
    public IReadOnlyList<string> MatchKeys { get; init; } = [];

    /// <summary>Architecture.</summary>
    public MachineArchitecture Architecture { get; init; }

    /// <summary>Releases.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>Sku catalogs.</summary>
    public IReadOnlyList<DriverSkuCatalog> SkuCatalogs { get; init; } = [];

    /// <summary>Seen at.</summary>
    public DateTimeOffset SeenAt { get; init; }
}

/// <summary>Freshness of a vendor's stored model list (row <c>list/&lt;vendor&gt;</c>).</summary>
public sealed record DriverCatalogListRecord
{
    /// <summary>Fetched at.</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>Catalog url.</summary>
    public required string CatalogUrl { get; init; }

    /// <summary>Model count.</summary>
    public int ModelCount { get; init; }

    /// <summary>Last attempt at.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>Last error.</summary>
    public string? LastError { get; init; }
}

/// <summary>An onboarded model (3.4).</summary>
public sealed record DriverModelRecord
{
    /// <summary>Model id.</summary>
    public required Guid ModelId { get; init; }

    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Architecture.</summary>
    public MachineArchitecture Architecture { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Catalog model key.</summary>
    public required string CatalogModelKey { get; init; }

    /// <summary>Match keys.</summary>
    public IReadOnlyList<string> MatchKeys { get; init; } = [];

    /// <summary>Releases.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>State.</summary>
    public DriverModelState State { get; init; } = DriverModelState.Draft;

    /// <summary>Onboarded at.</summary>
    public DateTimeOffset OnboardedAt { get; init; }

    /// <summary>Onboarded by.</summary>
    public string? OnboardedBy { get; init; }

    /// <summary>Completed at.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Completed by.</summary>
    public string? CompletedBy { get; init; }

    /// <summary>Last checked at.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }
}

/// <summary>Reservation of one match key by one model.</summary>
public sealed record DriverMatchKeyRecord(Guid ModelId);

/// <summary>The latest known version of one component lineage for a model and release (3.5).</summary>
public sealed record DriverComponentRecord
{
    /// <summary>Component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Category.</summary>
    public string? Category { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Released at.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>Previous version.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Size.</summary>
    public long Size { get; init; }

    /// <summary>Sha256.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Url.</summary>
    public string? Url { get; init; }

    /// <summary>File name.</summary>
    public string? FileName { get; init; }

    /// <summary>Allowed actions.</summary>
    public IReadOnlyList<DriverAction> AllowedActions { get; init; } = [DriverAction.Skip];

    /// <summary>Default action.</summary>
    public DriverAction DefaultAction { get; init; } = DriverAction.Skip;

    /// <summary>Extract.</summary>
    public DriverExtractKind Extract { get; init; }

    /// <summary>Install command.</summary>
    public DriverInstallCommand? InstallCommand { get; init; }

    /// <summary>Pci ids.</summary>
    public IReadOnlyList<DriverPciId> PciIds { get; init; } = [];

    /// <summary>Details.</summary>
    public IReadOnlyList<DriverDetailField> Details { get; init; } = [];

    /// <summary>Description url.</summary>
    public string? DescriptionUrl { get; init; }

    /// <summary>Description.</summary>
    public string? Description { get; init; }

    /// <summary>State.</summary>
    public DriverComponentState State { get; init; } = DriverComponentState.Active;

    /// <summary>Added at.</summary>
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>Updated at.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Verified version.</summary>
    public string? VerifiedVersion { get; init; }

    /// <summary>Verified file.</summary>
    public string? VerifiedFile { get; init; }
}

/// <summary>An action chosen for one model (3.6).</summary>
public sealed record DriverOverrideRecord
{
    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>A rule (3.7).</summary>
public sealed record DriverRuleRecord
{
    /// <summary>Rule id.</summary>
    public required Guid RuleId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Scope.</summary>
    public DriverRuleScope Scope { get; init; }

    /// <summary>Match.</summary>
    public DriverRuleMatch Match { get; init; }

    /// <summary>Value.</summary>
    public required string Value { get; init; }

    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>A critical category (3.8).</summary>
public sealed record DriverCriticalCategoryRecord
{
    /// <summary>Category name; the row key is an encoded form, since a category can hold characters a key may not.</summary>
    public string? Category { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>Set pointers of a model and release (3.9).</summary>
public sealed record DriverSetPointerRecord
{
    /// <summary>Current version.</summary>
    public int? CurrentVersion { get; init; }

    /// <summary>Previous version.</summary>
    public int? PreviousVersion { get; init; }

    /// <summary>Current fingerprint.</summary>
    public string? CurrentFingerprint { get; init; }

    /// <summary>Rolled back from fingerprint.</summary>
    public string? RolledBackFromFingerprint { get; init; }

    /// <summary>Last version.</summary>
    public int LastVersion { get; init; }

    /// <summary>Pending version.</summary>
    public int? PendingVersion { get; init; }

    /// <summary>Dirty.</summary>
    public bool Dirty { get; init; }

    /// <summary>Build after.</summary>
    public DateTimeOffset? BuildAfter { get; init; }

    /// <summary>Pending changes.</summary>
    public int PendingChanges { get; init; }

    /// <summary>Built at.</summary>
    public DateTimeOffset? BuiltAt { get; init; }
}

/// <summary>Status of a stored file.</summary>
public enum DriverFileStatus
{
    /// <summary>Downloading.</summary>
    Downloading,
    /// <summary>Verified.</summary>
    Verified,
    /// <summary>Failed.</summary>
    Failed,
}

/// <summary>A stored file (3.10).</summary>
public sealed record DriverFileRecord
{
    /// <summary>Blob name.</summary>
    public required string BlobName { get; init; }

    /// <summary>Size.</summary>
    public long Size { get; init; }

    /// <summary>Sha256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Status.</summary>
    public DriverFileStatus Status { get; init; }

    /// <summary>Verified at.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Failure reason.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Vendor address the file is downloaded from.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Original file name.</summary>
    public string? FileName { get; init; }

    /// <summary>Component name, for run items and the Downloads tab.</summary>
    public string? ComponentName { get; init; }

    /// <summary>Component version, for run items.</summary>
    public string? Version { get; init; }

    /// <summary>Vendor category of the component, for run items.</summary>
    public string? Category { get; init; }

    /// <summary>Start of the last download attempt; a sync retries files that failed before it started.</summary>
    public DateTimeOffset? AttemptedAt { get; init; }
}

/// <summary>A run (3.11).</summary>
public sealed record DriverSyncRunRecord
{
    /// <summary>Run id.</summary>
    public required Guid RunId { get; init; }

    /// <summary>Trigger.</summary>
    public DriverSyncTrigger Trigger { get; init; }

    /// <summary>Model id.</summary>
    public Guid? ModelId { get; init; }

    /// <summary>Model name.</summary>
    public string? ModelName { get; init; }

    /// <summary>Outstanding.</summary>
    public int Outstanding { get; init; }

    /// <summary>Started at.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Finished at.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Result.</summary>
    public DriverSyncResult Result { get; init; } = DriverSyncResult.Running;

    /// <summary>Started by.</summary>
    public string? StartedBy { get; init; }

    /// <summary>Counts.</summary>
    public Dictionary<DriverSyncItemOutcome, int> Counts { get; init; } = [];

    /// <summary>Vendors.</summary>
    public List<DriverSyncRunVendor> Vendors { get; init; } = [];

    /// <summary>Vendors whose catalogs the run set out to read, known before any is read.</summary>
    public List<DriverVendor> PlannedVendors { get; init; } = [];

    /// <summary>Items found so far per outcome; <see cref="Counts"/> is recounted from the items when the run ends.</summary>
    public Dictionary<DriverSyncItemOutcome, int> Found { get; init; } = [];

    /// <summary>The newest items, newest first.</summary>
    public List<Contracts.Models.Drivers.DriverSyncRunItem> Latest { get; init; } = [];

    /// <summary>Downloads queued, done or not.</summary>
    public int DownloadsQueued { get; init; }

    /// <summary>Set builds queued, done or not.</summary>
    public int SetChecksQueued { get; init; }

    /// <summary>Vendor catalogs the run reads.</summary>
    public int CatalogsTotal { get; init; }

    /// <summary>Models the run checks, added as each vendor catalog is read.</summary>
    public int ModelsTotal { get; init; }

    /// <summary>Models whose every release was checked.</summary>
    public int ModelsChecked { get; init; }

    /// <summary>Set builds done.</summary>
    public int SetChecks { get; init; }

    /// <summary>New sets that became current.</summary>
    public int NewSets { get; init; }

    /// <summary>Downloads done, verified or failed.</summary>
    public int Downloads { get; init; }

    /// <summary>Components downloaded and verified.</summary>
    public int DownloadedComponents { get; init; }

    /// <summary>Bytes of the downloaded components.</summary>
    public long DownloadedBytes { get; init; }
}

/// <summary>The active-run lock row.</summary>
public sealed record DriverActiveRunRecord(Guid RunId, DateTimeOffset StartedAt);

/// <summary>A change log row (3.13).</summary>
public sealed record DriverChangeLogRecord
{
    /// <summary>At.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>By.</summary>
    public required string By { get; init; }

    /// <summary>Object id.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Area.</summary>
    public required string Area { get; init; }

    /// <summary>Subject.</summary>
    public required string Subject { get; init; }

    /// <summary>Change.</summary>
    public required string Change { get; init; }
}

/// <summary>One version a component has had on a model and release (3.14).</summary>
public sealed record DriverComponentVersionRecord
{
    /// <summary>Lineage key.</summary>
    public required string LineageKey { get; init; }

    /// <summary>Component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>First set.</summary>
    public int FirstSet { get; init; }

    /// <summary>Last set.</summary>
    public int? LastSet { get; init; }

    /// <summary>From.</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>To.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>Url.</summary>
    public string? Url { get; init; }

    /// <summary>Sha256.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Size.</summary>
    public long Size { get; init; }

    /// <summary>File name.</summary>
    public string? FileName { get; init; }

    /// <summary>Origin.</summary>
    public DriverComponentOrigin Origin { get; init; }

    /// <summary>Available.</summary>
    public bool? Available { get; init; }

    /// <summary>Availability checked at.</summary>
    public DateTimeOffset? AvailabilityCheckedAt { get; init; }
}

/// <summary>A component roll back (3.15).</summary>
public sealed record DriverRollBackRecord
{
    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>Until.</summary>
    public DriverRollBackUntil Until { get; init; }

    /// <summary>Skipped version.</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>A set build (3.17).</summary>
public sealed record DriverSetBuildRecord
{
    /// <summary>Trigger.</summary>
    public string Trigger { get; init; } = "Sync";

    /// <summary>Built at.</summary>
    public DateTimeOffset BuiltAt { get; init; }

    /// <summary>Ready at.</summary>
    public DateTimeOffset? ReadyAt { get; init; }

    /// <summary>Components.</summary>
    public int Components { get; init; }

    /// <summary>New components.</summary>
    public int NewComponents { get; init; }

    /// <summary>Reused components.</summary>
    public int ReusedComponents { get; init; }

    /// <summary>Size bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Downloaded bytes.</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>Failed components.</summary>
    public int FailedComponents { get; init; }
}

/// <summary>One item of a resolved driver set.</summary>
public sealed record DriverSetItem
{
    /// <summary>Lineage key.</summary>
    public required string LineageKey { get; init; }

    /// <summary>Component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Category.</summary>
    public string? Category { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Origin.</summary>
    public DriverComponentOrigin Origin { get; init; }

    /// <summary>File key.</summary>
    public required string FileKey { get; init; }

    /// <summary>Blob name, stored as <c>file</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("file")]
    public required string BlobName { get; init; }

    /// <summary>Sha256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size, stored as <c>sizeBytes</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sizeBytes")]
    public long Size { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>Extract, stored under <c>install</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DriverExtractKind Extract { get; init; }

    /// <summary>Install command, stored under <c>install</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DriverInstallCommand? InstallCommand { get; init; }

    /// <summary>How the Client installs the item: extraction and the Specialize command.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("install")]
    public DriverSetItemInstall Install
    {
        get => new(Extract, InstallCommand);
        init
        {
            Extract = value?.Extract ?? DriverExtractKind.None;
            InstallCommand = value?.Command;
        }
    }

    /// <summary>Pci ids.</summary>
    public IReadOnlyList<DriverPciId> PciIds { get; init; } = [];
}

/// <summary>The <c>install</c> part of a set item.</summary>
/// <param name="Extract">How the package is extracted.</param>
/// <param name="Command">Specialize command, relative to the staged folder.</param>
public sealed record DriverSetItemInstall(DriverExtractKind Extract, DriverInstallCommand? Command);

/// <summary>A resolved driver set document, stored in blob storage.</summary>
public sealed record DriverSetDocument
{
    /// <summary>Current document schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Document schema.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Model id.</summary>
    public required Guid ModelId { get; init; }

    /// <summary>Release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Version.</summary>
    public int Version { get; init; }

    /// <summary>Fingerprint.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Built at.</summary>
    public DateTimeOffset BuiltAt { get; init; }

    /// <summary>Items.</summary>
    public IReadOnlyList<DriverSetItem> Items { get; init; } = [];
}

/// <summary>A custom component (3.16; PK <c>custom</c>, RK custom id).</summary>
public sealed record DriverCustomComponentRecord
{
    /// <summary>Id.</summary>
    public Guid CustomId { get; init; }

    /// <summary>Name, 1 to 100 characters.</summary>
    public required string Name { get; init; }

    /// <summary>Version, 1 to 50 characters.</summary>
    public required string Version { get; init; }

    /// <summary>Category.</summary>
    public required string Category { get; init; }

    /// <summary>Action; never Skip.</summary>
    public DriverAction Action { get; init; }

    /// <summary>The package in the <c>drivers</c> container.</summary>
    public required string BlobName { get; init; }

    /// <summary>Package SHA-256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Package size.</summary>
    public long Size { get; init; }

    /// <summary>Package file name.</summary>
    public required string FileName { get; init; }

    /// <summary>INF entries in a ZIP.</summary>
    public int InfCount { get; init; }

    /// <summary>Silent command for Specialize actions, relative to the package.</summary>
    public DriverInstallCommand? InstallCommand { get; init; }

    /// <summary>Uploaded at.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>Uploaded by.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>Changed at.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Changed by.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>A model a custom component is added to (PK custom id, RK model id).</summary>
public sealed record DriverCustomTargetRecord
{
    /// <summary>The vendor lineage it replaces on this model, if any.</summary>
    public string? ReplacesLineageKey { get; init; }

    /// <summary>The vendor version the banner was last shown for.</summary>
    public string? NoticeVersion { get; init; }

    /// <summary>The vendor component id of that version.</summary>
    public string? NoticeComponentId { get; init; }

    /// <summary>When the vendor released it.</summary>
    public DateTimeOffset? NoticeReleasedAt { get; init; }

    /// <summary>The notice version that was dismissed.</summary>
    public string? DismissedVersion { get; init; }

    /// <summary>Dismissed at.</summary>
    public DateTimeOffset? DismissedAt { get; init; }

    /// <summary>Dismissed by.</summary>
    public string? DismissedBy { get; init; }
}

/// <summary>The custom component that replaces a vendor lineage on a model (PK model id, RK lineage key).</summary>
public sealed record DriverReplacementRecord
{
    /// <summary>Custom component id.</summary>
    public Guid CustomId { get; init; }
}

/// <summary>A staged custom component upload (PK <c>upload</c>, RK upload id).</summary>
public sealed record DriverCustomUploadRecord
{
    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>Declared size.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Staging blob.</summary>
    public required string BlobName { get; init; }

    /// <summary>Where the browser stages the package; the upload link can write only this blob.</summary>
    public string? StagingBlobName { get; init; }

    /// <summary>UPN of the Operator who started the upload; only they can complete, abandon or use it.</summary>
    public string? StartedBy { get; init; }

    /// <summary>Started at.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>True once the block list is committed and the package checked.</summary>
    public bool Completed { get; init; }

    /// <summary>Package SHA-256 once completed.</summary>
    public string? Sha256 { get; init; }

    /// <summary>INF entries once completed.</summary>
    public int InfCount { get; init; }

    /// <summary>The custom component using it, once created.</summary>
    public Guid? CustomId { get; init; }
}

using System.Text.Json.Serialization;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>The signed-in portal user who made a change, taken from the portal token.</summary>
public sealed record DriverActor
{
    /// <summary>User principal name.</summary>
    public required string Upn { get; init; }

    /// <summary>Entra object id.</summary>
    public string? ObjectId { get; init; }
}

/// <summary>Draft models are being reviewed; active models build sets and match devices.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverModelState
{
    /// <summary>Onboarded but not completed: matches no device and downloads nothing.</summary>
    Draft,

    /// <summary>Onboarding completed.</summary>
    Active,
}

/// <summary>Why Check for updates on a model page is unavailable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverCheckBlockedReason
{
    /// <summary>The model is a draft.</summary>
    Draft,

    /// <summary>The model's vendor has Sync off.</summary>
    SyncOff,

    /// <summary>A run is active.</summary>
    Running,
}

/// <summary>A vendor and its Sync switch.</summary>
public sealed record DriverVendorRow
{
    /// <summary>Vendor.</summary>
    public required DriverVendor Vendor { get; init; }

    /// <summary>False for vendors listed as not supported yet.</summary>
    public bool Supported { get; init; }

    /// <summary>True when runs read this vendor's catalogs.</summary>
    public bool SyncEnabled { get; init; }

    /// <summary>Number of onboarded models.</summary>
    public int OnboardedModels { get; init; }

    /// <summary>How devices are matched ("system ID" for HP, "system SKU" for Dell).</summary>
    public string? MatchKeyType { get; init; }
}

/// <summary>Request to switch a vendor's Sync on or off.</summary>
public sealed record SetDriverVendorRequest
{
    /// <summary>New state.</summary>
    public bool SyncEnabled { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>A model from a vendor's catalog, offered in the onboarding dialog.</summary>
public sealed record DriverCatalogModel
{
    /// <summary>Key of the catalog model (system ID and architecture for HP, model name for Dell).</summary>
    public required string CatalogModelKey { get; init; }

    /// <summary>Vendor.</summary>
    public required DriverVendor Vendor { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>HP system IDs or Dell SKUs.</summary>
    public IReadOnlyList<string> MatchKeys { get; init; } = [];

    /// <summary>Processor architecture of the model.</summary>
    public MachineArchitecture Architecture { get; init; }

    /// <summary>Windows releases the vendor publishes drivers for.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>Set when the model is already onboarded.</summary>
    public Guid? OnboardedModelId { get; init; }
}

/// <summary>A vendor's model list as the onboarding dialog shows it.</summary>
public sealed record DriverCatalogModelList
{
    /// <summary>When the list was fetched from the vendor.</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>The catalog the list came from.</summary>
    public required string CatalogUrl { get; init; }

    /// <summary>True when a refresh failed and an older list is shown.</summary>
    public bool Stale { get; init; }

    /// <summary>Warning shown with a stale list.</summary>
    public string? Warning { get; init; }

    /// <summary>Models, filtered by the search text.</summary>
    public IReadOnlyList<DriverCatalogModel> Models { get; init; } = [];
}

/// <summary>Request to preview or onboard catalog models.</summary>
public sealed record DriverOnboardRequest
{
    /// <summary>Vendor of every model.</summary>
    public required DriverVendor Vendor { get; init; }

    /// <summary>Catalog model keys, at most 20.</summary>
    public IReadOnlyList<string> CatalogModelKeys { get; init; } = [];

    /// <summary>Windows releases to onboard.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>Who onboards; required for onboarding, not for the preview.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>Counts per action for one model in the onboarding preview.</summary>
public sealed record DriverModelPreview
{
    /// <summary>Catalog model key.</summary>
    public required string CatalogModelKey { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Components that are injected offline.</summary>
    public int Offline { get; init; }

    /// <summary>Components that are injected offline and also run during specialize.</summary>
    public int OfflineAndSpecialize { get; init; }

    /// <summary>Components that run during specialize only.</summary>
    public int Specialize { get; init; }

    /// <summary>Components that are skipped.</summary>
    public int Skip { get; init; }

    /// <summary>Download size of the included components.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>Result of the onboarding preview.</summary>
public sealed record DriverPreviewResult
{
    /// <summary>One entry per model that could be read.</summary>
    public IReadOnlyList<DriverModelPreview> Models { get; init; } = [];

    /// <summary>Models whose catalog could not be read.</summary>
    public IReadOnlyList<DriverPreviewError> Errors { get; init; } = [];
}

/// <summary>A model whose component catalog could not be read for the preview.</summary>
public sealed record DriverPreviewError
{
    /// <summary>Catalog model key.</summary>
    public required string CatalogModelKey { get; init; }

    /// <summary>Why it failed.</summary>
    public required string Detail { get; init; }
}

/// <summary>Result of onboarding: the draft models in request order.</summary>
public sealed record DriverOnboardResult
{
    /// <summary>Created model ids.</summary>
    public IReadOnlyList<Guid> ModelIds { get; init; } = [];
}

/// <summary>Download progress of a set being prepared.</summary>
public sealed record DriverProgressCount
{
    /// <summary>Done.</summary>
    public int Done { get; init; }

    /// <summary>Total.</summary>
    public int Total { get; init; }
}

/// <summary>Attention states of a model row.</summary>
public sealed record DriverModelStatus
{
    /// <summary>Components waiting for approval.</summary>
    public int WaitingForApproval { get; init; }

    /// <summary>Components whose last download failed.</summary>
    public int FailedItems { get; init; }

    /// <summary>Why the first failed component failed, with its name; null when none failed.</summary>
    public string? FailureReason { get; init; }

    /// <summary>True when the model's vendor has Sync off.</summary>
    public bool SyncOff { get; init; }

    /// <summary>Set while files of a new set are downloading.</summary>
    public DriverProgressCount? Downloading { get; init; }
}

/// <summary>A row of the Models tab.</summary>
public sealed record DriverModelRow
{
    /// <summary>Model id.</summary>
    public required Guid ModelId { get; init; }

    /// <summary>Vendor.</summary>
    public required DriverVendor Vendor { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>HP system IDs or Dell SKUs.</summary>
    public IReadOnlyList<string> MatchKeys { get; init; } = [];

    /// <summary>Processor architecture.</summary>
    public MachineArchitecture Architecture { get; init; }

    /// <summary>Onboarded Windows releases.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>Draft or active.</summary>
    public DriverModelState State { get; init; }

    /// <summary>Components in the current sets.</summary>
    public int Included { get; init; }

    /// <summary>Skipped components.</summary>
    public int Skipped { get; init; }

    /// <summary>Largest download per device across releases.</summary>
    public long MaxDownloadBytes { get; init; }

    /// <summary>Attention states.</summary>
    public DriverModelStatus Status { get; init; } = new();
}

/// <summary>A component roll back.</summary>
public sealed record DriverComponentRollBackInfo
{
    /// <summary>Held version.</summary>
    public required string Version { get; init; }

    /// <summary>How long the roll back lasts.</summary>
    public DriverRollBackUntil Until { get; init; }

    /// <summary>The version that was current when the roll back started.</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>Who rolled back.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset ChangedAt { get; init; }
}

/// <summary>Custom component information on a component row.</summary>
public sealed record DriverCustomInfo
{
    /// <summary>Custom component id.</summary>
    public required Guid CustomId { get; init; }

    /// <summary>Name of the vendor component it replaces, if any.</summary>
    public string? ReplacesName { get; init; }

    /// <summary>Who uploaded it.</summary>
    public string? UploadedBy { get; init; }
}

/// <summary>The custom component that replaces a vendor component.</summary>
public sealed record DriverReplacedByInfo
{
    /// <summary>Custom component id.</summary>
    public required Guid CustomId { get; init; }

    /// <summary>Custom component name.</summary>
    public required string Name { get; init; }
}

/// <summary>One labelled detail field of a component.</summary>
public sealed record DriverDetailField
{
    /// <summary>Label.</summary>
    public required string Label { get; init; }

    /// <summary>Value.</summary>
    public required string Value { get; init; }
}

/// <summary>A component row on the model page.</summary>
public sealed record DriverComponentRow
{
    /// <summary>Lineage key, stable across versions.</summary>
    public required string LineageKey { get; init; }

    /// <summary>Vendor component id of the current version.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Vendor category or type.</summary>
    public string? Category { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Release date of the current version.</summary>
    public DateOnly? LastUpdated { get; init; }

    /// <summary>Resolved action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Actions that can be chosen.</summary>
    public IReadOnlyList<DriverAction> AllowedActions { get; init; } = [];

    /// <summary>Where the action comes from.</summary>
    public DriverActionSource Source { get; init; }

    /// <summary>Rule name when <see cref="Source"/> is a rule.</summary>
    public string? RuleName { get; init; }

    /// <summary>State.</summary>
    public DriverComponentState State { get; init; }

    /// <summary>True when the last sync changed it.</summary>
    public bool UpdatedByLastSync { get; init; }

    /// <summary>Set when the component is rolled back.</summary>
    public DriverComponentRollBackInfo? RollBack { get; init; }

    /// <summary>Set for a custom component.</summary>
    public DriverCustomInfo? Custom { get; init; }

    /// <summary>Set when a custom component replaces this one.</summary>
    public DriverReplacedByInfo? ReplacedBy { get; init; }

    /// <summary>Vendor-specific labelled fields.</summary>
    public IReadOnlyList<DriverDetailField> Details { get; init; } = [];

    /// <summary>Release notes link.</summary>
    public string? DescriptionUrl { get; init; }

    /// <summary>Download size.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>Edits waiting to be built into a set.</summary>
public sealed record DriverPendingChanges
{
    /// <summary>Number of changes.</summary>
    public int Changes { get; init; }

    /// <summary>When the set is built.</summary>
    public DateTimeOffset BuildAt { get; init; }
}

/// <summary>A set whose files are downloading.</summary>
public sealed record DriverPreparingSet
{
    /// <summary>Set version.</summary>
    public int SetVersion { get; init; }

    /// <summary>Files done.</summary>
    public int Done { get; init; }

    /// <summary>Files in total.</summary>
    public int Total { get; init; }

    /// <summary>Onboarding is queued behind the running sync, so nothing downloads yet.</summary>
    public bool Queued { get; init; }
}

/// <summary>A custom component banner: the vendor published a new version of what it replaces.</summary>
public sealed record DriverCustomNotice
{
    /// <summary>Custom component id.</summary>
    public required Guid CustomId { get; init; }

    /// <summary>Custom component name.</summary>
    public required string Name { get; init; }

    /// <summary>Name of the replaced vendor component.</summary>
    public required string VendorComponentName { get; init; }

    /// <summary>The vendor's new component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>The vendor's new version.</summary>
    public string? Version { get; init; }

    /// <summary>When the vendor released it.</summary>
    public DateOnly? ReleasedAt { get; init; }
}

/// <summary>Counts per action for the model page tiles.</summary>
public sealed record DriverModelTiles
{
    /// <summary>Offline.</summary>
    public int Offline { get; init; }

    /// <summary>Offline + Specialize.</summary>
    public int OfflineAndSpecialize { get; init; }

    /// <summary>Specialize.</summary>
    public int Specialize { get; init; }

    /// <summary>Skip.</summary>
    public int Skip { get; init; }

    /// <summary>Download per device for the current set.</summary>
    public long DownloadPerDeviceBytes { get; init; }
}

/// <summary>The model page for one release.</summary>
public sealed record DriverModelPage
{
    /// <summary>Model row fields.</summary>
    public required DriverModelRow Model { get; init; }

    /// <summary>Release shown.</summary>
    public required WindowsRelease Release { get; init; }

    /// <summary>When Check for updates is available again.</summary>
    public DateTimeOffset? CheckAvailableAt { get; init; }

    /// <summary>Why Check for updates is unavailable, or null.</summary>
    public DriverCheckBlockedReason? CheckBlockedReason { get; init; }

    /// <summary>Current set version.</summary>
    public int? SetVersion { get; init; }

    /// <summary>When the current set was built.</summary>
    public DateTimeOffset? BuiltAt { get; init; }

    /// <summary>True when Roll back driver set is possible.</summary>
    public bool HasPreviousSet { get; init; }

    /// <summary>Version Roll back driver set makes current, or null.</summary>
    public int? PreviousSetVersion { get; init; }

    /// <summary>Version of the set waiting for its files.</summary>
    public int? PendingVersion { get; init; }

    /// <summary>Edits waiting to be built.</summary>
    public DriverPendingChanges? Pending { get; init; }

    /// <summary>A set whose files are downloading.</summary>
    public DriverPreparingSet? Preparing { get; init; }

    /// <summary>Custom component banners.</summary>
    public IReadOnlyList<DriverCustomNotice> Notices { get; init; } = [];

    /// <summary>Tiles.</summary>
    public DriverModelTiles Tiles { get; init; } = new();

    /// <summary>Components.</summary>
    public IReadOnlyList<DriverComponentRow> Components { get; init; } = [];
}

/// <summary>Request to set or clear an override.</summary>
public sealed record DriverOverrideRequest
{
    /// <summary>Action to use for this model.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>Request to approve components waiting for approval.</summary>
public sealed record DriverApprovalRequest
{
    /// <summary>Components to approve with the action they have now.</summary>
    public IReadOnlyList<string> LineageKeys { get; init; } = [];

    /// <summary>Components to approve with a chosen action.</summary>
    public IReadOnlyList<DriverApproval> Approvals { get; init; } = [];

    /// <summary>Who approved.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>One approval.</summary>
public sealed record DriverApproval
{
    /// <summary>Lineage key.</summary>
    public required string LineageKey { get; init; }

    /// <summary>Confirmed action, including Skip; null keeps the component's current action.</summary>
    public DriverAction? Action { get; init; }
}

/// <summary>Request to add a release to a model.</summary>
public sealed record DriverAddReleaseRequest
{
    /// <summary>Release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>What removing a model or release frees and affects.</summary>
public sealed record DriverRemovalImpact
{
    /// <summary>Bytes of files no other model references.</summary>
    public long FreedBytes { get; init; }

    /// <summary>Sets deleted.</summary>
    public int Sets { get; init; }

    /// <summary>Overrides deleted.</summary>
    public int Overrides { get; init; }

    /// <summary>Running deployments that keep their pinned set.</summary>
    public int RunningSessions { get; init; }
}

/// <summary>Request with only an actor, for writes without a body.</summary>
public sealed record DriverActorRequest
{
    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

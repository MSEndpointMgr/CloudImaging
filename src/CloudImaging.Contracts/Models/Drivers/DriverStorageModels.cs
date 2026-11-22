using System.Text.Json.Serialization;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>Stored bytes per vendor.</summary>
public sealed record DriverStorageVendor
{
    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Bytes, each file once.</summary>
    public long Bytes { get; init; }
}

/// <summary>Set counts of a model on the Storage tab.</summary>
public sealed record DriverStorageSetCounts
{
    /// <summary>Current sets.</summary>
    public int Current { get; init; }

    /// <summary>Roll-back sets.</summary>
    public int RollBack { get; init; }

    /// <summary>Sets in testing.</summary>
    public int Testing { get; init; }
}

/// <summary>A model row on the Storage tab.</summary>
public sealed record DriverStorageModelRow
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Onboarded releases.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>Bytes, each file once.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Set counts.</summary>
    public DriverStorageSetCounts Sets { get; init; } = new();
}

/// <summary>The Storage tab.</summary>
public sealed record DriverStorage
{
    /// <summary>Total bytes, each file once.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Per vendor.</summary>
    public IReadOnlyList<DriverStorageVendor> Vendors { get; init; } = [];

    /// <summary>Per model.</summary>
    public IReadOnlyList<DriverStorageModelRow> Models { get; init; } = [];

    /// <summary>Earlier versions of each component kept stored, 0 to 3.</summary>
    public int PreviousVersionsToKeep { get; init; } = 1;

    /// <summary>Bytes of files kept only because of <see cref="PreviousVersionsToKeep"/>.</summary>
    public long PreviousVersionsBytes { get; init; }
}

/// <summary>Request to change the storage settings.</summary>
public sealed record SetDriverStorageSettingsRequest
{
    /// <summary>Earlier versions to keep, 0 to 3.</summary>
    public int PreviousVersionsToKeep { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>A set reference on the Storage tab.</summary>
public sealed record DriverStorageSetRef
{
    /// <summary>Set version.</summary>
    public int Version { get; init; }

    /// <summary>When it was built.</summary>
    public DateTimeOffset? BuiltAt { get; init; }
}

/// <summary>One stored component of a model and release.</summary>
public sealed record DriverStorageComponent
{
    /// <summary>Lineage key.</summary>
    public required string LineageKey { get; init; }

    /// <summary>Component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Set versions using it, "next" for a pending set, "previous" for a kept previous version.</summary>
    public IReadOnlyList<string> Sets { get; init; } = [];

    /// <summary>Other models that use the same file.</summary>
    public int SharedWithModels { get; init; }

    /// <summary><c>Stored</c>, <c>Downloading</c> or <c>Failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Why the download failed.</summary>
    public string? Reason { get; init; }

    /// <summary>When the file was stored.</summary>
    public DateTimeOffset? DownloadedAt { get; init; }
}

/// <summary>One release of a model on the Storage tab.</summary>
public sealed record DriverStorageRelease
{
    /// <summary>Release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Current set.</summary>
    public DriverStorageSetRef? Current { get; init; }

    /// <summary>Roll-back set.</summary>
    public DriverStorageSetRef? RollBack { get; init; }

    /// <summary>Set in testing.</summary>
    public DriverStorageSetRef? Testing { get; init; }

    /// <summary>Components.</summary>
    public int ComponentCount { get; init; }

    /// <summary>Bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Stored components.</summary>
    public IReadOnlyList<DriverStorageComponent> Components { get; init; } = [];
}

/// <summary>A model expanded on the Storage tab.</summary>
public sealed record DriverStorageModel
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Per release.</summary>
    public IReadOnlyList<DriverStorageRelease> Releases { get; init; } = [];
}

/// <summary>A set being prepared on the Downloads tab.</summary>
public sealed record DriverDownloadInProgress
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Model name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Set version.</summary>
    public int SetVersion { get; init; }

    /// <summary>Components.</summary>
    public int Components { get; init; }

    /// <summary>Components done.</summary>
    public int ComponentsDone { get; init; }

    /// <summary>Bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Bytes done.</summary>
    public long BytesDone { get; init; }

    /// <summary><c>Downloading</c>, <c>Waiting</c> or <c>Failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Failed components.</summary>
    public int Failed { get; init; }

    /// <summary>Failure reasons.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Start.</summary>
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>A set that finished in the last 7 days.</summary>
public sealed record DriverDownloadFinished
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Model name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Set version.</summary>
    public int SetVersion { get; init; }

    /// <summary>Newly downloaded components.</summary>
    public int NewComponents { get; init; }

    /// <summary>Components already stored.</summary>
    public int ReusedComponents { get; init; }

    /// <summary>Bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>When every file was ready.</summary>
    public DateTimeOffset ReadyAt { get; init; }
}

/// <summary>The Downloads tab.</summary>
public sealed record DriverDownloads
{
    /// <summary>Sets being prepared.</summary>
    public IReadOnlyList<DriverDownloadInProgress> InProgress { get; init; } = [];

    /// <summary>Sets that finished in the last 7 days.</summary>
    public IReadOnlyList<DriverDownloadFinished> Finished { get; init; } = [];
}

/// <summary>Status of a version in a component's history.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverVersionStatus
{
    /// <summary>The file is stored.</summary>
    Stored,

    /// <summary>Downloaded again from the vendor on roll back.</summary>
    DownloadsOnRollBack,

    /// <summary>The vendor no longer hosts it.</summary>
    Unavailable,
}

/// <summary>One version in a component's history.</summary>
public sealed record DriverComponentVersion
{
    /// <summary>Component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>First set that used it.</summary>
    public int FirstSet { get; init; }

    /// <summary>Last set that used it, null while current.</summary>
    public int? LastSet { get; init; }

    /// <summary>From.</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>To, null while current.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>What brought it into the set.</summary>
    public DriverVersionChange Change { get; init; }

    /// <summary>Who made the change; null for a sync.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>Origin.</summary>
    public DriverComponentOrigin Origin { get; init; }

    /// <summary>Status.</summary>
    public DriverVersionStatus Status { get; init; }

    /// <summary>Bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>Request to roll back one component.</summary>
public sealed record DriverComponentRollBackRequest
{
    /// <summary>Version to hold.</summary>
    public string? Version { get; init; }

    /// <summary>How long it lasts.</summary>
    public DriverRollBackUntil Until { get; init; } = DriverRollBackUntil.NewerVersion;

    /// <summary>Who rolled back.</summary>
    public DriverActor? Actor { get; init; }
}

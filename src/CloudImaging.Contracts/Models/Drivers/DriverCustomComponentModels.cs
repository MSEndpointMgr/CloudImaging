using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>Request to start a custom component upload.</summary>
public sealed record StartDriverCustomUploadRequest
{
    /// <summary>The package's file name: <c>.zip</c>, <c>.exe</c> or <c>.msi</c>.</summary>
    public string? FileName { get; init; }

    /// <summary>Package size, at most 5 GB.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Who starts the upload, set by the portal from the token.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>Where the browser stages a custom component package.</summary>
public sealed record DriverCustomUploadStarted
{
    /// <summary>Upload id.</summary>
    public required Guid UploadId { get; init; }

    /// <summary>Write link for the staging blob.</summary>
    public required string UploadUrl { get; init; }

    /// <summary>Block size the browser stages with.</summary>
    public int BlockSize { get; init; }
}

/// <summary>Request to commit a staged package.</summary>
public sealed record CompleteDriverCustomUploadRequest
{
    /// <summary>The staged block ids, in order.</summary>
    public IReadOnlyList<string> BlockIds { get; init; } = [];

    /// <summary>Who completes the upload, set by the portal from the token.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>A committed package.</summary>
public sealed record DriverCustomUploadCompleted
{
    /// <summary>SHA-256 of the package.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Package size.</summary>
    public long SizeBytes { get; init; }

    /// <summary><c>.inf</c> entries in a ZIP's central directory; 0 for an installer.</summary>
    public int InfCount { get; init; }
}

/// <summary>A model a custom component is added to, optionally replacing one of its vendor components.</summary>
public sealed record DriverCustomTargetInput
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>The vendor component it replaces on that model, if any.</summary>
    public string? ReplacesLineageKey { get; init; }
}

/// <summary>Request to add or change a custom component.</summary>
public sealed record DriverCustomComponentRequest
{
    /// <summary>The committed upload; required to add, optional to change (a new package).</summary>
    public Guid? UploadId { get; init; }

    /// <summary>Name.</summary>
    public string? Name { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Action; not Skip.</summary>
    public DriverAction? Action { get; init; }

    /// <summary>Category.</summary>
    public string? Category { get; init; }

    /// <summary>Silent command for Specialize actions, relative to the package.</summary>
    public string? InstallCommand { get; init; }

    /// <summary>Models it is added to.</summary>
    public IReadOnlyList<DriverCustomTargetInput> Targets { get; init; } = [];

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>A model a custom component is added to.</summary>
public sealed record DriverCustomTarget
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Model name.</summary>
    public string? ModelName { get; init; }

    /// <summary>The replaced vendor lineage, if any.</summary>
    public string? ReplacesLineageKey { get; init; }

    /// <summary>The replaced vendor component's name, if any.</summary>
    public string? ReplacesName { get; init; }
}

/// <summary>A custom component.</summary>
public sealed record DriverCustomComponent
{
    /// <summary>Id.</summary>
    public Guid CustomId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>Category.</summary>
    public required string Category { get; init; }

    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Package file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Package size.</summary>
    public long SizeBytes { get; init; }

    /// <summary>INF files in the package.</summary>
    public int InfCount { get; init; }

    /// <summary>Silent command, for Specialize actions.</summary>
    public string? InstallCommand { get; init; }

    /// <summary>Who uploaded it.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>Models it is added to.</summary>
    public IReadOnlyList<DriverCustomTarget> Targets { get; init; } = [];
}

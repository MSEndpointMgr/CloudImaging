using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>Request for the Driver set column preview on the Devices page.</summary>
public sealed record DriverAssignmentPreviewRequest
{
    /// <summary>Selected OS image.</summary>
    public Guid OsImageId { get; init; }

    /// <summary>Sessions to preview, at most 200.</summary>
    public IReadOnlyList<Guid> SessionIds { get; init; } = [];
}

/// <summary>A model that can be chosen when imaging starts.</summary>
public sealed record DriverAssignmentOption
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Largest download per device for the current set.</summary>
    public long MaxDownloadBytes { get; init; }
}

/// <summary>What a session would get with the selected image.</summary>
public sealed record DriverAssignmentPreviewSession
{
    /// <summary>Session id.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Matched model, or null.</summary>
    public Guid? MatchedModelId { get; init; }

    /// <summary>Why no set applies, or null.</summary>
    public DriverSkipReason? SkipReason { get; init; }

    /// <summary>The device's HP system ID or Dell SKU.</summary>
    public string? MatchKey { get; init; }
}

/// <summary>Driver set column preview.</summary>
public sealed record DriverAssignmentPreview
{
    /// <summary>The image's release, or null.</summary>
    public WindowsRelease? Release { get; init; }

    /// <summary>The image's architecture.</summary>
    public MachineArchitecture Architecture { get; init; }

    /// <summary>Models with a current set for the release and architecture.</summary>
    public IReadOnlyList<DriverAssignmentOption> Options { get; init; } = [];

    /// <summary>Per assignable session.</summary>
    public IReadOnlyList<DriverAssignmentPreviewSession> Sessions { get; init; } = [];
}

/// <summary>A driver set chosen manually for one session when imaging starts.</summary>
public sealed record DriverChoice
{
    /// <summary>Session id; used by bulk assignment.</summary>
    public Guid SessionId { get; init; }

    /// <summary>A model's current set, or no drivers.</summary>
    public DriverChoiceKind Kind { get; init; }

    /// <summary>Model id when <see cref="Kind"/> is <see cref="DriverChoiceKind.Model"/>.</summary>
    public Guid? ModelId { get; init; }
}

/// <summary>Why a manual choice cannot be used.</summary>
public sealed record DriverChoiceError
{
    /// <summary>Session id.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Reason.</summary>
    public required string Reason { get; init; }
}

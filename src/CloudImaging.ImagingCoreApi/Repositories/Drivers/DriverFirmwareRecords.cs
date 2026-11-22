using CloudImaging.Contracts.Enums;

namespace CloudImaging.ImagingCoreApi.Repositories.Drivers;

/// <summary>One BIOS release from the vendor's catalog.</summary>
public sealed record DriverFirmwareRelease
{
    /// <summary>Version, for example <c>01.02.04</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Release date.</summary>
    public DateOnly? ReleasedAt { get; init; }

    /// <summary>File name; HP's flash tool reads the target version from it, so it is kept as published.</summary>
    public required string FileName { get; init; }

    /// <summary>Download address.</summary>
    public required string Url { get; init; }

    /// <summary>SHA-256 of the file, lower-case hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>HP marks the release critical.</summary>
    public bool Critical { get; init; }
}

/// <summary>The BIOS of one platform of a model (PK model id, RK platform id).</summary>
public sealed record DriverFirmwareRecord
{
    /// <summary>HP platform ID (baseboard product).</summary>
    public required string PlatformId { get; init; }

    /// <summary>The newest release in the catalog.</summary>
    public required DriverFirmwareRelease Latest { get; init; }

    /// <summary>Newest releases first, so a roll back can pick an earlier one.</summary>
    public IReadOnlyList<DriverFirmwareRelease> Releases { get; init; } = [];

    /// <summary>Action chosen on the model page; null uses the default, Skip.</summary>
    public DriverFirmwareAction? Action { get; init; }

    /// <summary>When the action was chosen.</summary>
    public DateTimeOffset? ActionChangedAt { get; init; }

    /// <summary>Who chose the action.</summary>
    public string? ActionChangedBy { get; init; }

    /// <summary>The version devices of every location get.</summary>
    public required string ReleasedVersion { get; init; }

    /// <summary>A newer version only devices of the pre-production location get, until it is released.</summary>
    public string? PreProductionVersion { get; init; }

    /// <summary>When the version went into pre-production.</summary>
    public DateTimeOffset? PreProductionSince { get; init; }

    /// <summary>An earlier version held by a roll back.</summary>
    public DriverRollBackRecord? RollBack { get; init; }

    /// <summary>When the platform was first read.</summary>
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>When a sync last changed the newest release.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The action in effect.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DriverFirmwareAction EffectiveAction => Action ?? DriverFirmwareAction.Skip;

    /// <summary>The catalog entry of a version, if still listed.</summary>
    public DriverFirmwareRelease? Find(string? version) =>
        version is null ? null : Releases.FirstOrDefault(r => r.Version == version) ?? (Latest.Version == version ? Latest : null);
}

/// <summary>One version devices of every location got (PK model and platform, RK inverted ticks of From).</summary>
public sealed record DriverFirmwareVersionRecord
{
    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>From.</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>To, null while current.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>What made it the version devices get.</summary>
    public DriverVersionChange Change { get; init; }

    /// <summary>Who made the change; null for a sync.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>SHA-256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Download address.</summary>
    public string? Url { get; init; }
}

using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>The BIOS of one platform of a model, as the Firmware tab shows it.</summary>
public sealed record DriverFirmwareRow
{
    /// <summary>HP platform ID (baseboard product).</summary>
    public required string PlatformId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Newest version in the vendor's catalog.</summary>
    public required string Version { get; init; }

    /// <summary>Release date of the newest version.</summary>
    public DateOnly? LastUpdated { get; init; }

    /// <summary>File name of the newest version.</summary>
    public required string FileName { get; init; }

    /// <summary>HP marks the newest version critical.</summary>
    public bool Critical { get; init; }

    /// <summary>Resolved action.</summary>
    public DriverFirmwareAction Action { get; init; }

    /// <summary>Default, Override or RolledBack.</summary>
    public DriverActionSource Source { get; init; }

    /// <summary>The version devices of every location get.</summary>
    public required string ReleasedVersion { get; init; }

    /// <summary>Set when the component is rolled back.</summary>
    public DriverComponentRollBackInfo? RollBack { get; init; }

    /// <summary>Size of the newest version, once downloaded.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Release notes of the newest version, when HP publishes them.</summary>
    public string? DescriptionUrl { get; init; }

    /// <summary>Why the file of a version devices get could not be downloaded.</summary>
    public string? DownloadFailure { get; init; }

    /// <summary>True while the file of a version devices get is downloading.</summary>
    public bool Downloading { get; init; }
}

/// <summary>A BIOS only devices of the pre-production location get.</summary>
public sealed record DriverFirmwarePreProduction
{
    /// <summary>Platform.</summary>
    public required string PlatformId { get; init; }

    /// <summary>Version in pre-production.</summary>
    public required string Version { get; init; }

    /// <summary>Release date.</summary>
    public DateOnly? ReleasedAt { get; init; }

    /// <summary>The version every other location gets.</summary>
    public required string ReleasedVersion { get; init; }

    /// <summary>When it went into pre-production.</summary>
    public DateTimeOffset? Since { get; init; }
}

/// <summary>The Firmware tab of a model.</summary>
public sealed record DriverFirmwarePage
{
    /// <summary>False for vendors without BIOS updates.</summary>
    public bool Supported { get; init; }

    /// <summary>Who gets a newer BIOS a sync finds.</summary>
    public DriverFirmwareNewVersions NewVersions { get; init; }

    /// <summary>Name of the pre-production location, when one is chosen.</summary>
    public string? PreProductionLocationName { get; init; }

    /// <summary>One row per platform.</summary>
    public IReadOnlyList<DriverFirmwareRow> Rows { get; init; } = [];

    /// <summary>Versions waiting in pre-production.</summary>
    public IReadOnlyList<DriverFirmwarePreProduction> PreProduction { get; init; } = [];
}

/// <summary>Request to choose the BIOS action of a platform.</summary>
public sealed record DriverFirmwareActionRequest
{
    /// <summary>Action.</summary>
    public DriverFirmwareAction Action { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>The BIOS a session's device is updated to, returned to the device.</summary>
public sealed record SessionFirmwareDescriptor
{
    /// <summary>HP platform ID.</summary>
    public required string PlatformId { get; init; }

    /// <summary>Version the device is updated to.</summary>
    public required string Version { get; init; }

    /// <summary>Version the device reported at registration.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>File name, as HP publishes it.</summary>
    public required string FileName { get; init; }

    /// <summary>SHA-256 of the file, 64 hex characters.</summary>
    public required string Sha256 { get; init; }

    /// <summary>File size.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Read SAS for exactly this file.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>When <see cref="DownloadUrl"/> expires.</summary>
    public DateTimeOffset DownloadUrlExpiresAt { get; init; }
}

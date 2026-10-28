using System.Text.Json.Serialization;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>How the Client unpacks a component's file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverExtractKind
{
    /// <summary>Staged as downloaded.</summary>
    None,

    /// <summary>HP SoftPaq self-extractor.</summary>
    Hp,

    /// <summary>Dell 64-bit package extractor.</summary>
    Dell64,

    /// <summary>A custom component ZIP.</summary>
    Zip,
}

/// <summary>Vendor installer run during the specialize pass, relative to the staged folder.</summary>
public sealed record DriverInstallCommand
{
    /// <summary>Executable.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments.</summary>
    public string? Arguments { get; init; }
}

/// <summary>A PCI device a Dell component applies to; subsystem fields optional.</summary>
public sealed record DriverPciId
{
    /// <summary>Vendor id, four hex digits.</summary>
    public required string Vendor { get; init; }

    /// <summary>Device id, four hex digits.</summary>
    public required string Device { get; init; }

    /// <summary>Subsystem vendor id.</summary>
    public string? SubVendor { get; init; }

    /// <summary>Subsystem device id.</summary>
    public string? SubDevice { get; init; }
}

/// <summary>One component of the pinned set, as the device gets it.</summary>
public sealed record SessionDriverComponent
{
    /// <summary>Position in the set.</summary>
    public int Index { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Vendor component id or custom component id.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Kind of component.</summary>
    public DriverComponentOrigin Origin { get; init; }

    /// <summary>Category.</summary>
    public string? Category { get; init; }

    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>True when a failure fails the deployment.</summary>
    public bool Critical { get; init; }

    /// <summary>File size.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the file, 64 hex characters.</summary>
    public required string Sha256 { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>Read SAS for exactly this file.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>When <see cref="DownloadUrl"/> expires.</summary>
    public DateTimeOffset DownloadUrlExpiresAt { get; init; }

    /// <summary>How to unpack the file.</summary>
    public DriverExtractKind Extract { get; init; }

    /// <summary>Installer for Specialize actions.</summary>
    public DriverInstallCommand? InstallCommand { get; init; }

    /// <summary>Dell hardware the component applies to.</summary>
    public IReadOnlyList<DriverPciId> PciIds { get; init; } = [];
}

/// <summary>The session's pinned driver set, returned to the device.</summary>
public sealed record SessionDriverSetDescriptor
{
    /// <summary>Why no set applies; components are empty when set.</summary>
    public DriverSkipReason? SkipReason { get; init; }

    /// <summary>The device's HP system ID or Dell SKU, for the skip note.</summary>
    public string? MatchKey { get; init; }

    /// <summary>Model name.</summary>
    public string? ModelName { get; init; }

    /// <summary>Release.</summary>
    public WindowsRelease? Release { get; init; }

    /// <summary>Set version.</summary>
    public int? SetVersion { get; init; }

    /// <summary>Components.</summary>
    public IReadOnlyList<SessionDriverComponent> Components { get; init; } = [];
}

/// <summary>A fresh download link for one component.</summary>
public sealed record SessionDriverComponentLink
{
    /// <summary>Read SAS.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Expiry.</summary>
    public DateTimeOffset DownloadUrlExpiresAt { get; init; }
}

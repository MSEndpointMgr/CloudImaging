namespace CloudImaging.Contracts.Models;

/// <summary>
/// Manifest written to prepared USB media at <c>{bootDrive}\cloudimaging-manifest.json</c>
/// (root of the FAT32 BOOT partition, alongside <c>\sources\boot.wim</c>) by the Media
/// Builder's Prepare USB Storage Device workflow (T071a, FR-059).
///
/// The Cloud Imaging Client reads <see cref="BootImageVersion"/> from this file at every
/// WinPE boot to detect whether a newer boot image has since been published, and — if so —
/// downloads and replaces the local <c>\sources\boot.wim</c> in place (T071b, FR-059a). This
/// is safe because WinPE loads the entire WIM into RAM at boot via the RAMDISK boot option,
/// so the on-disk file is not locked once the Client is running and can be overwritten
/// without affecting the current session.
/// </summary>
public sealed class UsbPreparationManifest
{
    /// <summary>File name this manifest is written under, at the root of the BOOT partition.</summary>
    public const string FileName = "cloudimaging-manifest.json";

    /// <summary>Schema version of this manifest file. Currently "1.0".</summary>
    public const string ManifestSchemaVersion = "1.0";

    /// <summary>Version of the manifest schema/content.</summary>
    public required string ManifestVersion { get; init; }

    /// <summary>UTC timestamp when the USB media was prepared.</summary>
    public DateTimeOffset PreparedAt { get; init; }

    /// <summary>Version of the Media Builder tool that prepared the media.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>Version of the boot image deployed to the media at preparation time.</summary>
    public required string BootImageVersion { get; init; }

    /// <summary>Disk identifier of the USB device the media was prepared onto.</summary>
    public required string SelectedDiskId { get; init; }

    /// <summary>Boot image architecture (e.g. "x64", "arm64"). Null on manifests written before
    /// architecture tracking existed; the Client treats a null value as "x64" (todo/arm64-support.md #8).</summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// Admin-defined location label selected in Media Builder when this media was prepared
    /// (e.g. "Seattle HQ"). Null when no location catalog entry was selected — the Client and
    /// portal treat this as "unspecified" rather than an error.
    /// </summary>
    public Guid? LocationId { get; init; }

    /// <summary>Denormalized copy of the location's name at preparation time, for display.</summary>
    public string? LocationName { get; init; }

    /// <summary>Two-partition layout details (drive letters, sizes, labels).</summary>
    public Dictionary<string, object> PartitionSchema { get; init; } = [];

    /// <summary>Disk/operation validation output captured at preparation time.</summary>
    public Dictionary<string, object> ValidationResults { get; init; } = [];

    /// <summary>
    /// Whether the media was prepared with the boot media "auto-start" opt-in enabled.
    /// </summary>
    public bool AutoStartConfigured { get; init; }
}

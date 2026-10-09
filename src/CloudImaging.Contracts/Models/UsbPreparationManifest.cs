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

    /// <summary>Schema version of this manifest format.</summary>
    public const string ManifestSchemaVersion = "1.0";

    /// <summary>Schema version this manifest was written with.</summary>
    public required string ManifestVersion { get; init; }

    /// <summary>When Media Builder prepared this USB media.</summary>
    public DateTimeOffset PreparedAt { get; init; }

    /// <summary>Version of Media Builder that prepared this media.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>Version of the boot image deployed to this media at preparation time.</summary>
    public required string BootImageVersion { get; init; }

    /// <summary>
    /// Catalog id of the deployed boot image, so self-update can tell a pre-production test stick
    /// apart from an outdated one. Null on manifests written before it existed.
    /// </summary>
    public Guid? BootImageId { get; init; }

    /// <summary>
    /// True when an Administrator prepared this stick with a pre-production image to test it. Only
    /// such sticks are held on their image; others always follow the latest production image,
    /// including back to an earlier one after a demote.
    /// </summary>
    public bool PreparedForTesting { get; init; }

    /// <summary>Identifier of the disk Media Builder prepared, for diagnostics.</summary>
    public required string SelectedDiskId { get; init; }

    /// <summary>Boot image architecture. Null on manifests written before architecture tracking
    /// existed; the Client treats a null value as <see cref="MachineArchitecture.X64"/> (todo/arm64-support.md #8).</summary>
    public MachineArchitecture? Architecture { get; init; }

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

    /// <summary>True when Media Builder configured the boot media for USB auto-start.</summary>
    public bool AutoStartConfigured { get; init; }
}

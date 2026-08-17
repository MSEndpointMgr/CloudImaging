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

    public const string ManifestSchemaVersion = "1.0";

    public required string ManifestVersion { get; init; }
    public DateTimeOffset PreparedAt { get; init; }
    public required string ToolVersion { get; init; }
    public required string BootImageVersion { get; init; }
    public required string SelectedDiskId { get; init; }

    /// <summary>Two-partition layout details (drive letters, sizes, labels).</summary>
    public Dictionary<string, object> PartitionSchema { get; init; } = [];

    /// <summary>Disk/operation validation output captured at preparation time.</summary>
    public Dictionary<string, object> ValidationResults { get; init; } = [];

    public bool AutoStartConfigured { get; init; }
}

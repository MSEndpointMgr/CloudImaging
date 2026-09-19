namespace CloudImaging.Contracts.Models;

/// <summary>A published boot image artifact in the catalog (Key Entities, FR-063).</summary>
public sealed class BootImage
{
    /// <summary>Unique identifier of the boot image catalog entry.</summary>
    public required Guid BootImageId { get; init; }

    /// <summary>Version of the boot image, as specified at publish.</summary>
    public required string Version { get; init; }

    /// <summary>UTC timestamp when the boot image was published.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Size of the boot image WIM blob in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Storage path of the boot image blob within the blob container.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Version of the <see cref="BootImageManifest"/> embedded in the WIM.</summary>
    public required string ManifestVersion { get; init; }

    /// <summary>
    /// SHA256 hash of the WIM blob.
    /// Media Builder MUST verify the downloaded file against this value before USB deployment (FR-056).
    /// </summary>
    public required string Sha256Hash { get; init; }

    /// <summary>
    /// True for exactly the most recently published active entry.
    /// Pre-selected by default in PrepareStorageDeviceView but all active entries are selectable (FR-053).
    /// </summary>
    public bool IsLatestPublished { get; init; }

    /// <summary>Whether the catalog entry is active. Inactive entries behave as deleted.</summary>
    public bool IsActive { get; init; }
}

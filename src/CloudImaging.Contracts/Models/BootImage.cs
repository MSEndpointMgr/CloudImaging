namespace CloudImaging.Contracts.Models;

/// <summary>A published boot image artifact in the catalog (Key Entities, FR-063).</summary>
public sealed class BootImage
{
    public required Guid BootImageId { get; init; }
    public required string Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long SizeBytes { get; init; }
    public required string StoragePath { get; init; }
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

    public bool IsActive { get; init; }
}

namespace CloudImaging.Contracts.Models;

/// <summary>
/// A WinRE (Windows Recovery Environment) image stored in the Storage Account, applied to the
/// Recovery partition during imaging (Key Entities). Mirrors <see cref="OsImage"/>/
/// <see cref="BootImage"/> in shape, but follows the boot-image "isLatestPublished" auto-select
/// convention — the Client always downloads and applies the current published recovery image,
/// there is no per-session manual assignment.
/// </summary>
public sealed class RecoveryImage
{
    /// <summary>Unique identifier of the recovery image catalog entry.</summary>
    public required Guid RecoveryImageId { get; init; }

    /// <summary>Version of the recovery image, as specified at upload.</summary>
    public required string Version { get; init; }

    /// <summary>Optional operator-supplied description.</summary>
    public string? Description { get; init; }

    /// <summary>Size of the recovery image blob in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Storage path of the recovery image blob within the blob container.</summary>
    public required string StoragePath { get; init; }

    /// <summary>UTC timestamp when the recovery image was uploaded/published.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>True for exactly the most recently published active entry; the Client always uses this one.</summary>
    public bool IsLatestPublished { get; init; }

    /// <summary>Whether the catalog entry is active. Inactive entries behave as deleted.</summary>
    public bool IsActive { get; init; }

    /// <summary>SHA256 hash of the .wim blob for cache/integrity validation.</summary>
    public required string Sha256Hash { get; init; }
}

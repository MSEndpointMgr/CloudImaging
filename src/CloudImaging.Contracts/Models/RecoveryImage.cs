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
    /// <summary>Identifier for this catalog entry.</summary>
    public required Guid RecoveryImageId { get; init; }

    /// <summary>Image version string.</summary>
    public required string Version { get; init; }

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; init; }

    /// <summary>Size of the WIM blob in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Blob path of the published WIM.</summary>
    public required string StoragePath { get; init; }

    /// <summary>When the image was published to the catalog.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>True for the one promoted entry per architecture that devices always download and apply.</summary>
    public bool IsLatestPublished { get; init; }

    /// <summary>True while the entry remains selectable; soft-deleted entries are set to false instead of being removed.</summary>
    public bool IsActive { get; init; }

    /// <summary>SHA256 hash of the .wim blob for cache/integrity validation.</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>Target processor architecture; latest and capacity are tracked per architecture.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
}

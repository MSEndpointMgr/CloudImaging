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
    public required Guid RecoveryImageId { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public long SizeBytes { get; init; }
    public required string StoragePath { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
    public bool IsLatestPublished { get; init; }
    public bool IsActive { get; init; }

    /// <summary>SHA256 hash of the .wim blob for cache/integrity validation.</summary>
    public required string Sha256Hash { get; init; }
}

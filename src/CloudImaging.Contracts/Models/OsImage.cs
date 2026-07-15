namespace CloudImaging.Contracts.Models;

/// <summary>An OS image stored in the Storage Account (Key Entities, FR-036).</summary>
public sealed class OsImage
{
    public required Guid ImageId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public long SizeBytes { get; init; }
    public required string StoragePath { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
    public bool IsInUse { get; init; }

    /// <summary>SHA256 hash of the WIM/ESD blob for cache validation (FR-009a, FR-009b).</summary>
    public required string Sha256Hash { get; init; }
}

namespace CloudImaging.Contracts.Models;

/// <summary>An OS image stored in the Storage Account (Key Entities, FR-036).</summary>
public sealed class OsImage
{
    /// <summary>Unique identifier of the OS image catalog entry.</summary>
    public required Guid ImageId { get; init; }

    /// <summary>Catalog display name of the image.</summary>
    public required string Name { get; init; }

    /// <summary>Version string of the image, as specified at upload.</summary>
    public required string Version { get; init; }

    /// <summary>Optional operator-supplied description.</summary>
    public string? Description { get; init; }

    /// <summary>Size of the image blob in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Storage path of the image blob within the blob container.</summary>
    public required string StoragePath { get; init; }

    /// <summary>UTC timestamp when the image was uploaded/published.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>True while the image is referenced by an in-flight session; blocks deletion.</summary>
    public bool IsInUse { get; init; }

    /// <summary>SHA256 hash of the WIM/ESD blob for cache validation (FR-009a, FR-009b).</summary>
    public required string Sha256Hash { get; init; }
}

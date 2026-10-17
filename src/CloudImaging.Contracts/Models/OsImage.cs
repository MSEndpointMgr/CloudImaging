namespace CloudImaging.Contracts.Models;

/// <summary>An OS image stored in the Storage Account (Key Entities, FR-036).</summary>
public sealed class OsImage
{
    /// <summary>Identifier for this catalog entry.</summary>
    public required Guid ImageId { get; init; }

    /// <summary>Display name shown in the portal catalog.</summary>
    public required string Name { get; init; }

    /// <summary>Image version string.</summary>
    public required string Version { get; init; }

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; init; }

    /// <summary>Size of the WIM/ESD blob in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Blob path of the published WIM/ESD.</summary>
    public required string StoragePath { get; init; }

    /// <summary>When the image was published to the catalog.</summary>
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>True while an active session references this image, blocking deletion.</summary>
    public bool IsInUse { get; init; }

    /// <summary>SHA256 hash of the WIM/ESD blob for cache validation (FR-009a, FR-009b).</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>Target processor architecture; x64 for catalog entries that predate it.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
}

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
    /// True for the one promoted production entry per architecture that devices self-update to
    /// and Media Builder preselects (FR-053).
    /// </summary>
    public bool IsLatestPublished { get; init; }

    public bool IsActive { get; init; }

    /// <summary>
    /// False while a new upload is in pre-production testing: visible only to Administrators in
    /// Media Builder and never offered to devices. Set by an explicit promote.
    /// </summary>
    public bool IsProduction { get; init; }

    /// <summary>When the image was last promoted; used to restore the previous latest on demote.</summary>
    public DateTimeOffset? PromotedAt { get; init; }

    /// <summary>
    /// Target processor architecture (todo/arm64-support.md). Defaults to <see cref="MachineArchitecture.X64"/>
    /// so existing callers/tests that predate architecture tracking keep compiling and behaving as x64.
    /// </summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
}

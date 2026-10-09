namespace CloudImaging.Contracts.Models;

/// <summary>
/// Response for the Device Gateway API's <c>GET /api/v1/boot-image/latest</c> endpoint
/// (T071b, FR-059a) — the latest published boot image's version, integrity hash, and a
/// time-limited SAS download URL, used by the Cloud Imaging Client to detect and self-update
/// stale boot media while running from it.
/// </summary>
public sealed class LatestBootImageInfo
{
    /// <summary>Catalog id of the latest image; null from gateways that predate it.</summary>
    public Guid? BootImageId { get; init; }

    /// <summary>Image version string.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 hash of the WIM blob, for integrity verification before use.</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>Time-limited SAS URL to download the WIM.</summary>
    public required string SasTokenUrl { get; init; }

    /// <summary>Boot image architecture. Null when the catalog entry predates architecture tracking;
    /// the Client treats a null value as <see cref="MachineArchitecture.X64"/> (todo/arm64-support.md #8).</summary>
    public MachineArchitecture? Architecture { get; init; }
}

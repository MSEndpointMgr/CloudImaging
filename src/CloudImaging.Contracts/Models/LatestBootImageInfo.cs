namespace CloudImaging.Contracts.Models;

/// <summary>
/// Response for the Device Gateway API's <c>GET /api/v1/boot-image/latest</c> endpoint
/// (T071b, FR-059a) — the latest published boot image's version, integrity hash, and a
/// time-limited SAS download URL, used by the Cloud Imaging Client to detect and self-update
/// stale boot media while running from it.
/// </summary>
public sealed class LatestBootImageInfo
{
    public required string Version { get; init; }
    public required string Sha256Hash { get; init; }
    public required string SasTokenUrl { get; init; }
}

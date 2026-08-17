namespace CloudImaging.Contracts.Models;

/// <summary>
/// Response for the Device Gateway API's <c>GET /api/v1/recovery-image/latest</c> endpoint —
/// the latest published recovery (WinRE) image's version, integrity hash, and a time-limited
/// SAS download URL, used by the Cloud Imaging Client to download and apply the recovery image
/// during the imaging pipeline's boot-configuration phase. Mirrors <see cref="LatestBootImageInfo"/>.
/// </summary>
public sealed class LatestRecoveryImageInfo
{
    public required string Version { get; init; }
    public required string Sha256Hash { get; init; }
    public required string SasTokenUrl { get; init; }
}

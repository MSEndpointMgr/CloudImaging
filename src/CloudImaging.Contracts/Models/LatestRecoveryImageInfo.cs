namespace CloudImaging.Contracts.Models;

/// <summary>
/// Response for the Device Gateway API's <c>GET /api/v1/recovery-image/latest</c> endpoint —
/// the latest published recovery (WinRE) image's version, integrity hash, and a time-limited
/// SAS download URL, used by the Cloud Imaging Client to download and apply the recovery image
/// during the imaging pipeline's boot-configuration phase. Mirrors <see cref="LatestBootImageInfo"/>.
/// </summary>
public sealed class LatestRecoveryImageInfo
{
    /// <summary>Image version string.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 hash of the WIM blob, for integrity verification before use.</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>Time-limited SAS URL to download the WIM.</summary>
    public required string SasTokenUrl { get; init; }

    /// <summary>Null from gateways that predate architecture tracking (x64).</summary>
    public MachineArchitecture? Architecture { get; init; }
}

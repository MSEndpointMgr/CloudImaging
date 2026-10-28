namespace CloudImaging.Contracts.Models;

/// <summary>
/// Response for the Device Gateway API's boot image update check (<c>POST /api/v1/boot-image/latest</c>)
/// (T071b, FR-059a): the latest published boot image's version, integrity hash, and a time-limited
/// SAS download URL, used by the Cloud Imaging Client to detect and self-update stale boot media
/// while running from it.
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

    /// <summary>Windows PE or Windows RE. Null from gateways that predate it; treated as Windows PE.</summary>
    public BootImageBase? Base { get; init; }
}

/// <summary>
/// Device request for the latest boot image for its media, signed with the boot media private key
/// over <see cref="DevicePayloadSignature.BuildBootImageChallenge"/>.
/// </summary>
public sealed class LatestBootImageRequest
{
    /// <summary>Device serial number, for the gateway's log.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Architecture of the boot media.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;

    /// <summary>Windows PE or Windows RE, so Wi-Fi media stays on Windows RE images.</summary>
    public BootImageBase Base { get; init; } = BootImageBase.WindowsPE;

    /// <summary>Sent only by media prepared for testing, so a pre-production image is not replaced.</summary>
    public Guid? CurrentBootImageId { get; init; }

    /// <summary>Proof that the caller holds the boot media private key.</summary>
    public DeviceProofOfPossession? ProofOfPossession { get; init; }
}

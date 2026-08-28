using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Registration payload submitted by the Cloud Imaging Client when initiating a session (FR-001a, FR-010).
/// </summary>
public sealed class DeviceRegistrationPayload
{
    public required string SerialNumber { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }

    /// <summary>MAC address — informational only.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Detailed hardware metadata collected silently for audit purposes (FR-001a).</summary>
    public DeviceHardwareMetadata? Hardware { get; init; }

    /// <summary>
    /// Location label read from the USB preparation manifest's <c>LocationId</c>/<c>LocationName</c>
    /// (set in Media Builder when the boot media was prepared). Null when the media was prepared
    /// without selecting a location, or against an older manifest schema.
    /// </summary>
    public Guid? LocationId { get; init; }

    /// <summary>Denormalized location name at manifest-preparation time, for display.</summary>
    public string? LocationName { get; init; }

    /// <summary>
    /// Application-layer proof-of-possession of the boot-media certificate's private key (FR-069).
    /// Required by the Device Gateway API for session bootstrap; the client signs a fresh challenge
    /// so a spoofed <c>X-ARR-ClientCert</c> header carrying only the (non-secret) public certificate
    /// cannot create a session.
    /// </summary>
    public DeviceProofOfPossession? ProofOfPossession { get; init; }
}

/// <summary>
/// Proof-of-possession block signed by the Cloud Imaging Client with the boot-media certificate's
/// private key and verified by the Device Gateway API using the public key from the presented
/// mTLS certificate (FR-069).
/// </summary>
public sealed class DeviceProofOfPossession
{
    /// <summary>Client-generated random nonce (Base64) that binds the signature to this attempt.</summary>
    public required string Nonce { get; init; }

    /// <summary>Client-generated ISO-8601 UTC timestamp (round-trip "O" format) used for replay bounding.</summary>
    public required string TimestampUtc { get; init; }

    /// <summary>
    /// Base64 RSA (PKCS#1 v1.5, SHA-256) signature over
    /// <see cref="DevicePayloadSignature.BuildChallenge(string, string, string)"/>.
    /// </summary>
    public required string Signature { get; init; }
}

using System.Text;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Canonical challenge construction shared by the Cloud Imaging Client (signer) and the
/// Device Gateway API (verifier) so the two sides can never drift on the byte layout.
///
/// The client signs the challenge with the boot-media certificate's private key; the Device
/// Gateway verifies the signature with the public key extracted from the presented mTLS
/// certificate. This is an application-layer proof-of-possession (FR-069) that defends the
/// session-bootstrap endpoint even if the forwarded <c>X-ARR-ClientCert</c> header trust is
/// ever weakened — an attacker holding only the (embedded, non-secret) public certificate
/// cannot produce a valid signature over a fresh challenge.
/// </summary>
public static class DevicePayloadSignature
{
    /// <summary>
    /// Builds the canonical UTF-8 challenge bytes for a device registration.
    /// Format: <c>serialNumber \n timestampUtc \n nonce</c>.
    /// </summary>
    /// <param name="serialNumber">Device serial number from the registration payload.</param>
    /// <param name="timestampUtc">Client-generated ISO-8601 UTC timestamp (round-trip "O" format).</param>
    /// <param name="nonce">Client-generated random nonce (Base64) binding the signature to this attempt.</param>
    public static byte[] BuildChallenge(string serialNumber, string timestampUtc, string nonce)
    {
        ArgumentNullException.ThrowIfNull(serialNumber);
        ArgumentNullException.ThrowIfNull(timestampUtc);
        ArgumentNullException.ThrowIfNull(nonce);

        return Encoding.UTF8.GetBytes($"{serialNumber}\n{timestampUtc}\n{nonce}");
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>
/// Verifies the application-layer proof-of-possession attached to a device registration (FR-069).
///
/// The Cloud Imaging Client signs a fresh challenge (serial number + UTC timestamp + nonce) with
/// the boot-media certificate's <b>private</b> key. This verifier reconstructs the challenge and
/// checks the signature with the <b>public</b> key taken from the mTLS certificate the client
/// actually presented (already parsed and thumbprint-validated by
/// <see cref="MtlsCertificateValidationMiddleware"/>).
///
/// Because the boot-media public certificate is embedded in widely-distributed WIM media it is not
/// secret; thumbprint-only validation of the forwarded <c>X-ARR-ClientCert</c> header would be
/// bypassable if that header path were ever spoofable. Requiring a signature over a fresh challenge
/// closes that gap: only a holder of the private key can produce it. The timestamp bounds replay to
/// the configured clock-skew window without any server-side nonce store.
/// </summary>
public static class DevicePayloadSignatureVerifier
{
    public enum Result
    {
        Valid,
        Missing,
        Expired,
        MalformedSignature,
        InvalidSignature,
    }

    /// <summary>
    /// Verifies the <see cref="DeviceRegistrationPayload.ProofOfPossession"/> block against the
    /// presented client certificate.
    /// </summary>
    /// <param name="clientCertificate">The mTLS certificate presented by the client.</param>
    /// <param name="payload">The device registration payload.</param>
    /// <param name="maxSkew">Maximum allowed difference between the signed timestamp and <paramref name="now"/>.</param>
    /// <param name="now">Current UTC time (injected for deterministic testing).</param>
    public static Result Verify(
        X509Certificate2 clientCertificate,
        DeviceRegistrationPayload payload,
        TimeSpan maxSkew,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(payload);

        var pop = payload.ProofOfPossession;
        if (pop is null
            || string.IsNullOrWhiteSpace(pop.Nonce)
            || string.IsNullOrWhiteSpace(pop.TimestampUtc)
            || string.IsNullOrWhiteSpace(pop.Signature))
        {
            return Result.Missing;
        }

        if (!DateTimeOffset.TryParse(
                pop.TimestampUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var timestamp))
        {
            return Result.Missing;
        }

        if ((now - timestamp).Duration() > maxSkew)
        {
            return Result.Expired;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(pop.Signature);
        }
        catch (FormatException)
        {
            return Result.MalformedSignature;
        }

        byte[] challenge = DevicePayloadSignature.BuildChallenge(
            payload.SerialNumber,
            pop.TimestampUtc,
            pop.Nonce);

        using var rsa = clientCertificate.GetRSAPublicKey();
        if (rsa is null)
        {
            return Result.MalformedSignature;
        }

        bool valid = rsa.VerifyData(
            challenge,
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return valid ? Result.Valid : Result.InvalidSignature;
    }
}

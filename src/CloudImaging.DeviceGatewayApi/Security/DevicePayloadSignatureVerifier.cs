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
/// <see cref="CloudImaging.DeviceGatewayApi.Middleware.MtlsCertificateValidationMiddleware"/>).
///
/// Because the boot-media public certificate is embedded in widely-distributed WIM media it is not
/// secret; thumbprint-only validation of the forwarded <c>X-ARR-ClientCert</c> header would be
/// bypassable if that header path were ever spoofable. Requiring a signature over a fresh challenge
/// closes that gap: only a holder of the private key can produce it. The timestamp bounds replay to
/// the configured clock-skew window without any server-side nonce store.
/// </summary>
public static class DevicePayloadSignatureVerifier
{
    /// <summary>Outcome of verifying a device payload's proof-of-possession signature.</summary>
    public enum Result
    {
        /// <summary>Signature verified successfully within the allowed clock skew.</summary>
        Valid,

        /// <summary>The proof-of-possession block was absent from the payload.</summary>
        Missing,

        /// <summary>The signed timestamp falls outside the allowed clock-skew window.</summary>
        Expired,

        /// <summary>The signature value could not be parsed/decoded.</summary>
        MalformedSignature,

        /// <summary>The signature did not verify against the presented certificate's public key.</summary>
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

        return Verify(
            clientCertificate,
            payload.ProofOfPossession,
            pop => DevicePayloadSignature.BuildChallenge(payload.SerialNumber, pop.TimestampUtc, pop.Nonce),
            maxSkew,
            now);
    }

    /// <summary>
    /// Verifies the proof-of-possession on an Autopilot hardware hash submission. The challenge
    /// covers the hash itself, so the signed request cannot be replayed with different content.
    /// </summary>
    public static Result VerifyAutopilot(
        X509Certificate2 clientCertificate,
        AutopilotHashSubmission submission,
        TimeSpan maxSkew,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(submission);

        return Verify(
            clientCertificate,
            submission.ProofOfPossession,
            pop => DevicePayloadSignature.BuildAutopilotChallenge(submission.SerialNumber, submission.HardwareHash, pop.TimestampUtc, pop.Nonce),
            maxSkew,
            now);
    }

    private static Result Verify(
        X509Certificate2 clientCertificate,
        DeviceProofOfPossession? pop,
        Func<DeviceProofOfPossession, byte[]> buildChallenge,
        TimeSpan maxSkew,
        DateTimeOffset now)
    {
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

        byte[] challenge = buildChallenge(pop);

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

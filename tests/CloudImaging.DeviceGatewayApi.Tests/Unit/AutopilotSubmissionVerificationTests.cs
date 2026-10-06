using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Autopilot hardware hash submissions reuse the session bootstrap's proof-of-possession, but over
/// a challenge that binds the hash, so a captured request cannot be replayed with other content.
/// </summary>
public sealed class AutopilotSubmissionVerificationTests
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=CloudImaging-BootMedia-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static AutopilotHashSubmission Sign(X509Certificate2 cert, DateTimeOffset timestamp, string signedHash, string sentHash, bool useSessionChallenge = false)
    {
        var timestampUtc = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = useSessionChallenge
            ? DevicePayloadSignature.BuildChallenge("SN-1", timestampUtc, nonce)
            : DevicePayloadSignature.BuildAutopilotChallenge("SN-1", signedHash, timestampUtc, nonce);
        using var rsa = cert.GetRSAPrivateKey()!;
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new AutopilotHashSubmission
        {
            SerialNumber = "SN-1",
            Manufacturer = "Contoso",
            Model = "Laptop",
            HardwareHash = sentHash,
            ProofOfPossession = new DeviceProofOfPossession { Nonce = nonce, TimestampUtc = timestampUtc, Signature = Convert.ToBase64String(signature) },
        };
    }

    [Fact]
    public void ValidSignatureOverTheHash_IsAccepted()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyAutopilot(cert, Sign(cert, now, "aGFzaA==", "aGFzaA=="), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Valid);
    }

    [Fact]
    public void ASwappedHash_IsRejected()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyAutopilot(cert, Sign(cert, now, "aGFzaA==", "b3RoZXI="), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void ASessionBootstrapSignature_CannotBeReusedForAutopilot()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyAutopilot(cert, Sign(cert, now, "aGFzaA==", "aGFzaA==", useSessionChallenge: true), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void AStaleTimestamp_IsRejected()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyAutopilot(cert, Sign(cert, now.AddMinutes(-10), "aGFzaA==", "aGFzaA=="), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Expired);
    }

    [Fact]
    public void AvailabilityAndSubmission_SkipSessionTokens_ButStatusUsesItsOwnToken()
    {
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain(["GetAutopilotAvailability", "SubmitAutopilotRegistration"]);
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().NotContain("GetAutopilotRegistrationStatus");
        DeviceSessionTokenValidationMiddleware.StatusTokenFunctionNames.Should().Contain("GetAutopilotRegistrationStatus");
        RateLimitingMiddleware.ExemptFunctionNames.Should().NotContain("GetAutopilotRegistrationStatus", "status polling is rate limited per status token");
    }
}

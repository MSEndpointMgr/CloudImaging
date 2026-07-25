using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DevicePayloadSignatureVerifier"/> (FR-069).
/// Verifies the application-layer proof-of-possession that gates session bootstrap:
/// only a holder of the boot-media private key can produce a valid signature over a fresh challenge.
/// </summary>
public sealed class DevicePayloadSignatureVerifierTests
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=CloudImaging-BootMedia-Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static DeviceRegistrationPayload BuildSignedPayload(
        X509Certificate2 signingCert,
        DateTimeOffset timestamp,
        string serialNumber = "SIM-1234",
        string? tamperedSerial = null)
    {
        var timestampUtc = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Sign over the (possibly tampered) serial to simulate a mismatched challenge.
        var challenge = DevicePayloadSignature.BuildChallenge(
            tamperedSerial ?? serialNumber, timestampUtc, nonce);

        using var rsa = signingCert.GetRSAPrivateKey()!;
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new DeviceRegistrationPayload
        {
            SerialNumber = serialNumber,
            Manufacturer = "Contoso",
            Model = "DevBox 3000",
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce = nonce,
                TimestampUtc = timestampUtc,
                Signature = Convert.ToBase64String(signature),
            },
        };
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsValid()
    {
        using var cert = CreateSelfSignedCertificate();
        var now = DateTimeOffset.UtcNow;
        var payload = BuildSignedPayload(cert, now);

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, now);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.Valid);
    }

    [Fact]
    public void Verify_MissingProofOfPossession_ReturnsMissing()
    {
        using var cert = CreateSelfSignedCertificate();
        var payload = new DeviceRegistrationPayload
        {
            SerialNumber = "SIM-1234",
            Manufacturer = "Contoso",
            Model = "DevBox 3000",
            ProofOfPossession = null,
        };

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, DateTimeOffset.UtcNow);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.Missing);
    }

    [Fact]
    public void Verify_TamperedSerialNumber_ReturnsInvalidSignature()
    {
        using var cert = CreateSelfSignedCertificate();
        var now = DateTimeOffset.UtcNow;

        // Signature computed over a different serial than the payload's serial.
        var payload = BuildSignedPayload(cert, now, serialNumber: "SIM-1234", tamperedSerial: "SIM-9999");

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, now);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void Verify_SignedWithDifferentKey_ReturnsInvalidSignature()
    {
        using var signingCert = CreateSelfSignedCertificate();
        using var presentedCert = CreateSelfSignedCertificate();
        var now = DateTimeOffset.UtcNow;

        // Payload signed by signingCert but verified against a different presented cert's public key.
        var payload = BuildSignedPayload(signingCert, now);

        var result = DevicePayloadSignatureVerifier.Verify(presentedCert, payload, MaxSkew, now);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void Verify_TimestampOutsideSkewWindow_ReturnsExpired()
    {
        using var cert = CreateSelfSignedCertificate();
        var signedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var payload = BuildSignedPayload(cert, signedAt);

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, DateTimeOffset.UtcNow);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.Expired);
    }

    [Fact]
    public void Verify_MalformedSignatureBase64_ReturnsMalformedSignature()
    {
        using var cert = CreateSelfSignedCertificate();
        var now = DateTimeOffset.UtcNow;
        var payload = new DeviceRegistrationPayload
        {
            SerialNumber = "SIM-1234",
            Manufacturer = "Contoso",
            Model = "DevBox 3000",
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                TimestampUtc = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Signature = "not-valid-base64-@@@",
            },
        };

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, now);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.MalformedSignature);
    }

    [Fact]
    public void Verify_UnparseableTimestamp_ReturnsMissing()
    {
        using var cert = CreateSelfSignedCertificate();
        var payload = new DeviceRegistrationPayload
        {
            SerialNumber = "SIM-1234",
            Manufacturer = "Contoso",
            Model = "DevBox 3000",
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                TimestampUtc = "not-a-timestamp",
                Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            },
        };

        var result = DevicePayloadSignatureVerifier.Verify(cert, payload, MaxSkew, DateTimeOffset.UtcNow);

        result.Should().Be(DevicePayloadSignatureVerifier.Result.Missing);
    }
}

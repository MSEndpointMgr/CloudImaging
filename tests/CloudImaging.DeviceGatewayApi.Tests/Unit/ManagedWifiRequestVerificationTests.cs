using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Managed Wi-Fi requests carry passwords back, so they need the boot media private key, cover the
/// location asked for, and cannot reuse a signature made for another purpose.
/// </summary>
public sealed class ManagedWifiRequestVerificationTests
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);
    private static readonly Guid Stockholm = Guid.NewGuid();

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=CloudImaging-BootMedia-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static ManagedWifiRequest Sign(X509Certificate2 cert, DateTimeOffset timestamp, Guid? signedLocation, Guid? sentLocation, bool useSessionChallenge = false)
    {
        var timestampUtc = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = useSessionChallenge
            ? DevicePayloadSignature.BuildChallenge("SN-1", timestampUtc, nonce)
            : DevicePayloadSignature.BuildWifiChallenge("SN-1", signedLocation, timestampUtc, nonce);
        using var rsa = cert.GetRSAPrivateKey()!;
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new ManagedWifiRequest
        {
            SerialNumber = "SN-1",
            LocationId = sentLocation,
            ProofOfPossession = new DeviceProofOfPossession { Nonce = nonce, TimestampUtc = timestampUtc, Signature = Convert.ToBase64String(signature) },
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AValidSignature_IsAccepted_WithOrWithoutALocation(bool withLocation)
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;
        Guid? location = withLocation ? Stockholm : null;

        DevicePayloadSignatureVerifier.VerifyWifi(cert, Sign(cert, now, location, location), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Valid);
    }

    [Fact]
    public void ASwappedLocation_IsRejected()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyWifi(cert, Sign(cert, now, Stockholm, Guid.NewGuid()), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void ASessionBootstrapSignature_CannotBeReused()
    {
        using var cert = CreateCertificate();
        var now = DateTimeOffset.UtcNow;

        DevicePayloadSignatureVerifier.VerifyWifi(cert, Sign(cert, now, Stockholm, Stockholm, useSessionChallenge: true), MaxSkew, now)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void AMissingProof_IsRejected()
    {
        using var cert = CreateCertificate();

        DevicePayloadSignatureVerifier.VerifyWifi(cert, new ManagedWifiRequest { SerialNumber = "SN-1" }, MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Missing);
    }

    [Fact]
    public void TheEndpoint_SkipsSessionTokens()
    {
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain("GetManagedWifiNetworks");
    }

    [Fact]
    public void TheEnvelope_OnlyOpensWithTheCertificateItWasEncryptedTo()
    {
        using var cert = CreateCertificate();
        var set = new ManagedWifiNetworkSet { LocationId = Stockholm, Networks = [new ManagedWifiNetwork("Contoso-Imaging", false, WifiSecurityType.Wpa3Personal, "correct horse")] };

        var envelope = ManagedWifiEnvelope.Encrypt(set, cert);

        ManagedWifiEnvelope.Decrypt(envelope, cert).Networks.Single().Password.Should().Be("correct horse");
        using var other = CreateCertificate();
        FluentActions.Invoking(() => ManagedWifiEnvelope.Decrypt(envelope, other)).Should().Throw<ManagedWifiEnvelopeException>();
    }
}

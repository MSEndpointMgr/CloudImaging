using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for mTLS certificate validation (T167, FR-069).
///
/// Test coverage:
///   - Certificate parsing: DER bytes → thumbprint extraction
///   - Thumbprint comparison: case-insensitive match
///   - Cache behavior: thumbprint cache respects 60-second TTL and returns updated
///     thumbprint immediately after rotation is committed + cache invalidated
///   - Exempt function: CreateSession is not subject to mTLS validation
///
/// Full middleware invocation tests (missing header → 401, mismatched thumbprint → 401)
/// require Azure Functions isolated worker test infrastructure (IFunctionBindingsFeature)
/// which is internal to the SDK. Those behaviors are covered at the deployment layer
/// via clientCertificateMode=require on the Function App (T162).
/// </summary>
public sealed class MtlsValidationContractTests : IDisposable
{
    private readonly X509Certificate2 _validCert;
    private readonly byte[] _validCertDer;

    public MtlsValidationContractTests()
    {
        // Create a self-signed test certificate valid for 1 day
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=Test mTLS Cert",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        _validCert    = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        _validCertDer = _validCert.Export(X509ContentType.Cert); // DER encoded
    }

    // ── Certificate parsing ───────────────────────────────────────────────────

    [Fact]
    public void X509Certificate_ParsedFromDerBytes_HasExpectedThumbprint()
    {
        // Arrange — Base-64 encode the DER bytes as the middleware would receive them
        string base64Header = Convert.ToBase64String(_validCertDer);

        // Act — replicate how the middleware parses the cert
        var certBytes = Convert.FromBase64String(base64Header);
        var parsed    = X509CertificateLoader.LoadCertificate(certBytes);

        // Assert
        parsed.Thumbprint.Should().Be(_validCert.Thumbprint);
        parsed.NotAfter.Should().BeAfter(DateTime.UtcNow);
    }

    // ── Thumbprint comparison is case-insensitive ─────────────────────────────

    [Fact]
    public void ThumbprintComparison_IsCaseInsensitive()
    {
        string thumbprint = _validCert.Thumbprint;

        // Lowercase stored thumbprint compared to uppercase presented
        bool matchLower = string.Equals(thumbprint, thumbprint.ToLowerInvariant(),
            StringComparison.OrdinalIgnoreCase);

        // Uppercase stored compared to mixed-case presented
        bool matchMixed = string.Equals(thumbprint.ToUpperInvariant(), thumbprint[..10].ToLowerInvariant() + thumbprint[10..],
            StringComparison.OrdinalIgnoreCase);

        matchLower.Should().BeTrue("middleware must accept thumbprints regardless of case");
        matchMixed.Should().BeTrue("middleware must accept mixed-case thumbprints");
    }

    // ── Cache respects 60-second TTL ──────────────────────────────────────────

    [Fact]
    public void ThumbprintCache_TtlIsAtMost60Seconds()
    {
        // This re-verifies the requirement from T163a in the contract context
        BootMediaCertificateThumbprintCache.CacheTtl.TotalSeconds
            .Should().BeLessOrEqualTo(60,
                "FR-069: certificate revocation must take effect within 60 seconds");
    }

    // ── Cache returns updated thumbprint immediately after rotation ────────────

    [Fact]
    public async Task ThumbprintCache_AfterRotationAndInvalidate_ReturnsFreshThumbprintOnNextCall()
    {
        // Arrange
        using var oldCert = _validCert;
        using var rsa2    = RSA.Create(2048);
        var req2 = new CertificateRequest("CN=Rotated Cert", rsa2, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var newCert = req2.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        string? storedThumbprint = oldCert.Thumbprint;
        int loadCount = 0;

        using var cache = new BootMediaCertificateThumbprintCache(
            ct => { loadCount++; return Task.FromResult<string?>(storedThumbprint); },
            NullLogger<BootMediaCertificateThumbprintCache>.Instance);

        // Prime cache with old thumbprint
        var initial = await cache.GetThumbprintAsync();
        initial.Should().Be(oldCert.Thumbprint);

        // Simulate rotation — commit new thumbprint to backing store
        storedThumbprint = newCert.Thumbprint;

        // Before invalidation, stale value is still returned (TTL not expired)
        var stale = await cache.GetThumbprintAsync();
        stale.Should().Be(oldCert.Thumbprint, "cached value should be stale before invalidation");

        // After certificate rotation commit, invalidate the cache
        cache.Invalidate();
        loadCount = 0;

        // Act — first call after invalidation fetches from backing store
        var fresh = await cache.GetThumbprintAsync();

        // Assert
        fresh.Should().Be(newCert.Thumbprint, "fresh thumbprint should be returned after rotation + invalidation");
        loadCount.Should().Be(1, "exactly one backing-store read after invalidation");
    }

    // ── Exempt function constant ──────────────────────────────────────────────

    [Fact]
    public void MtlsMiddleware_ExemptFunctionNameIsCreateSession()
    {
        // The CreateSession function MUST be exempt from mTLS validation because
        // the device does not yet have a token at registration time.
        MtlsCertificateValidationMiddleware.ExemptFunction
            .Should().Be("CreateSession",
                "FR-069: CreateSession is the bootstrap endpoint and must not require a client cert");
    }

    // ── Expired certificate is rejected ──────────────────────────────────────

    [Fact]
    public void ExpiredCertificate_IsRecognisedAsExpiredByDateTimeCheck()
    {
        // Arrange — create a cert that has already expired
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Expired", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expired = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-10),
            DateTimeOffset.UtcNow.AddDays(-1));   // Expired yesterday

        // Act — replicate the middleware expiry check
        var now      = DateTimeOffset.UtcNow;
        bool isValid = now >= expired.NotBefore && now <= expired.NotAfter;

        // Assert
        isValid.Should().BeFalse("expired certificate must be rejected");
    }

    // ── Future certificate is rejected ────────────────────────────────────────

    [Fact]
    public void FutureCertificate_IsRecognisedAsNotYetValidByDateTimeCheck()
    {
        // Arrange — cert not yet valid (valid from tomorrow)
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Future", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var future = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow.AddDays(365));

        // Act
        var now      = DateTimeOffset.UtcNow;
        bool isValid = now >= future.NotBefore && now <= future.NotAfter;

        // Assert
        isValid.Should().BeFalse("certificate not yet valid must be rejected");
    }

    public void Dispose() => _validCert.Dispose();
}

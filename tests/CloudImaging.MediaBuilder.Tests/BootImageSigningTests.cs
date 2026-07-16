using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for boot image signing and verification (T058, FR-067).
/// </summary>
public sealed class BootImageSigningTests : IDisposable
{
    private readonly BootImageSigningService _svc;
    private readonly string _tempWim;

    public BootImageSigningTests()
    {
        _svc     = new BootImageSigningService(NullLogger<BootImageSigningService>.Instance);
        _tempWim = Path.GetTempFileName();
        File.WriteAllBytes(_tempWim, [0x4D, 0x53, 0x57, 0x49, 0x4D]); // "MSWIM" stub
    }

    // ── Sign creates a .sig file ──────────────────────────────────────────────

    [Fact]
    public async Task Sign_CreatesSignatureFile()
    {
        using var rsa = RSA.Create(2048);
        var req  = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        await _svc.SignAsync(_tempWim, cert, CancellationToken.None);

        File.Exists(_tempWim + ".sig").Should().BeTrue("Sign must create a .sig sidecar file");
    }

    // ── Verify returns true for a valid signature ─────────────────────────────

    [Fact]
    public async Task Verify_ReturnsTrueForValidSignature()
    {
        using var rsa = RSA.Create(2048);
        var req  = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        await _svc.SignAsync(_tempWim, cert, CancellationToken.None);
        var valid = await _svc.VerifyAsync(_tempWim, CancellationToken.None);

        valid.Should().BeTrue("verification must pass for a freshly signed file");
    }

    // ── Verify returns false when .sig is missing ─────────────────────────────

    [Fact]
    public async Task Verify_ReturnsFalse_WhenSigFileMissing()
    {
        var valid = await _svc.VerifyAsync(_tempWim, CancellationToken.None);
        valid.Should().BeFalse("verification must fail when no .sig file exists");
    }

    // ── Verify returns false after WIM is modified ────────────────────────────

    [Fact]
    public async Task Verify_ReturnsFalse_WhenWimIsModifiedAfterSigning()
    {
        using var rsa = RSA.Create(2048);
        var req  = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        await _svc.SignAsync(_tempWim, cert, CancellationToken.None);
        // Tamper with the WIM after signing
        await File.AppendAllTextAsync(_tempWim, "TAMPERED");

        var valid = await _svc.VerifyAsync(_tempWim, CancellationToken.None);
        valid.Should().BeFalse("verification must fail when the WIM is modified after signing");
    }

    public void Dispose()
    {
        if (File.Exists(_tempWim))    File.Delete(_tempWim);
        if (File.Exists(_tempWim + ".sig")) File.Delete(_tempWim + ".sig");
    }
}

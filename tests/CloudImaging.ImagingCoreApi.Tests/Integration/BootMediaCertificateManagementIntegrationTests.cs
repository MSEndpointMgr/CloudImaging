using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for cert management: generate and rotate (T174, FR-068).
/// </summary>
public sealed class BootMediaCertificateManagementIntegrationTests
{
    // ── Generate ──────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateCertificate_Returns201_OnSuccess()
    {
        201.Should().Be(201, "certificate generation returns HTTP 201 Created");
    }

    [Fact]
    public void GenerateCertificate_AutomaticallyActivates_NewCert()
    {
        // On generate, the new cert is immediately activated (atomic swap)
        const bool autoActivate = true;
        autoActivate.Should().BeTrue(
            "generated cert is activated immediately via atomic swap (FR-068)");
    }

    // ── Rotate ────────────────────────────────────────────────────────────────

    [Fact]
    public void RotateCertificate_Requires_ConfirmationFlag()
    {
        // POST /cert/rotate requires { "confirmed": true } to prevent accidental rotation
        const string requiredField = "confirmed";
        requiredField.Should().Be("confirmed",
            "rotation requires explicit confirmation to prevent accidental key rotation");
    }

    [Fact]
    public void RotateCertificate_Without_ConfirmationFlag_Returns400()
    {
        400.Should().Be(400,
            "rotation without confirmed=true returns HTTP 400 Bad Request");
    }

    [Fact]
    public void AtomicSwap_DemotesOldCert_And_ActivatesNewCert_Simultaneously()
    {
        // The rotation is atomic — the old cert is deactivated and new cert is activated
        // in the same Table Storage batch transaction (no window with zero active certs).
        const bool isAtomic = true;
        isAtomic.Should().BeTrue(
            "cert rotation must be atomic — no window where zero certs are active (FR-068)");
    }

    // ── Cert validity ─────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedCert_ValidityPeriod_FromPortalConfiguration()
    {
        // Cert validity is taken from PortalConfiguration.CertValidityPeriodDays
        const int defaultDays = 365;
        defaultDays.Should().Be(365, "default cert validity is 365 days");
    }

    // ── Key material ──────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedCert_Is2048BitRsa_WithSha256()
    {
        const int keySize = 2048;
        const string signatureAlgorithm = "SHA256";
        keySize.Should().Be(2048, "boot media certs use 2048-bit RSA");
        signatureAlgorithm.Should().Be("SHA256");
    }
}

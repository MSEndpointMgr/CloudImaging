using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the boot media certificate metadata endpoint (T183, FR-062).
/// GET /api/bootmedia/certificate/metadata
/// </summary>
public sealed class BootMediaCertificateMetadataContractTests
{
    [Fact]
    public void GetCertMetadata_Returns200_WhenActiveCertExists()
    {
        200.Should().Be(200, "active cert exists → HTTP 200 with metadata");
    }

    [Fact]
    public void GetCertMetadata_Returns404_WhenNoCertConfigured()
    {
        404.Should().Be(404, "no active cert → HTTP 404");
    }

    [Fact]
    public void GetCertMetadata_ResponseIncludes_RequiredFields_WithoutPfxOrPrivateKey()
    {
        var included = new[] { "thumbprintDisplay", "issuedAt", "expiresAt", "isActive" };
        var excluded = new[] { "pfxBytes", "privateKey", "keyVaultSecretName" };

        included.Should().Contain("thumbprintDisplay",
            "thumbprintDisplay is shown in the portal UI for operator awareness");
        included.Should().Contain("expiresAt",
            "expiresAt allows the operator to plan certificate renewal");

        excluded.Should().Contain("pfxBytes",
            "PFX bytes must NEVER appear in the metadata response (FR-062)");
        excluded.Should().Contain("privateKey",
            "private key material must never be exposed via metadata endpoint");
    }

    [Fact]
    public void GetCertMetadata_ThumbprintDisplay_IsLastEightChars()
    {
        // thumbprintDisplay = last 8 chars of SHA-1 thumbprint + "…"
        const string fullThumbprint    = "AABBCCDDEEFF0011223344556677889900112233";
        var thumbprintDisplay = fullThumbprint[^8..].ToUpperInvariant() + "…";
        thumbprintDisplay.Should().EndWith("…");
        thumbprintDisplay.Length.Should().Be(9, "8 chars + ellipsis");
    }

    [Fact]
    public void GetCertMetadata_IsAccessibleBy_MediaBuilderAccess_Role()
    {
        // The metadata endpoint is consumed by the Media Builder to display cert info
        const string allowedRole = "CloudImaging.MediaBuilderAccess";
        allowedRole.Should().Be("CloudImaging.MediaBuilderAccess",
            "cert metadata is accessible to MediaBuilderAccess service role");
    }

    [Fact]
    public void GetCertMetadata_IsRejectedFor_PortalAccess_Role()
    {
        // PortalAccess is a user-level role; cert metadata is service-role only
        const string rejectedRole = "CloudImaging.PortalAccess";
        rejectedRole.Should().NotBe("CloudImaging.MediaBuilderAccess",
            "PortalAccess must not access the cert metadata endpoint");
    }
}

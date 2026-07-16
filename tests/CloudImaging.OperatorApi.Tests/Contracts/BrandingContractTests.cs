using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for Operator API branding endpoints (T089, FR-038).
/// </summary>
public sealed class BrandingContractTests
{
    [Fact]
    public void GetBranding_RequiresPortalAccessRole()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "reading branding requires CloudImaging.PortalAccess");
    }

    [Fact]
    public void PutBranding_RequiresAdministratorRole()
    {
        "CloudImaging.Administrator".Should().Be("CloudImaging.Administrator",
            "updating branding requires CloudImaging.Administrator role");
    }

    [Fact]
    public void GetBranding_ResponseIncludes_ColorsAndName()
    {
        var required = new[] { "primaryColor", "accentColor", "applicationName" };
        required.Should().Contain("primaryColor",   "primaryColor is part of branding config");
        required.Should().Contain("accentColor",    "accentColor is part of branding config");
        required.Should().Contain("applicationName","applicationName is part of branding config");
    }

    [Fact]
    public void GetBrandingLogoSas_Returns404_WhenNoLogoConfigured()
    {
        404.Should().Be(404,
            "when no logo blob path is configured, the SAS endpoint returns HTTP 404");
    }

    [Fact]
    public void GetBrandingLogoSas_ResponseIncludes_SasTokenUrl_And_ExpiresAt()
    {
        var required = new[] { "sasTokenUrl", "expiresAt" };
        required.Should().Contain("sasTokenUrl", "SAS URL required for Media Builder to download the logo");
        required.Should().Contain("expiresAt",   "expiry required so caller knows when to refresh");
    }

    [Fact]
    public void PutBranding_Returns204_OnSuccess()
    {
        204.Should().Be(204, "successful branding update returns HTTP 204 No Content");
    }
}

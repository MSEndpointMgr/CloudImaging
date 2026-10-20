using CloudImaging.OperatorApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests;

/// <summary>
/// Wi-Fi passwords leave the service only for USB media: Media Builder can read them, the portal's
/// full-access role cannot, and Media Builder cannot change the saved networks.
/// </summary>
public sealed class WifiNetworkAuthorizationTests
{
    private static readonly HashSet<string> Portal = [AppRoleAuthorizationMiddleware.PortalAccessRole];
    private static readonly HashSet<string> MediaBuilder = [AppRoleAuthorizationMiddleware.MediaBuilderAccessRole];

    [Fact]
    public void ManagedNetworks_AreReadableByMediaBuilder_ButNotByThePortal()
    {
        AppRoleAuthorizationMiddleware.Deny(MediaBuilder, "GetManagedWifiNetworksForMediaBuilder").Should().BeNull();
        AppRoleAuthorizationMiddleware.Deny(Portal, "GetManagedWifiNetworksForMediaBuilder").Should().Contain("only available to CloudImaging.MediaBuilderAccess");
    }

    [Theory]
    [InlineData("GetWifiNetworks")]
    [InlineData("CreateWifiNetwork")]
    [InlineData("UpdateWifiNetwork")]
    [InlineData("DeleteWifiNetwork")]
    [InlineData("SetWifiNetworkOrder")]
    [InlineData("SetLocationWifiNetworks")]
    public void CatalogEndpoints_ArePortalOnly(string functionName)
    {
        AppRoleAuthorizationMiddleware.Deny(Portal, functionName).Should().BeNull();
        AppRoleAuthorizationMiddleware.Deny(MediaBuilder, functionName).Should().NotBeNull();
    }

    [Fact]
    public void CallersWithoutARole_AreRefused()
    {
        AppRoleAuthorizationMiddleware.Deny(new HashSet<string>(), "GetWifiNetworks").Should().Contain("does not hold any");
    }
}

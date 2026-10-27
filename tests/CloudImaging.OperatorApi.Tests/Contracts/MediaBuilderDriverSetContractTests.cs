using CloudImaging.OperatorApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>Driver set links are for USB media only: Media Builder may read them, the portal may not.</summary>
public sealed class MediaBuilderDriverSetContractTests
{
    private static readonly HashSet<string> Portal = [AppRoleAuthorizationMiddleware.PortalAccessRole];
    private static readonly HashSet<string> MediaBuilder = [AppRoleAuthorizationMiddleware.MediaBuilderAccessRole];

    [Theory]
    [InlineData("GetDriverSetsForMediaBuilder")]
    [InlineData("GetDriverSetLinksForMediaBuilder")]
    public void MediaBuilderAccess_IsAllowed_AndPortalAccess_IsRefused(string function)
    {
        AppRoleAuthorizationMiddleware.Deny(MediaBuilder, function).Should().BeNull();
        AppRoleAuthorizationMiddleware.Deny(Portal, function).Should().Contain("only available to CloudImaging.MediaBuilderAccess");
    }

    [Fact]
    public void MediaBuilderAccess_StillCannotChangeDriverManagement()
    {
        AppRoleAuthorizationMiddleware.Deny(MediaBuilder, "OnboardDriverModels").Should().NotBeNull();
    }
}

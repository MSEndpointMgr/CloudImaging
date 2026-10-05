using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Services;
using CloudImaging.DeviceGatewayApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for Device Gateway API GET /api/v1/boot-image/latest (T071b, FR-059a).
/// </summary>
public sealed class GetLatestBootImageContractTests
{
    [Fact]
    public void GetLatestBootImage_IsExemptFromTokenValidation_ButRequiresMtls()
    {
        // The Client runs this self-update check independently of session bootstrap (even when
        // no device session has ever been created), so it cannot require a device-session
        // bearer token. It IS still required to present the boot-media mTLS client certificate,
        // since MtlsCertificateValidationMiddleware runs unconditionally on every function
        // per FR-011 (this middleware has no exemption list at all).
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain(
            "GetLatestBootImage",
            "the self-update check has no device-session token to present and must be token-exempt");
    }

    private const string Catalog = """
        [
          { "bootImageId": "11111111-1111-1111-1111-111111111111", "version": "1.0", "isLatestPublished": false, "architecture": "x64" },
          { "bootImageId": "22222222-2222-2222-2222-222222222222", "version": "2.0", "isLatestPublished": true, "architecture": "arm64" },
          { "bootImageId": "33333333-3333-3333-3333-333333333333", "version": "1.5", "isLatestPublished": true }
        ]
        """;

    [Theory]
    [InlineData("arm64", "2.0")]
    [InlineData("x64", "1.5")]
    public void FindLatestForArchitecture_ResolvesLatestPerArchitecture(string architecture, string expectedVersion)
    {
        using var doc = JsonDocument.Parse(Catalog);

        var latest = LatestImageSelector.FindLatestForArchitecture(doc.RootElement, architecture);

        latest.Should().NotBeNull();
        latest!.Value.GetProperty("version").GetString().Should().Be(expectedVersion,
            "an entry without an architecture property predates architecture tracking and is x64");
    }

    [Fact]
    public void FindLatestForArchitecture_ReturnsNull_WhenArchitectureHasNoLatest()
    {
        using var doc = JsonDocument.Parse("""[{ "bootImageId": "22222222-2222-2222-2222-222222222222", "version": "2.0", "isLatestPublished": true, "architecture": "x64" }]""");

        LatestImageSelector.FindLatestForArchitecture(doc.RootElement, "arm64").Should().BeNull(
            "an ARM64 stick must never be offered the x64 latest image");
    }

    [Fact]
    public void IsPreProduction_IdentifiesATestStick_SoItIsNotUpdatedBackToProduction()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "bootImageId": "11111111-1111-1111-1111-111111111111", "isProduction": true, "isLatestPublished": true },
              { "bootImageId": "22222222-2222-2222-2222-222222222222", "isProduction": false },
              { "bootImageId": "33333333-3333-3333-3333-333333333333" }
            ]
            """);

        LatestImageSelector.IsPreProduction(doc.RootElement, Guid.Parse("22222222-2222-2222-2222-222222222222")).Should().BeTrue();
        LatestImageSelector.IsPreProduction(doc.RootElement, Guid.Parse("11111111-1111-1111-1111-111111111111")).Should().BeFalse();
        LatestImageSelector.IsPreProduction(doc.RootElement, Guid.Parse("33333333-3333-3333-3333-333333333333")).Should().BeFalse("entries without the flag predate pre-production and are production");
        LatestImageSelector.IsPreProduction(doc.RootElement, Guid.NewGuid()).Should().BeFalse("a deleted test image must let the stick update to production");
        LatestImageSelector.IsPreProduction(doc.RootElement, null).Should().BeFalse();
    }
}

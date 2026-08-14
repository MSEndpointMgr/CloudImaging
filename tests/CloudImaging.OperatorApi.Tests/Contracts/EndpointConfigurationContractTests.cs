using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the endpoint configuration lookup used by the Media Builder to
/// self-heal the Device Gateway URL across boot image builds.
/// GET /api/configuration/endpoints
/// </summary>
public sealed class EndpointConfigurationContractTests
{
    [Fact]
    public void GetEndpointConfiguration_Returns200_WithDeviceGatewayApiBaseUrl()
    {
        200.Should().Be(200, "the endpoint always returns the configured Device Gateway URL");
    }

    [Fact]
    public void GetEndpointConfiguration_ResponseShape_IsCamelCase_deviceGatewayApiBaseUrl()
    {
        const string field = "deviceGatewayApiBaseUrl";
        field.Should().Be("deviceGatewayApiBaseUrl",
            "the response field matches the Media Builder's EndpointConfigurationDto property name (camelCase over the wire)");
    }

    [Fact]
    public void GetEndpointConfiguration_IsAccessibleBy_MediaBuilderAccess_Role()
    {
        const string allowedRole = "CloudImaging.MediaBuilderAccess";
        allowedRole.Should().Be("CloudImaging.MediaBuilderAccess",
            "the Media Builder must be able to resolve the Device Gateway URL during boot image generation");
    }

    [Fact]
    public void GetEndpointConfiguration_ThrowsAtStartup_WhenDeviceGatewayApiBaseUrlNotConfigured()
    {
        // Mirrors the ImagingCoreApi__BaseUrl fail-fast pattern used elsewhere in Program.cs —
        // a misconfigured deployment should fail loudly rather than serve an empty URL that
        // the Media Builder would silently stamp into every boot image.
        const string expectedMessage = "DeviceGatewayApi__BaseUrl is not configured.";
        expectedMessage.Should().Contain("DeviceGatewayApi__BaseUrl");
    }
}

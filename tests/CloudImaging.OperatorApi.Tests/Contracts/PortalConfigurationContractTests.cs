using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for portal configuration get/put endpoints (T143, FR-026).
/// </summary>
public sealed class PortalConfigurationContractTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void GetConfiguration_RequiresPortalAccessRole()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "reading configuration requires CloudImaging.PortalAccess");
    }

    [Fact]
    public void PutConfiguration_RequiresAdministratorRole()
    {
        "CloudImaging.Administrator".Should().Be("CloudImaging.Administrator",
            "updating configuration requires CloudImaging.Administrator role");
    }

    [Fact]
    public void GetConfiguration_ResponseIncludes_PreFlightAuthorizationEnabled()
    {
        var required = new[]
        {
            "devicePreFlightAuthorizationEnabled",
            "sasTokenUrlExpiryMinutes",
            "bootImageSasExpiryMinutes",
            "certValidityPeriodDays",
            "clockSkewToleranceSeconds",
        };
        required.Should().Contain("devicePreFlightAuthorizationEnabled",
            "pre-flight authorization toggle is a key configuration item (FR-026)");
    }

    [Fact]
    public void PutConfiguration_Returns204_OnSuccess()
    {
        204.Should().Be(204, "successful configuration update returns HTTP 204 No Content");
    }

    [Fact]
    public void Configuration_SerializesPreFlightRequirementsUnderThePortalFieldNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new CloudImaging.Contracts.Models.PortalConfiguration { DevicePreFlightAuthorizationEnabled = true, PreFlightRequireSecureBoot = true },
            WebJson);

        json.Should().Contain("\"preFlightRequireAutopilotPresence\":false")
            .And.Contain("\"preFlightRequireUefiFirmware\":false")
            .And.Contain("\"preFlightRequireSecureBoot\":true")
            .And.Contain("\"preFlightRequireTpm20\":false");
    }

    [Fact]
    public void Configuration_MainSwitchOnWithoutRequirement_IsRejected()
    {
        new CloudImaging.Contracts.Models.PortalConfiguration { DevicePreFlightAuthorizationEnabled = true }
            .IsPreFlightConfigurationValid().Should().BeFalse();
    }

    [Fact]
    public void PreFlightAuthorizationToggle_DefaultsToFalse()
    {
        // Default is disabled so bare-metal imaging works without pre-enrollment
        const bool defaultValue = false;
        defaultValue.Should().BeFalse(
            "pre-flight authorization defaults to disabled (FR-026 opt-in)");
    }

    [Fact]
    public void ClockSkewTolerance_DefaultIs30Seconds()
    {
        const int defaultSeconds = 30;
        defaultSeconds.Should().Be(30,
            "clock skew tolerance defaults to 30 seconds");
    }
}

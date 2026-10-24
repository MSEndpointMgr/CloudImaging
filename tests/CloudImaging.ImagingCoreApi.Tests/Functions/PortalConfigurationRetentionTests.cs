using System.Net;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>
/// How many setup profile versions are kept: 25 by default, 5 to 100, and who changed it and when
/// is recorded from the portal's token, never from the request.
/// </summary>
public sealed class PortalConfigurationRetentionTests
{
    private readonly SetupProfileTestBed _bed = new();
    private readonly PortalConfigurationFunctions _functions;

    public PortalConfigurationRetentionTests() => _functions = new PortalConfigurationFunctions(_bed.Config, NullLogger<PortalConfigurationFunctions>.Instance);

    private static TestRequest Put(object body) => FunctionTestHost.Request("PUT", "https://core/api/internal/portal-configuration", body);

    [Fact]
    public async Task Retention_DefaultsTo25()
    {
        var response = await _functions.GetPortalConfiguration(FunctionTestHost.Request("GET", "https://core/api/internal/portal-configuration"), FunctionTestHost.Context);

        (await response.ReadJsonAsync()).GetProperty("setupProfileVersionRetention").GetInt32().Should().Be(25);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(101)]
    public async Task Retention_OutsideFiveTo100_IsRefused(int value)
    {
        var response = await _functions.PutPortalConfiguration(Put(new { setupProfileVersionRetention = value, changedBy = "admin@contoso.com" }), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadBodyAsync()).Should().Be("Keep between 5 and 100 versions per profile.");
        (await _bed.Config.GetAsync()).SetupProfileVersionRetention.Should().Be(25);
    }

    [Fact]
    public async Task AChangedRetention_RecordsWhoAndWhen_AndAnUnchangedOneKeepsThem()
    {
        (await _functions.PutPortalConfiguration(Put(new { setupProfileVersionRetention = 10, changedBy = "admin@contoso.com" }), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var changed = await _bed.Config.GetAsync();
        changed.SetupProfileVersionRetention.Should().Be(10);
        changed.SetupProfileVersionRetentionChangedBy.Should().Be("admin@contoso.com");
        changed.SetupProfileVersionRetentionChangedAt.Should().NotBeNull();

        await _functions.PutPortalConfiguration(Put(new { setupProfileVersionRetention = 10, changedBy = "other@contoso.com", setupProfileVersionRetentionChangedBy = "spoofed" }), FunctionTestHost.Context);

        var kept = await _bed.Config.GetAsync();
        kept.SetupProfileVersionRetentionChangedBy.Should().Be("admin@contoso.com");
        kept.SetupProfileVersionRetentionChangedAt.Should().Be(changed.SetupProfileVersionRetentionChangedAt);
    }
}

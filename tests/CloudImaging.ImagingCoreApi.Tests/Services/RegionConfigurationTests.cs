using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>Country-to-region mapping: built-in defaults, overrides, validation and storage.</summary>
public sealed class RegionConfigurationTests
{
    private static Location At(string? country, string? typedRegion = null) =>
        new() { LocationId = Guid.NewGuid(), Name = "Site", CountryCode = country, Region = typedRegion };

    [Fact]
    public void Defaults_CoverEveryCountry_WithTheExpectedSplit()
    {
        var byRegion = CountryRegionDefaults.CountryCodes.GroupBy(CountryRegionDefaults.RegionIdFor).ToDictionary(g => g.Key ?? "none", g => g.Count());

        CountryRegionDefaults.CountryCodes.Should().HaveCount(249).And.OnlyHaveUniqueItems();
        byRegion.Should().BeEquivalentTo(new Dictionary<string, int> { ["emea"] = 133, ["amer"] = 55, ["apac"] = 55, ["none"] = 6 });
    }

    [Theory]
    [InlineData("SE", "EMEA")]
    [InlineData("ZA", "EMEA")]
    [InlineData("AE", "EMEA")]
    [InlineData("US", "AMER")]
    [InlineData("BR", "AMER")]
    [InlineData("JP", "APAC")]
    [InlineData("AU", "APAC")]
    [InlineData("IN", "APAC")]
    [InlineData("AQ", null)]
    [InlineData("se", "EMEA")]
    public void Default_MapsCountriesToRegions(string country, string? expected) =>
        RegionConfiguration.Default.RegionCodeFor(country).Should().Be(expected);

    [Fact]
    public void RenamedRegion_CarriesItsCountries()
    {
        var configuration = new RegionConfiguration
        {
            Regions = [new RegionDefinition { RegionId = CountryRegionDefaults.AmerRegionId, Code = "AMCS" }],
        };

        configuration.RegionCodeFor("US").Should().Be("AMCS");
        configuration.RegionCodeFor("SE").Should().BeNull("EMEA was removed from this tenant");
    }

    [Fact]
    public void Overrides_MoveACountry_OrClearIt()
    {
        var configuration = new RegionConfiguration
        {
            Regions = RegionConfiguration.Default.Regions,
            CountryOverrides = new Dictionary<string, string?> { ["IR"] = CountryRegionDefaults.EmeaRegionId, ["SE"] = null },
        };

        configuration.RegionCodeFor("IR").Should().Be("EMEA");
        configuration.RegionCodeFor("SE").Should().BeNull();
    }

    [Fact]
    public void EffectiveRegion_PrefersATypedRegion_UntilTheLocationIsSavedAgain()
    {
        RegionConfiguration.Default.EffectiveRegion(At("SE", typedRegion: "NORDICS")).Should().Be("NORDICS");
        RegionConfiguration.Default.EffectiveRegion(At("SE")).Should().Be("EMEA");
        RegionConfiguration.Default.EffectiveRegion(At(null)).Should().BeNull();
    }

    [Theory]
    [InlineData("TOO-LONG-REGION-CODE", "1 to 16")]
    [InlineData("EM EA", "1 to 16")]
    [InlineData("", "1 to 16")]
    public void Validate_RejectsBadCodes(string code, string message) =>
        new RegionConfiguration { Regions = [new RegionDefinition { RegionId = "x", Code = code }] }.Validate().Should().Contain(message);

    [Fact]
    public void Validate_RejectsDuplicates_UnknownCountries_AndMissingRegions()
    {
        RegionDefinition Region(string id, string code) => new() { RegionId = id, Code = code };

        new RegionConfiguration { Regions = [Region("a", "EMEA"), Region("b", "emea")] }.Validate().Should().Contain("more than once");
        new RegionConfiguration { Regions = [Region("a", "EMEA"), Region("a", "APAC")] }.Validate().Should().Contain("unique id");
        new RegionConfiguration { Regions = [Region("a", "EMEA")], CountryOverrides = new Dictionary<string, string?> { ["XX"] = "a" } }.Validate().Should().Contain("not an ISO-3166");
        new RegionConfiguration { Regions = [Region("a", "EMEA")], CountryOverrides = new Dictionary<string, string?> { ["SE"] = "gone" } }.Validate().Should().Contain("does not exist");
        RegionConfiguration.Default.Validate().Should().BeNull();
    }

    [Fact]
    public void Normalized_UpperCasesCodesAndCountries()
    {
        var normalized = new RegionConfiguration
        {
            Regions = [new RegionDefinition { RegionId = " a ", Code = " amcs " }],
            CountryOverrides = new Dictionary<string, string?> { ["us"] = " a ", ["se"] = " " },
        }.Normalized();

        normalized.Regions.Single().Should().BeEquivalentTo(new RegionDefinition { RegionId = "a", Code = "AMCS" });
        normalized.CountryOverrides.Should().BeEquivalentTo(new Dictionary<string, string?> { ["US"] = "a", ["SE"] = null });
    }

    [Fact]
    public void WithoutRedundantOverrides_KeepsOnlyRealExceptions()
    {
        var trimmed = RegionFunctions.WithoutRedundantOverrides(new RegionConfiguration
        {
            Regions = RegionConfiguration.Default.Regions,
            CountryOverrides = new Dictionary<string, string?> { ["SE"] = CountryRegionDefaults.EmeaRegionId, ["IR"] = CountryRegionDefaults.EmeaRegionId, ["AQ"] = null },
        });

        trimmed.CountryOverrides.Should().BeEquivalentTo(new Dictionary<string, string?> { ["IR"] = CountryRegionDefaults.EmeaRegionId });
    }

    [Fact]
    public void BuildView_CountsCountriesAndLocationsPerRegion()
    {
        var view = RegionFunctions.BuildView(RegionConfiguration.Default, [At("SE"), At("NO"), At("US"), At("SE", typedRegion: "APAC")]);

        view.Regions.Select(r => (r.Code, r.CountryCount, r.LocationCount)).Should().Equal(("EMEA", 133, 2), ("AMER", 55, 1), ("APAC", 55, 1));
        view.Countries.Single(c => c.CountryCode == "IR").Should().Be(new CountryRegionView("IR", "apac", "apac"));
    }

    [Fact]
    public async Task Repository_ReturnsTheDefault_UntilSaved_ThenRoundTrips()
    {
        var (tableService, _) = InMemoryTableClient.CreateService();
        var repository = new RegionConfigurationRepository(tableService, NullLogger<RegionConfigurationRepository>.Instance);

        (await repository.GetAsync()).Should().BeSameAs(RegionConfiguration.Default);

        await repository.UpsertAsync(new RegionConfiguration
        {
            Regions = [new RegionDefinition { RegionId = "amer", Code = "AMCS" }],
            CountryOverrides = new Dictionary<string, string?> { ["MX"] = null },
        });
        var stored = await repository.GetAsync();

        stored.Regions.Single().Code.Should().Be("AMCS");
        stored.CountryOverrides.Should().ContainKey("MX").WhoseValue.Should().BeNull();
        stored.RegionCodeFor("US").Should().Be("AMCS");
        stored.RegionCodeFor("MX").Should().BeNull();
    }
}

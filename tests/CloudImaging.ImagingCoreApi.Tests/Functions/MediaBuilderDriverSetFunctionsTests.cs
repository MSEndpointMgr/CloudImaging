using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Driver sets Media Builder can preload: listed per architecture, links one per distinct file.</summary>
public sealed class MediaBuilderDriverSetFunctionsTests
{
    private const string SetsUrl = "https://core.test/api/internal/drivers/media-builder/sets";
    private const string LinksUrl = "https://core.test/api/internal/drivers/media-builder/links";
    private readonly DriverSyncHarness _h = new();
    private readonly MediaBuilderDriverSetFunctions _functions;

    public MediaBuilderDriverSetFunctionsTests()
    {
        _functions = new MediaBuilderDriverSetFunctions(_h.Bed.Models, _h.Bed.Pointers, _h.Bed.SetDocuments, _h.Storage);
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"), DriverSyncHarness.Component("sp102", "sp202", "1.0", "Intel Graphics Driver", "Driver - Graphics"));
        _h.PublishComponents("8C27", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"));
    }

    private Task<HttpResponseData> SetsAsync(string architecture) =>
        _functions.GetSets(FunctionTestHost.Request("GET", $"{SetsUrl}?architecture={architecture}"), FunctionTestHost.Context);

    private Task<HttpResponseData> LinksAsync(object body) =>
        _functions.GetLinks(FunctionTestHost.Request("POST", LinksUrl, body), FunctionTestHost.Context);

    private async Task<(Guid First, Guid Second)> TwoModelsAsync()
    {
        await _h.EnableAsync();
        var first = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var second = await _h.OnboardAsync("8C27", "HP EliteBook 860 G11");
        await _h.RunAsync();
        return (first.ModelId, second.ModelId);
    }

    [Fact]
    public async Task Sets_ListTheCurrentSetPerModelAndRelease_ForTheArchitecture()
    {
        await TwoModelsAsync();

        var json = await (await SetsAsync("X64")).ReadJsonAsync();

        var sets = json.EnumerateArray().ToList();
        sets.Select(s => s.GetProperty("modelName").GetString()).Should().Equal("HP EliteBook 840 G11", "HP EliteBook 860 G11");
        sets[0].GetProperty("vendorName").GetString().Should().Be("HP");
        sets[0].GetProperty("releaseName").GetString().Should().Be("Windows 11, version 24H2");
        sets[0].GetProperty("components").GetInt32().Should().Be(2);
        sets[0].GetProperty("bytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length + DriverSyncHarness.Content("sp202", "1.0").Length);
        (await (await SetsAsync("Arm64")).ReadJsonAsync()).GetArrayLength().Should().Be(0);
        (await SetsAsync("Itanium")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Links_AreOnePerDistinctFile_WithASixtyMinuteReadLink_AndWriteNothing()
    {
        var (first, second) = await TwoModelsAsync();
        var setsBefore = (await _h.Bed.SetDocuments.ListAsync()).Count;

        var response = await LinksAsync(new { sets = new[] { new { modelId = first, release = "Windows11_24H2" }, new { modelId = second, release = "Windows11_24H2" } } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        json.GetProperty("sets").GetArrayLength().Should().Be(2);
        var files = json.GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(2, "the shared file appears once");
        files.Should().OnlyContain(f => f.GetProperty("url").GetString()!.Contains("sp=r", StringComparison.Ordinal));
        files.Should().OnlyContain(f => f.GetProperty("expiresAt").GetDateTimeOffset() == DriverTestBed.Start.AddMinutes(60));
        (await _h.Bed.SetDocuments.ListAsync()).Should().HaveCount(setsBefore);
    }

    [Fact]
    public async Task Links_RefuseMoreThanTwentySets_AndUnknownPairs()
    {
        var (first, _) = await TwoModelsAsync();

        (await LinksAsync(new { sets = Enumerable.Range(0, 21).Select(_ => new { modelId = Guid.NewGuid(), release = "Windows11_24H2" }) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LinksAsync(new { sets = new[] { new { modelId = Guid.NewGuid(), release = "Windows11_24H2" } } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LinksAsync(new { sets = new[] { new { modelId = first, release = "Windows11_23H2" } } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void StepCounts_SayHowManyFilesCameFromTheUsbDrive()
    {
        static Contracts.Models.SetupProfiles.ImagingStepItem Item(int index, bool? usb) => new() { Index = index, Kind = StepItemKind.DriverComponent, Name = "x", Outcome = StepItemOutcome.Succeeded, FromUsbCache = usb };

        var counts = ReportProgressFunction.CountItems([Item(0, true), Item(1, true), Item(2, false)]);
        var none = ReportProgressFunction.CountItems([Item(0, false)]);

        counts.Single().Should().Match<Contracts.Models.SetupProfiles.ImagingStepItemCount>(c => c.Count == 3 && c.FromUsbCache == 2);
        none.Single().FromUsbCache.Should().BeNull();
    }
}

using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

/// <summary>The Windows releases offered: the built-in ones plus Microsoft's list, kept and re-read on a schedule.</summary>
public sealed class WindowsReleaseCatalogServiceTests
{
    private readonly DriverTestBed _bed = new();
    private readonly FakeWindowsReleaseSource _source = new();
    private readonly WindowsReleaseCatalogService _catalog;

    public WindowsReleaseCatalogServiceTests()
    {
        _catalog = FakeWindowsReleaseSource.Catalog(_bed, _source);
    }

    private static WindowsRelease Release(string value) =>
        WindowsRelease.TryParse(value, out var release) ? release : throw new ArgumentException(value, nameof(value));

    private static WindowsReleaseRead Listed(params string[] releases) =>
        new([.. releases.Select(r => new WindowsReleaseEntry(Release(r), "26200", DriverTestBed.Start))], null);

    [Fact]
    public async Task Get_AddsMicrosoftsReleasesToTheBuiltInOnes_NewestFirst()
    {
        _source.Next = Listed("Windows11_26H2", "Windows11_25H2");

        var list = await _catalog.GetAsync();

        list.Releases.Select(r => r.WireName).Should().Equal("Windows11_26H2", "Windows11_25H2", "Windows11_24H2", "Windows11_23H2", "Windows10_22H2");
        list.RefreshedAt.Should().Be(DriverTestBed.Start);
        list.Problem.Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenMicrosoftsListCannotBeRead_OffersTheBuiltInOnes_WithTheProblem()
    {
        _source.Next = new WindowsReleaseRead([], WindowsReleaseGraphClient.DeniedMessage);

        var list = await _catalog.GetAsync();

        list.Releases.Should().Equal(WindowsReleaseCatalogService.BuiltIn.OrderDescending());
        list.Problem.Should().Be(WindowsReleaseGraphClient.DeniedMessage);
        list.RefreshedAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_ReadsMicrosoftsListAgain_OnlyWhenItIsDue()
    {
        _source.Next = Listed("Windows11_26H2");
        await _catalog.GetAsync();

        _bed.Time.Advance(WindowsReleaseCatalogService.RefreshAge - TimeSpan.FromMinutes(1));
        await _catalog.GetAsync();
        _source.Reads.Should().Be(1);

        _bed.Time.Advance(TimeSpan.FromMinutes(1));
        await _catalog.GetAsync();
        _source.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Get_AfterAFailedRead_TriesAgainSooner_AndClearsTheProblemOnceItWorks()
    {
        _source.Next = new WindowsReleaseRead([], WindowsReleaseGraphClient.DeniedMessage);
        await _catalog.GetAsync();

        _source.Next = Listed("Windows11_26H2");
        _bed.Time.Advance(WindowsReleaseCatalogService.RetryAge);
        var list = await _catalog.GetAsync();

        _source.Reads.Should().Be(2);
        list.Problem.Should().BeNull();
        list.Releases.Should().Contain(Release("Windows11_26H2"));
    }

    [Fact]
    public async Task Get_KeepsAReleaseMicrosoftNoLongerLists_AndAFailedReadKeepsTheStoredList()
    {
        _source.Next = Listed("Windows11_26H2");
        await _catalog.GetAsync();

        _source.Next = Listed("Windows11_27H2");
        _bed.Time.Advance(WindowsReleaseCatalogService.RefreshAge);
        await _catalog.GetAsync();

        _source.Next = new WindowsReleaseRead([], "Microsoft Graph could not be reached: timeout");
        _bed.Time.Advance(WindowsReleaseCatalogService.RefreshAge);
        var list = await _catalog.GetAsync();

        list.Releases.Should().Contain([Release("Windows11_26H2"), Release("Windows11_27H2")]);
        list.Problem.Should().Be("Microsoft Graph could not be reached: timeout");
        list.RefreshedAt.Should().Be(DriverTestBed.Start + WindowsReleaseCatalogService.RefreshAge);
    }

    [Fact]
    public async Task Function_ReturnsTheReleasesAsWireValues()
    {
        _source.Next = Listed("Windows11_26H2");

        var response = await new WindowsReleaseFunctions(_catalog).GetWindowsReleases(FunctionTestHost.Request("GET", "https://core/api/internal/drivers/windows-releases"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("releases").EnumerateArray().Select(r => r.GetString()).Should().StartWith(["Windows11_26H2", "Windows11_25H2"]);
        body.GetProperty("problem").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }
}

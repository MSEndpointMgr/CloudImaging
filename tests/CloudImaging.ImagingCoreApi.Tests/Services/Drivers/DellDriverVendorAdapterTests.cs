using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

/// <summary>The Dell adapter over the captured catalog index and the Latitude 5450 (0CB2, 0CB4) and 7455 (0C85, ARM64) catalogs.</summary>
public sealed class DellDriverVendorAdapterTests
{
    private const string Sku0Cb4Sha = "f3ea8890a93915edb4efdecf9224206f3d31574fe9f941880ae2c610899ad2c7";
    private static readonly DateTimeOffset Seen = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private readonly DellDriverVendorAdapter _adapter = new();

    private static MemoryStream Decompress(string fixture)
    {
        var output = new MemoryStream();
        CabinetReader.ExtractSingleFile(CabinetReaderTests.ReadFixture(fixture), output);
        output.Position = 0;
        return output;
    }

    private DriverCatalogModelRecord Model(string key) => _adapter.ParseModelList(Decompress("CatalogIndexPC.cab"), Seen).Single(m => m.Key == key).Model;

    private IReadOnlyList<DriverVendorComponent> Parse(string fixture, MachineArchitecture architecture = MachineArchitecture.X64, WindowsRelease release = WindowsRelease.Windows11Version24H2) =>
        _adapter.ParseComponents(Decompress(fixture), new DriverCatalogModelRecord { Name = "Latitude", MatchKeys = ["0CB4"], Architecture = architecture }, release, Seen);

    [Fact]
    public void ModelList_GroupsSkusByModelName_WithEachSkusCatalogPathAndSha256()
    {
        var models = _adapter.ParseModelList(Decompress("CatalogIndexPC.cab"), Seen);

        var latitude = models.Single(m => m.Key == "LATITUDE_5450").Model;
        latitude.Name.Should().Be("Latitude 5450");
        latitude.MatchKeys.Should().Equal("0CB2", "0CB4");
        latitude.Architecture.Should().Be(MachineArchitecture.X64);
        latitude.Releases.Should().Equal(WindowsRelease.Windows11Version23H2, WindowsRelease.Windows11Version24H2, WindowsRelease.Windows11Version25H2);
        latitude.SkuCatalogs.Should().ContainEquivalentOf(new DriverSkuCatalog("0CB4", "FOLDER15162537M/1/Latitude_0CB4.cab", Sku0Cb4Sha));
        models.Should().HaveCountGreaterThan(500);
    }

    [Fact]
    public void ModelList_Arm64Collector_IsAnArm64Model()
    {
        var arm = Model("LATITUDE_7455");

        arm.Architecture.Should().Be(MachineArchitecture.Arm64);
        arm.MatchKeys.Should().Equal("0C85");
    }

    [Fact]
    public void ComponentCatalogs_AreTheSkuCatalogs_WithTheirPublishedSha256()
    {
        var model = Model("LATITUDE_5450");

        var urls = _adapter.ComponentCatalogUrls(model, WindowsRelease.Windows11Version24H2);

        urls.Should().Contain("https://downloads.dell.com/FOLDER15162537M/1/Latitude_0CB4.cab").And.HaveCount(2);
        _adapter.CatalogSha256(model, "https://downloads.dell.com/FOLDER15162537M/1/Latitude_0CB4.cab").Should().Be(Sku0Cb4Sha);
    }

    [Fact]
    public async Task ASkuCatalogWhoseSha256Differs_IsRefused()
    {
        var bed = new DriverTestBed();
        var storage = new DriverStorageService(new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"), bed.Blobs, bed.Http, bed.Time);
        var catalogs = new DriverCatalogService(bed.Store, bed.Models, storage, bed.Blobs, [_adapter], bed.Time);
        var model = Model("LATITUDE_5450") with { SkuCatalogs = [new DriverSkuCatalog("0CB4", "FOLDER15162537M/1/Latitude_0CB4.cab", new string('0', 64))] };
        bed.Vendor.ServeFixture("https://downloads.dell.com/FOLDER15162537M/1/Latitude_0CB4.cab", "Latitude_0CB4.cab");

        var (components, failure) = await catalogs.ReadComponentsAsync(_adapter, model, WindowsRelease.Windows11Version24H2, null);

        components.Should().BeNull();
        failure.Should().Be("The catalog at https://downloads.dell.com/FOLDER15162537M/1/Latitude_0CB4.cab does not match its published checksum.");
    }

    [Fact]
    public async Task ComponentsOfAllSkus_AreMergedByLineage_KeepingTheNewest()
    {
        var bed = new DriverTestBed();
        var storage = new DriverStorageService(new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"), bed.Blobs, bed.Http, bed.Time);
        var catalogs = new DriverCatalogService(bed.Store, bed.Models, storage, bed.Blobs, [_adapter], bed.Time);
        var model = Model("LATITUDE_5450");
        foreach (var sku in model.SkuCatalogs)
        {
            bed.Vendor.ServeFixture($"https://downloads.dell.com/{sku.Path}", $"Latitude_{sku.Sku}.cab");
        }

        var (merged, failure) = await catalogs.ReadComponentsAsync(_adapter, model, WindowsRelease.Windows11Version24H2, null);

        failure.Should().BeNull();
        var all = merged.Should().NotBeNull().And.Subject;
        all.Select(c => c.LineageKey).Should().OnlyHaveUniqueItems();
        var single = Parse("Latitude_0CB4.cab");
        all.Count().Should().BeGreaterThanOrEqualTo(single.Count);
        foreach (var component in all)
        {
            single.Where(s => s.LineageKey == component.LineageKey).Should().OnlyContain(s => s.Component.ReleasedAt <= component.Component.ReleasedAt);
        }
    }

    [Fact]
    public void Components_AreNewestPerLineage_KeyedByComponentId()
    {
        var components = Parse("Latitude_0CB4.cab");

        components.Select(c => c.LineageKey).Should().OnlyHaveUniqueItems();
        components.Should().OnlyContain(c => c.LineageKey.StartsWith("dell-", StringComparison.Ordinal) || c.LineageKey.StartsWith("name-", StringComparison.Ordinal));
        components.Count(c => c.Component.Name == "Intel Management Engine Components Installer").Should().Be(1);
    }

    [Fact]
    public void FirmwareAndBios_AreNeverStored()
    {
        var components = Parse("Latitude_0CB4.cab");

        components.Should().NotBeEmpty();
        components.Should().NotContain(c => c.Component.Name.Contains("Firmware", StringComparison.OrdinalIgnoreCase));
        components.Should().NotContain(c => c.Component.Name.Contains("BIOS", StringComparison.Ordinal) && c.Component.Category!.StartsWith("BIOS", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsFilter_DropsComponentsThatDoNotListTheRelease()
    {
        var windows11 = Parse("Latitude_0CB4.cab");
        var windows10 = Parse("Latitude_0CB4.cab", release: WindowsRelease.Windows10Version22H2);

        windows11.Should().NotBeEmpty();
        windows10.Select(c => c.Component.ComponentId).Should().NotBeEquivalentTo(windows11.Select(c => c.Component.ComponentId), "some packages list only one Windows version");
    }

    [Theory]
    [InlineData("DRVR", "LW64", true, DriverAction.OfflineAndSpecialize, new[] { DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip })]
    [InlineData("DRVR", "LW64", false, DriverAction.Offline, new[] { DriverAction.Offline, DriverAction.Skip })]
    [InlineData("DRVR", "LWXP", false, DriverAction.Specialize, new[] { DriverAction.Specialize, DriverAction.Skip })]
    [InlineData("DRVR", "LWXP", true, DriverAction.Specialize, new[] { DriverAction.Specialize, DriverAction.Skip })]
    [InlineData("APAC", "LW64", false, DriverAction.Skip, new[] { DriverAction.Skip, DriverAction.Specialize })]
    public void Classification_FollowsTheDellTable(string type, string package, bool app, DriverAction expectedDefault, DriverAction[] allowed)
    {
        var (defaultAction, allowedActions) = DellDriverVendorAdapter.Classify(type, package, app);

        defaultAction.Should().Be(expectedDefault);
        allowedActions.Should().Equal(allowed);
    }

    [Fact]
    public void Lwxp_NeverOffersOffline_AndRunsThePackageWithS()
    {
        var realtek = Parse("Latitude_0CB4.cab").Single(c => c.Component.ComponentId == "CP0VC").Component;

        realtek.AllowedActions.Should().NotContain([DriverAction.Offline, DriverAction.OfflineAndSpecialize]);
        realtek.DefaultAction.Should().Be(DriverAction.Specialize);
        realtek.Extract.Should().Be(DriverExtractKind.None);
        realtek.InstallCommand.Should().Be(new DriverInstallCommand { Executable = realtek.FileName!, Arguments = "/s" });
        realtek.Details.Should().ContainEquivalentOf(new DriverDetailField { Label = "Installer", Value = "32-bit" });
    }

    [Fact]
    public void DriverAndApplication_IsOfflineAndSpecialize_ExtractedAsDell64()
    {
        var rst = Parse("Latitude_0CB4.cab").First(c => c.Component.Name.Contains("Driver and Application", StringComparison.Ordinal)).Component;

        rst.DefaultAction.Should().Be(DriverAction.OfflineAndSpecialize);
        rst.Extract.Should().Be(DriverExtractKind.Dell64);
        rst.Url.Should().StartWith("https://downloads.dell.com/");
        rst.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        rst.Version.Should().MatchRegex("^A\\d\\d$");
    }

    [Fact]
    public void Arm64Model_TakesItsArchitectureFromOsArch()
    {
        var arm = Parse("Latitude_0C85.cab", MachineArchitecture.Arm64);
        var asX64 = Parse("Latitude_0C85.cab", MachineArchitecture.X64);

        arm.Should().NotBeEmpty();
        arm.Count.Should().BeGreaterThan(asX64.Count, "the Snapdragon catalog lists its packages for ARM64");
    }

    [Fact]
    public void PciIds_AreParsedFromTheSupportedDevices()
    {
        var withPci = Parse("Latitude_0CB4.cab").Where(c => c.Component.PciIds.Count > 0).ToList();

        withPci.Should().NotBeEmpty();
        withPci.SelectMany(c => c.Component.PciIds).Should().OnlyContain(p => p.Vendor.Length == 4 && p.Device.Length == 4);
        withPci.SelectMany(c => c.Component.PciIds).Should().Contain(p => p.Vendor == "8086" && p.Device == "43F0" && p.SubVendor == "8086" && p.SubDevice == "0074");
        withPci.Should().OnlyContain(c => c.Component.Details.Any(d => d.Label == "Hardware match" && d.Value.EndsWith("PCI device", StringComparison.Ordinal) || d.Label == "Hardware match" && d.Value.EndsWith("PCI devices", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModelPageDetails_ShowTheDellFieldsInOrder_NamingDell()
    {
        var component = Parse("Latitude_0CB4.cab").Single(c => c.Component.ComponentId == "CP0VC").Component;

        var details = DriverModelService.Details(DriverVendor.Dell, component);

        details.Select(d => d.Label).Should().Equal("Installer", "Hardware match", "Package type", "Default action", "Released by Dell", "Criticality", "Added to this model", "Previous version", "Download size");
        details.Single(d => d.Label == "Default action").Value.Should().Be("Specialize");
        details.Should().NotContain(d => d.Label.Contains("HP", StringComparison.Ordinal) || d.Value.Contains("HP", StringComparison.Ordinal));
    }

    [Fact]
    public void Matching_UsesTheSystemSku()
    {
        _adapter.DeviceMatchKey(new DeviceHardwareMetadata { MotherboardModel = "0B1F", SystemSku = "0CB4" }).Should().Be("0CB4");
        _adapter.IsVendorOf("Dell Inc.").Should().BeTrue();
        _adapter.IsVendorOf("HP").Should().BeFalse();
    }
}

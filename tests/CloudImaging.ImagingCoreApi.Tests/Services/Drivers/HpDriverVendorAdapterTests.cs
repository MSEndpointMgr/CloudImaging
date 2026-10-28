using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class HpDriverVendorAdapterTests
{
    private static readonly DateTimeOffset Seen = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private readonly HpDriverVendorAdapter _adapter = new();

    [Fact]
    public void ParseModelList_PlatformList_YieldsX64AndArm64ModelsWithReleases()
    {
        var models = _adapter.ParseModelList(Decompress("platformList.cab"), Seen);

        var g11 = models.Single(m => m.Key == "8C26_X64").Model;
        g11.Name.Should().Be("HP EliteBook 830 13 inch G11 Notebook PC");
        g11.MatchKeys.Should().Equal("8C26");
        g11.Architecture.Should().Be(MachineArchitecture.X64);
        g11.Releases.Should().Contain([WindowsRelease.Windows10Version22H2, WindowsRelease.Windows11Version23H2, WindowsRelease.Windows11Version24H2, WindowsRelease.Windows11Version25H2]);

        var arm = models.Single(m => m.Key == "8CBE_ARM64").Model;
        arm.Architecture.Should().Be(MachineArchitecture.Arm64);
        arm.Releases.Should().Equal(WindowsRelease.Windows11Version24H2);
        models.Should().HaveCountGreaterThan(400);
    }

    [Fact]
    public void ComponentCatalogUrls_X64AndArm64_UseTheirFileNames()
    {
        var x64 = new DriverCatalogModelRecord { Name = "G11", MatchKeys = ["8C26"], Architecture = MachineArchitecture.X64 };
        var arm = new DriverCatalogModelRecord { Name = "G1q", MatchKeys = ["8CBE"], Architecture = MachineArchitecture.Arm64 };

        _adapter.ComponentCatalogUrls(x64, WindowsRelease.Windows11Version24H2).Should().Equal("https://hpia.hpcloud.hp.com/ref/8c26/8c26_64_11.0.24h2.cab");
        _adapter.ComponentCatalogUrls(arm, WindowsRelease.Windows11Version24H2).Should().Equal("https://hpia.hpcloud.hp.com/ref/8cbe/8cbe_arm64_11.0.24h2.cab");
    }

    [Fact]
    public void ParseComponents_ReferenceFile_ExcludesFirmwareBiosAndDocks()
    {
        var components = ParseX64();

        components.Should().NotBeEmpty();
        components.Should().NotContain(c => c.Component.Category!.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase));
        components.Should().NotContain(c => c.Component.Category == "Firmware");
        components.Should().NotContain(c => c.Component.Category!.StartsWith("Dock - Firmware", StringComparison.OrdinalIgnoreCase));
        components.Should().NotContain(c => c.Component.Name.Contains("Firmware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseComponents_Fields_AreReadWithHttpsUrlsAndLowercaseHashes()
    {
        var graphics = ParseX64().Single(c => c.Component.ComponentId == "sp174837").Component;

        graphics.Name.Should().Be("Intel Video Driver and Control Panel");
        graphics.Category.Should().Be("Driver - Graphics");
        graphics.Version.Should().Be("32.0.101.9028");
        graphics.Url.Should().Be("https://ftp.hp.com/pub/softpaq/sp174501-175000/sp174837.exe");
        graphics.Sha256.Should().Be("aa7add707817e5e34e5474a3fc1a3b5830e059e2a8ccba5f5c7b52791ad2b3b4");
        graphics.Size.Should().Be(869469496);
        graphics.DescriptionUrl.Should().Be("https://ftp.hp.com/pub/softpaq/sp174501-175000/sp174837.html");
        graphics.Extract.Should().Be(DriverExtractKind.Hp);
        DriverStorageService.CheckUrl(graphics.Url).Should().BeNull();
    }

    [Fact]
    public void ParseComponents_DriverWithApp_IsOfflineAndSpecializeByDefault()
    {
        var graphics = ParseX64().Single(c => c.Component.ComponentId == "sp174837").Component;

        graphics.DefaultAction.Should().Be(DriverAction.OfflineAndSpecialize);
        graphics.AllowedActions.Should().Equal(DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip);
        graphics.InstallCommand.Should().BeEquivalentTo(new DriverInstallCommand { Executable = "HPUP.exe" });
    }

    [Fact]
    public void ParseComponents_SupersedesChain_KeysTheLineageByItsOldestId()
    {
        var graphics = ParseX64().Single(c => c.Component.ComponentId == "sp174837");

        graphics.LineageKey.Should().NotBe("sp174837");
        graphics.LineageKey.Should().StartWith("sp");
    }

    [Theory]
    [InlineData(true, false, true, "Driver - Network", DriverAction.Offline, new[] { DriverAction.Offline, DriverAction.Skip })]
    [InlineData(true, true, true, "Driver - Audio", DriverAction.OfflineAndSpecialize, new[] { DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip })]
    [InlineData(true, true, false, "Driver - Graphics", DriverAction.Offline, new[] { DriverAction.Offline, DriverAction.Skip })]
    [InlineData(false, false, true, "Software - Solutions", DriverAction.Skip, new[] { DriverAction.Skip, DriverAction.Specialize })]
    [InlineData(false, false, true, "Driver - Chipset", DriverAction.Specialize, new[] { DriverAction.Specialize, DriverAction.Skip })]
    [InlineData(false, false, false, "Driver - Chipset", DriverAction.Skip, new[] { DriverAction.Skip })]
    [InlineData(false, false, true, "Diagnostic", DriverAction.Skip, new[] { DriverAction.Skip })]
    [InlineData(false, false, true, "Manageability - Driver Pack", DriverAction.Skip, new[] { DriverAction.Skip })]
    [InlineData(false, false, true, "Utility - Tools", DriverAction.Skip, new[] { DriverAction.Skip })]
    public void Classify_X64_FollowsTheHpTable(bool dpb, bool app, bool installable, string category, DriverAction expectedDefault, DriverAction[] expectedAllowed)
    {
        var (defaultAction, allowed) = HpDriverVendorAdapter.Classify(dpb, app, installable, category, arm64: false);

        defaultAction.Should().Be(expectedDefault);
        allowed.Should().Equal(expectedAllowed);
    }

    [Theory]
    [InlineData(true, false, true, "Driver - Network")]
    [InlineData(true, true, true, "Driver - Audio")]
    [InlineData(false, false, true, "Driver - Chipset")]
    public void Classify_Arm64Installable_NeverAllowsOfflineAndDefaultsToSpecialize(bool dpb, bool app, bool installable, string category)
    {
        var (defaultAction, allowed) = HpDriverVendorAdapter.Classify(dpb, app, installable, category, arm64: true);

        defaultAction.Should().Be(DriverAction.Specialize);
        allowed.Should().NotContain([DriverAction.Offline, DriverAction.OfflineAndSpecialize]);
        allowed.Should().Contain(DriverAction.Specialize);
    }

    [Fact]
    public void Classify_Arm64NotInstallable_IsLockedToSkip()
    {
        var (defaultAction, allowed) = HpDriverVendorAdapter.Classify(true, false, false, "Driver - Network", arm64: true);

        defaultAction.Should().Be(DriverAction.Skip);
        allowed.Should().Equal(DriverAction.Skip);
    }

    [Fact]
    public void ParseComponents_Arm64ReferenceFile_StagesTheSoftPaqUnextractedWithSlashS()
    {
        var model = new DriverCatalogModelRecord { Name = "G1q", MatchKeys = ["8CBE"], Architecture = MachineArchitecture.Arm64 };

        var components = _adapter.ParseComponents(Decompress("8cbe_arm64_11.0.24h2.cab"), model, WindowsRelease.Windows11Version24H2, Seen);

        components.Should().NotBeEmpty();
        components.Should().OnlyContain(c => !c.Component.AllowedActions.Contains(DriverAction.Offline) && !c.Component.AllowedActions.Contains(DriverAction.OfflineAndSpecialize));
        components.Should().OnlyContain(c => c.Component.Extract == DriverExtractKind.None);
        components.Where(c => c.Component.InstallCommand is not null)
            .Should().OnlyContain(c => c.Component.InstallCommand!.Executable == c.Component.FileName && c.Component.InstallCommand.Arguments == "/s");
    }

    [Theory]
    [InlineData(true, "HPUP.exe", true)]
    [InlineData(true, "\"HPUP.exe\" silent", true)]
    [InlineData(false, "HPUP.exe", false)]
    [InlineData(true, "None", false)]
    [InlineData(true, "NA", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "SSD do not support silent install.", false)]
    public void IsInstallable_FollowsTheHpRule(bool ssm, string silentInstall, bool expected)
    {
        HpDriverVendorAdapter.IsInstallable(ssm, silentInstall).Should().Be(expected);
    }

    [Fact]
    public void LineageKey_FollowsSupersedesToTheOldestId()
    {
        var supersedes = new Dictionary<string, string> { ["sp3"] = "sp2", ["sp2"] = "sp1" };

        HpDriverVendorAdapter.LineageKey("SP3", supersedes).Should().Be("sp1");
        HpDriverVendorAdapter.LineageKey("sp9", supersedes).Should().Be("sp9");
    }

    private IReadOnlyList<DriverVendorComponent> ParseX64()
    {
        var model = new DriverCatalogModelRecord { Name = "G11", MatchKeys = ["8C26"], Architecture = MachineArchitecture.X64 };
        return _adapter.ParseComponents(Decompress("8c26_64_11.0.24h2.cab"), model, WindowsRelease.Windows11Version24H2, Seen);
    }

    private static MemoryStream Decompress(string fixture)
    {
        var output = new MemoryStream();
        CabinetReader.ExtractSingleFile(CabinetReaderTests.ReadFixture(fixture), output);
        output.Position = 0;
        return output;
    }
}

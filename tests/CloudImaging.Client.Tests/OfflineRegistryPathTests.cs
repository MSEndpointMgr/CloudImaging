using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Offline registry paths: a value lands in the right hive file and mount, CurrentControlSet maps to
/// the image's own control set, and data converts to the registry type at the range edges.
/// </summary>
public sealed class OfflineRegistryPathTests
{
    [Theory]
    [InlineData(1, @"ControlSet001\Services\tzautoupdate")]
    [InlineData(2, @"ControlSet002\Services\tzautoupdate")]
    public void CurrentControlSet_MapsToTheImagesCurrentControlSet(int current, string expected)
    {
        OfflineRegistryPath.MapKey(SetupRegistryHive.System, @"CurrentControlSet\Services\tzautoupdate", current).Should().Be(expected);
        OfflineRegistryPath.MapKey(SetupRegistryHive.System, "currentcontrolset", current).Should().Be($"ControlSet00{current}");
    }

    [Theory]
    [InlineData(SetupRegistryHive.System, @"Setup\Status")]
    [InlineData(SetupRegistryHive.Software, @"Policies\Microsoft\FVE")]
    [InlineData(SetupRegistryHive.DefaultUser, @"Control Panel\Desktop")]
    public void OtherKeys_AreUnchanged(SetupRegistryHive hive, string key)
    {
        OfflineRegistryPath.MapKey(hive, key, 3).Should().Be(key);
    }

    [Theory]
    [InlineData(SetupRegistryHive.Software, @"HKLM\CI_OFFLINE_SOFTWARE", @"Windows\System32\config\SOFTWARE")]
    [InlineData(SetupRegistryHive.System, @"HKLM\CI_OFFLINE_SYSTEM", @"Windows\System32\config\SYSTEM")]
    [InlineData(SetupRegistryHive.DefaultUser, @"HKLM\CI_OFFLINE_DEFAULTUSER", @"Users\Default\NTUSER.DAT")]
    public void MountRootsAndHiveFiles(SetupRegistryHive hive, string mountRoot, string hiveFile)
    {
        OfflineRegistryPath.MountRoot(hive).Should().Be(mountRoot);
        OfflineRegistryPath.HiveFile(hive).Should().Be(hiveFile);
    }

    [Fact]
    public void Strings_KeepTheirKind()
    {
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.Text, "a", out var text, out var textKind).Should().BeTrue();
        (text, textKind).Should().Be(("a", RegistryValueKind.String));
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.ExpandString, "%WINDIR%", out _, out var expandKind).Should().BeTrue();
        expandKind.Should().Be(RegistryValueKind.ExpandString);
    }

    [Fact]
    public void MultiString_SplitsOnNewLines()
    {
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.MultiString, "one\ntwo\r\nthree", out var value, out var kind).Should().BeTrue();
        kind.Should().Be(RegistryValueKind.MultiString);
        value.Should().BeEquivalentTo(ExpectedLines);
    }

    private static readonly string[] ExpectedLines = ["one", "two", "three"];

    [Theory]
    [InlineData("0", 0)]
    [InlineData("4294967295", -1)]
    [InlineData("0xFFFFFFFF", -1)]
    [InlineData("0x10", 16)]
    public void DWord_ParsesDecimalAndHex(string data, int expected)
    {
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.DWord, data, out var value, out var kind).Should().BeTrue();
        kind.Should().Be(RegistryValueKind.DWord);
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData(SetupRegistryValueType.DWord, "4294967296")]
    [InlineData(SetupRegistryValueType.DWord, "0x100000000")]
    [InlineData(SetupRegistryValueType.DWord, "-1")]
    [InlineData(SetupRegistryValueType.QWord, "18446744073709551616")]
    [InlineData(SetupRegistryValueType.QWord, "0x")]
    public void OutOfRange_IsRefused(SetupRegistryValueType type, string data)
    {
        OfflineRegistryPath.TryParseData(type, data, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void QWord_ParsesTheFullRange()
    {
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.QWord, "18446744073709551615", out var max, out var kind).Should().BeTrue();
        kind.Should().Be(RegistryValueKind.QWord);
        max.Should().Be(-1L);
        OfflineRegistryPath.TryParseData(SetupRegistryValueType.QWord, "0xFFFFFFFFFFFFFFFF", out var hex, out _).Should().BeTrue();
        hex.Should().Be(-1L);
    }

    [Fact]
    public void DisplayTarget_IsTheFullPathWithValueName()
    {
        OfflineRegistryPath.DisplayTarget(new SetupRegistryValue { Name = "x", Hive = SetupRegistryHive.Software, Key = @"Policies\Microsoft\FVE", ValueName = "UseTPM" })
            .Should().Be(@"HKLM\SOFTWARE\Policies\Microsoft\FVE\UseTPM");
        OfflineRegistryPath.DisplayTarget(new SetupRegistryValue { Name = "x", Hive = SetupRegistryHive.DefaultUser, Key = "Control Panel", ValueName = "" })
            .Should().Be(@"Default user\Control Panel\(Default)");
    }
}

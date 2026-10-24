using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class SetupProfileValidatorTests
{
    private static readonly Guid Known = Guid.NewGuid();
    private static readonly HashSet<Guid> Locations = [Known];

    private static SetupRegistryValue Value(SetupRegistryHive hive = SetupRegistryHive.Software, string key = "Policies\\Microsoft\\FVE", string valueName = "EncryptionMethodWithXtsOs", SetupRegistryValueType type = SetupRegistryValueType.DWord, string data = "7", string name = "Use XTS-AES 256") =>
        new() { Name = name, Hive = hive, Key = key, ValueName = valueName, Type = type, Data = data };

    private static readonly string Hash = new('a', 64);

    private static SetupScript Script(string name = "Enable Wake on LAN", SetupScriptPhase runs = SetupScriptPhase.WinPE, int order = 1, int timeout = 5, string? sha = null, SetupScriptLocations? locations = null) =>
        new() { Name = name, Runs = runs, Order = order, TimeoutMinutes = timeout, ContentSha256 = sha ?? Hash, Locations = locations ?? new SetupScriptLocations() };

    private static List<string> Content(params SetupRegistryValue[] values) =>
        SetupProfileValidator.ValidateContent(new SetupProfileContent { RegistryValues = values }, Locations);

    private static List<string> Scripts(params SetupScript[] scripts) =>
        SetupProfileValidator.ValidateContent(new SetupProfileContent { Scripts = scripts }, Locations);

    [Fact]
    public void ValidateProfile_ValidNameAndLocation_ReturnsNoProblems()
    {
        SetupProfileValidator.ValidateProfile("Stockholm", new SetupProfileTargeting { LocationIds = [Known] }, Locations).Should().BeEmpty();
        SetupProfileValidator.ValidateProfile("Everywhere", new SetupProfileTargeting { AllDevices = true }, Locations).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateProfile_EmptyName_IsRefused(string name)
    {
        SetupProfileValidator.ValidateProfile(name, new SetupProfileTargeting { AllDevices = true }, Locations)
            .Should().ContainSingle().Which.Should().Be("Enter a profile name of 1 to 100 characters.");
    }

    [Fact]
    public void ValidateProfile_NameOf101Characters_IsRefused_AndHundredIsAccepted()
    {
        SetupProfileValidator.ValidateProfile(new string('a', 101), new SetupProfileTargeting { AllDevices = true }, Locations).Should().HaveCount(1);
        SetupProfileValidator.ValidateProfile(new string('a', 100), new SetupProfileTargeting { AllDevices = true }, Locations).Should().BeEmpty();
    }

    [Fact]
    public void ValidateProfile_NoTargeting_AsksForALocationOrAllDevices()
    {
        SetupProfileValidator.ValidateProfile("Lab", new SetupProfileTargeting(), Locations)
            .Should().ContainSingle().Which.Should().Be("Choose at least one location or All devices.");
    }

    [Fact]
    public void ValidateProfile_UnknownLocation_IsRefused()
    {
        SetupProfileValidator.ValidateProfile("Lab", new SetupProfileTargeting { LocationIds = [Guid.NewGuid()] }, Locations)
            .Should().ContainSingle().Which.Should().Contain("no longer exists");
    }

    [Fact]
    public void ValidateProfile_AllDevicesWithLocations_IsRefused()
    {
        SetupProfileValidator.ValidateProfile("Lab", new SetupProfileTargeting { AllDevices = true, LocationIds = [Known] }, Locations).Should().HaveCount(1);
    }

    [Fact]
    public void ValidateProfile_EveryProblem_IsReturnedAtOnce()
    {
        SetupProfileValidator.ValidateProfile("", new SetupProfileTargeting(), Locations).Should().HaveCount(2);
    }

    [Fact]
    public void ValidateContent_ValidValues_ReturnsNoProblems()
    {
        Content(
            Value(),
            Value(SetupRegistryHive.System, "CurrentControlSet\\Control\\BitLocker", "PreventDeviceEncryption", data: "1", name: "Stop device encryption"),
            Value(SetupRegistryHive.DefaultUser, "Control Panel\\Desktop", "MenuShowDelay", SetupRegistryValueType.Text, "200", "Faster menus"),
            Value(valueName: "", type: SetupRegistryValueType.MultiString, data: "a\nb", name: "Default value"),
            Value(valueName: "Q", type: SetupRegistryValueType.QWord, data: "0xFFFFFFFFFFFFFFFF", name: "Max qword"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("HKLM\\SOFTWARE\\Policies")]
    [InlineData("HKEY_LOCAL_MACHINE\\SOFTWARE")]
    [InlineData("\\Policies")]
    [InlineData("Policies\\")]
    [InlineData("Policies\\\\Microsoft")]
    [InlineData("")]
    public void ValidateContent_KeyNotRelativeToTheHive_IsRefused(string key)
    {
        Content(Value(key: key)).Should().ContainSingle().Which.Should().Contain("Use XTS-AES 256");
    }

    [Fact]
    public void ValidateContent_KeyLimits_AreEnforced()
    {
        Content(Value(key: new string('k', 513))).Should().HaveCount(1);
        Content(Value(key: new string('k', 256))).Should().ContainSingle().Which.Should().Contain("at most 255");
        Content(Value(key: $"{new string('k', 255)}\\{new string('k', 255)}")).Should().BeEmpty();
    }

    [Fact]
    public void ValidateContent_CurrentControlSet_IsOnlyAllowedUnderSystem()
    {
        Content(Value(SetupRegistryHive.Software, "CurrentControlSet\\Services")).Should().ContainSingle().Which.Should().Contain("only exists under HKLM SYSTEM");
        Content(Value(SetupRegistryHive.System, "CurrentControlSet\\Services")).Should().BeEmpty();
    }

    [Fact]
    public void ValidateContent_ValueNameOver255_IsRefused()
    {
        Content(Value(valueName: new string('v', 256))).Should().HaveCount(1);
    }

    [Theory]
    [InlineData(SetupRegistryValueType.DWord, "0", true)]
    [InlineData(SetupRegistryValueType.DWord, "4294967295", true)]
    [InlineData(SetupRegistryValueType.DWord, "4294967296", false)]
    [InlineData(SetupRegistryValueType.DWord, "0xFFFFFFFF", true)]
    [InlineData(SetupRegistryValueType.DWord, "0x1FFFFFFFF", false)]
    [InlineData(SetupRegistryValueType.DWord, "-1", false)]
    [InlineData(SetupRegistryValueType.DWord, "seven", false)]
    [InlineData(SetupRegistryValueType.QWord, "18446744073709551615", true)]
    [InlineData(SetupRegistryValueType.QWord, "18446744073709551616", false)]
    [InlineData(SetupRegistryValueType.QWord, "0x10000000000000000", false)]
    [InlineData(SetupRegistryValueType.MultiString, "a\n\nb", false)]
    [InlineData(SetupRegistryValueType.MultiString, "", false)]
    public void ValidateContent_DataPerType(SetupRegistryValueType type, string data, bool valid)
    {
        Content(Value(type: type, data: data)).Should().HaveCount(valid ? 0 : 1);
    }

    [Fact]
    public void ValidateContent_StringDataOver4096_IsRefused()
    {
        Content(Value(type: SetupRegistryValueType.Text, data: new string('x', 4097))).Should().HaveCount(1);
        Content(Value(type: SetupRegistryValueType.ExpandString, data: new string('x', 4096))).Should().BeEmpty();
    }

    [Fact]
    public void ValidateContent_SameValueTwice_IgnoringCase_IsRefused()
    {
        Content(Value(name: "First"), Value(key: "policies\\microsoft\\fve", valueName: "encryptionmethodwithxtsos", name: "Second"))
            .Should().ContainSingle().Which.Should().Be("\"Second\" sets the same value as \"First\". Keep one of them.");
    }

    [Fact]
    public void ValidateContent_MoreThan200Values_IsRefused()
    {
        var values = Enumerable.Range(0, 201).Select(i => Value(valueName: $"V{i}", name: $"Value {i}")).ToArray();
        Content(values).Should().ContainSingle().Which.Should().Be("A profile can have at most 200 registry values.");
    }

    [Fact]
    public void ValidateContent_ValidScripts_ReturnsNoProblems()
    {
        Scripts(Script(), Script("Second", order: 2), Script("Setup one", SetupScriptPhase.WindowsSetup, timeout: 30)).Should().BeEmpty();
    }

    [Fact]
    public void ValidateContent_DuplicateScriptNames_AreRefused()
    {
        Scripts(Script("Same"), Script("same", order: 2))
            .Should().ContainSingle().Which.Should().Be("Script names must be unique. Rename \"same\" and save again.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void ValidateContent_TimeoutOutsideOneToThirty_IsRefused(int timeout)
    {
        Scripts(Script(timeout: timeout)).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("not a hash")]
    public void ValidateContent_AScriptWithoutAnUploadedText_IsRefused(string sha)
    {
        Scripts(Script("Wake", sha: sha)).Should().ContainSingle().Which.Should().Be("The text of \"Wake\" was not uploaded. Open the script and press Done again.");
    }

    [Fact]
    public void ValidateContent_AnAnswerFileWithoutAnUploadedText_IsRefused()
    {
        SetupProfileValidator.ValidateContent(new SetupProfileContent { AnswerFile = new SetupAnswerFile { ContentSha256 = "x" } }, Locations)
            .Should().ContainSingle().Which.Should().Be("The text of the answer file was not uploaded. Open the answer file and press Done again.");
    }

    [Fact]
    public void ValidateContent_ScriptLocations_MustBeTargetedByTheProfile()
    {
        Scripts(Script(locations: new SetupScriptLocations { All = false, LocationIds = [Known] })).Should().BeEmpty();
        Scripts(Script("Wake", locations: new SetupScriptLocations { All = false, LocationIds = [Known, Guid.NewGuid()] }))
            .Should().ContainSingle().Which.Should().Be("\"Wake\" runs at a location this profile does not target. Choose its locations again.");
    }

    [Fact]
    public void ValidateContent_SelectedLocationsWithNoneChosen_HasNoLocationsLeft()
    {
        Scripts(Script("Wake", locations: new SetupScriptLocations { All = false }))
            .Should().ContainSingle().Which.Should().Be("\"Wake\" has no locations left. Choose a location or delete the script.");
    }

    [Fact]
    public void AllowedScriptLocations_AreTheTargetedOnes_OrEveryLocationForAllDevices()
    {
        var other = Guid.NewGuid();
        HashSet<Guid> known = [Known, other];

        SetupProfileValidator.AllowedScriptLocations(new SetupProfileTargeting { LocationIds = [Known] }, known).Should().BeEquivalentTo([Known]);
        SetupProfileValidator.AllowedScriptLocations(new SetupProfileTargeting { AllDevices = true }, known).Should().BeEquivalentTo([Known, other]);
    }

    [Fact]
    public void ScriptsWithoutLocations_NamesTheScriptsWhoseLocationsAreAllGone()
    {
        var gone = Guid.NewGuid();
        var scripts = new[]
        {
            Script("Everywhere"),
            Script("Still here", locations: new SetupScriptLocations { All = false, LocationIds = [Known, gone] }),
            Script("Gone", locations: new SetupScriptLocations { All = false, LocationIds = [gone] }),
        };

        SetupProfileValidator.ScriptsWithoutLocations(scripts, Locations).Should().Equal("\"Gone\" has no locations left. Choose a location or delete the script.");
    }

    [Fact]
    public void ValidateContent_OrderWithGaps_IsRefused()
    {
        Scripts(Script(order: 1), Script("Two", order: 3)).Should().HaveCount(1);
    }

    [Fact]
    public void ValidateContent_1000ScriptsAreAccepted_1001AreRefused()
    {
        Scripts([.. Enumerable.Range(1, 1000).Select(i => Script($"Script {i}", order: i))]).Should().BeEmpty();
        Scripts([.. Enumerable.Range(1, 1001).Select(i => Script($"Script {i}", order: i))]).Should().Equal("A profile can have at most 1000 scripts.");
    }
}

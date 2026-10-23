using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services.Setup;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The setup runner manifest: driver installers before scripts, every path relative to the system
/// drive, script items with the PowerShell command and their timeout in seconds, and the JSON the
/// runner reads (camelCase, string enums, UTF-8 without BOM).
/// </summary>
public sealed class SetupRunnerManifestBuilderTests
{
    private static readonly Guid SessionId = Guid.Parse("5b7e0f0e-6a0c-4b8e-9a0d-1c2b3d4e5f60");
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 23, 10, 2, 11, 500, TimeSpan.Zero);

    private static SpecializeInstaller Installer(string name, string folder) => new(
        name,
        [new SpecializeInstallerFile($@"{folder}\setup.exe", "a3c1")],
        new SpecializeInstallerCommand($@"{folder}\setup.exe", "/s /norestart", folder),
        1200,
        [0, 3010],
        [folder]);

    private static StagedScript Staged(string name, int position, int minutes = 5) =>
        new(name, SetupRunnerManifestBuilder.ScriptPath(position), "77de", minutes);

    [Fact]
    public void Installers_ComeFirst_ThenScripts_NumberedFromOne()
    {
        var manifest = SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, "Stockholm", 8,
            [Installer("HP Hotkey Support", @"CloudImaging\Drivers\sp1"), Installer("Realtek", @"CloudImaging\Drivers\sp2")],
            [Staged("Wired 802.1X", 1), Staged("Inventory", 2)]);

        manifest.Items.Select(i => (i.Order, i.Kind, i.Name)).Should().Equal(
            (1, SetupRunnerItemKind.DriverInstaller, "HP Hotkey Support"),
            (2, SetupRunnerItemKind.DriverInstaller, "Realtek"),
            (3, SetupRunnerItemKind.Script, "Wired 802.1X"),
            (4, SetupRunnerItemKind.Script, "Inventory"));
        manifest.Profile.Should().Be(new SetupRunnerManifestProfile("Stockholm", 8));
        manifest.CleanupPaths.Should().Equal(@"Windows\Setup\CloudImaging");
        manifest.CreatedAt.Should().Be("2026-10-23T10:02:11Z");
        manifest.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void ScriptItems_RunPowerShellOnTheirFile_WithTheTimeoutInSeconds()
    {
        var item = SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, "Stockholm", 8, [], [Staged("Wired 802.1X", 1, minutes: 2)]).Items.Single();

        item.Files.Should().Equal(new SetupRunnerManifestFile(@"Windows\Setup\CloudImaging\Scripts\01.ps1", "77de"));
        item.Command.Should().Be(new SetupRunnerManifestCommand(@"Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\"", @"Windows\Setup\CloudImaging\Scripts"));
        item.TimeoutSeconds.Should().Be(120);
        item.SuccessExitCodes.Should().Equal(0);
        item.CleanupPaths.Should().BeEmpty();
    }

    [Fact]
    public void InstallerItems_KeepTheirFields()
    {
        var item = SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, null, null, [Installer("HP Hotkey Support", @"CloudImaging\Drivers\sp1")], []).Items.Single();

        item.Files.Should().Equal(new SetupRunnerManifestFile(@"CloudImaging\Drivers\sp1\setup.exe", "a3c1"));
        item.Command.Arguments.Should().Be("/s /norestart");
        item.TimeoutSeconds.Should().Be(1200);
        item.SuccessExitCodes.Should().Equal(0, 3010);
        item.CleanupPaths.Should().Equal(@"CloudImaging\Drivers\sp1");
    }

    [Fact]
    public void DriverWorkOnly_HasNoProfile()
    {
        SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, null, null, [Installer("A", @"CloudImaging\Drivers\a")], []).Profile.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Windows\setup.exe")]
    [InlineData(@"\Windows\setup.exe")]
    [InlineData(@"CloudImaging\..\..\setup.exe")]
    [InlineData(@"D:setup.exe")]
    public void ARootedPath_OrOneWithParentSegments_IsRefused(string path)
    {
        var installer = new SpecializeInstaller("A", [new SpecializeInstallerFile(path, "a")], new SpecializeInstallerCommand("msiexec.exe", "/qn", @"CloudImaging"), 60, [0], []);

        var build = () => SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, null, null, [installer], []);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Serialized_IsUtf8WithoutBom_CamelCase_WithStringEnums()
    {
        var bytes = SetupRunnerManifestBuilder.Serialize(SetupRunnerManifestBuilder.Build(SessionId, CreatedAt, "Stockholm", 8, [Installer("A", @"CloudImaging\Drivers\a")], [Staged("B", 1)]));

        bytes.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("sessionId").GetString().Should().Be("5b7e0f0e-6a0c-4b8e-9a0d-1c2b3d4e5f60");
        root.GetProperty("profile").GetProperty("version").GetInt32().Should().Be(8);
        root.GetProperty("items")[0].GetProperty("kind").GetString().Should().Be("DriverInstaller");
        root.GetProperty("items")[1].GetProperty("kind").GetString().Should().Be("Script");
        root.GetProperty("items")[1].GetProperty("command").GetProperty("workingDirectory").GetString().Should().Be(@"Windows\Setup\CloudImaging\Scripts");
    }

    [Fact]
    public void Sha256_IsLowercaseHexOfTheBytes()
    {
        SetupRunnerManifestBuilder.Sha256(Encoding.ASCII.GetBytes("abc")).Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}

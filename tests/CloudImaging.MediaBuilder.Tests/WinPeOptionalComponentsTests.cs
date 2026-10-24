using System.IO;
using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// PowerShell in every boot image: WinPE-Scripting and WinPE-PowerShell follow WMI and NetFx for
/// both architectures, a missing cab stops generation with a message naming it, and the manifest
/// records that PowerShell is present.
/// </summary>
public sealed class WinPeOptionalComponentsTests
{
    private const string Adk = @"C:\ADK";

    [Fact]
    public void Components_AreInjectedInTheRequiredOrder()
    {
        WinPeOptionalComponents.InjectionOrder.Should().Equal("WinPE-WMI", "WinPE-NetFx", "WinPE-Scripting", "WinPE-PowerShell");
        WinPeOptionalComponents.PowerShell.Should().Equal("WinPE-Scripting", "WinPE-PowerShell");
        WinPeOptionalComponents.PowerShellProgressText.Should().Be("Adding WinPE PowerShell support");
    }

    [Theory]
    [InlineData(MachineArchitecture.X64, "amd64")]
    [InlineData(MachineArchitecture.Arm64, "arm64")]
    public void EachComponent_HasABaseCabAndAnEnUsLanguageCab(MachineArchitecture architecture, string folder)
    {
        var (baseCab, languageCab) = WinPeOptionalComponents.PackagePaths(Adk, architecture, "WinPE-PowerShell");

        baseCab.Should().Be($@"C:\ADK\Windows Preinstallation Environment\{folder}\WinPE_OCs\WinPE-PowerShell.cab");
        languageCab.Should().Be($@"C:\ADK\Windows Preinstallation Environment\{folder}\WinPE_OCs\en-us\WinPE-PowerShell_en-us.cab");
        WinPeOptionalComponents.PackagePaths(Adk, architecture, "WinPE-Scripting").BaseCab.Should().EndWith(@"WinPE_OCs\WinPE-Scripting.cab");
    }

    [Fact]
    public void AMissingCab_NamesTheComponentAndBothFiles()
    {
        var message = WinPeOptionalComponents.MissingMessage(@"C:\ADK\Windows Preinstallation Environment\amd64\WinPE_OCs", "WinPE-Scripting");

        message.Should().Contain("WinPE-Scripting optional component").And.Contain("\"WinPE-Scripting.cab\"").And.Contain(@"en-us\WinPE-Scripting_en-us.cab").And.Contain("adkwinpesetup.exe");
        message.Should().MatchRegex("^[\\x00-\\x7F]*$");
    }

    [Fact]
    public void TheManifest_RecordsPowerShell()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            BootImageManifestService.Build(folder, "20261023", 0, null, null, null, powerShellIncluded: true).PowerShellIncluded.Should().BeTrue();
            BootImageManifestService.Build(folder, "20261023", 0, null, null, null).PowerShellIncluded.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

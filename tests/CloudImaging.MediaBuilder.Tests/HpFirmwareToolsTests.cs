using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

public sealed class HpFirmwareToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ci-hp-tools-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static byte[] Package(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void RequiredVersions_ReadsTheExactModuleVersionsOfTheLibrary()
    {
        var nuspec = """
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
              <metadata>
                <id>HPCMSL</id>
                <version>1.9.0</version>
                <dependencies>
                  <dependency id="HP.Firmware" version="[1.9.0]" />
                  <dependency id="HP.Private" version="[1.9.0]" />
                  <dependency id="HP.Softpaq" version="[1.8.6, )" />
                </dependencies>
              </metadata>
            </package>
            """;

        var versions = HpFirmwareTools.RequiredVersions(Package(("HPCMSL.nuspec", nuspec)));

        versions.Should().BeEquivalentTo(new Dictionary<string, string> { ["HP.Firmware"] = "1.9.0", ["HP.Private"] = "1.9.0" }, "a range is not an exact version and is left out");
    }

    [Fact]
    public void Extract_LeavesOutNuGetFiles_AndDecodesNames()
    {
        HpFirmwareTools.Extract(Package(("HP.Firmware.psd1", "manifest"), ("en-US/about%20HP.txt", "help"), ("HP.Firmware.nuspec", "x"), ("[Content_Types].xml", "x"), ("_rels/.rels", "x"), ("package/services/metadata/core-properties/1.psmdcp", "x")), _folder);

        Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_folder, f))
            .Should().BeEquivalentTo("HP.Firmware.psd1", Path.Combine("en-US", "about HP.txt"));
    }

    [Fact]
    public void Extract_RefusesAnEntryOutsideTheFolder()
    {
        var act = () => HpFirmwareTools.Extract(Package(("..%2F..%2Fevil.ps1", "x")), _folder);

        act.Should().Throw<InvalidDataException>();
    }

    // A NuGet library Microsoft signed itself, so the check sees a real signature of the file's own.
    private static string MicrosoftSignedLibrary => typeof(NullLogger).Assembly.Location;

    [Fact]
    public void SignerOrganization_ReadsTheFilesOwnSignature()
    {
        HpFirmwareTools.SignerOrganization(MicrosoftSignedLibrary).Should().Be("Microsoft Corporation");
    }

    [Fact]
    public void SignerOrganization_IsNull_ForAChangedOrUnsignedFile()
    {
        Directory.CreateDirectory(_folder);
        var changed = Path.Combine(_folder, "changed.dll");
        var bytes = File.ReadAllBytes(MicrosoftSignedLibrary);
        bytes[0x4E] ^= 0x20;
        File.WriteAllBytes(changed, bytes);
        var unsigned = Path.Combine(_folder, "unsigned.ps1");
        File.WriteAllText(unsigned, "Write-Output 'x'");

        HpFirmwareTools.SignerOrganization(changed).Should().BeNull("a byte changed after signing breaks the signature");
        HpFirmwareTools.SignerOrganization(unsigned).Should().BeNull();
    }

    [Fact]
    public void VerifySignatures_RefusesFilesSignedBySomeoneElse()
    {
        Directory.CreateDirectory(_folder);
        File.Copy(MicrosoftSignedLibrary, Path.Combine(_folder, "Other.dll"));
        File.WriteAllText(Path.Combine(_folder, "license.txt"), "not checked");

        var act = () => HpFirmwareTools.VerifySignatures(_folder, CancellationToken.None);

        act.Should().Throw<InvalidOperationException>().WithMessage("1 file(s) of the HP firmware tools are not signed by HP, for example \"Other.dll\"*");
    }

    [Fact]
    public void Generate_WaitsForHpsLicence_WhenTheToolsAreIncluded()
    {
        var vm = new GenerateBootImageViewModel(
            new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance),
            new EntraAuthenticationService("test-client", "test-tenant", "api://test-client/.default", NullLogger<EntraAuthenticationService>.Instance),
            new GitHubReleasesClient(new HttpClient(), NullLogger<GitHubReleasesClient>.Instance),
            navigateBack: () => { });
        var before = vm.CanGenerate;

        vm.IncludeHpFirmwareTools = true;
        vm.CanGenerate.Should().BeFalse();
        vm.AcceptHpLicence = true;
        vm.CanGenerate.Should().Be(before);

        vm.IsArm64Selected = true;
        vm.IncludeHpFirmwareTools.Should().BeFalse("HP's tools are x64 only");
        vm.IncludeHpFirmwareTools = false;
        vm.AcceptHpLicence.Should().BeFalse("turning the tools off withdraws the acceptance");
    }
}

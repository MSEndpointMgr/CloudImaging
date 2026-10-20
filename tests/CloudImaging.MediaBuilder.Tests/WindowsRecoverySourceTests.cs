using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// A Windows RE boot image is built from a Windows installation source and the ADK's optional
/// components, so the source must match the ADK build, the target architecture and an ADK language.
/// </summary>
public sealed class WindowsRecoverySourceTests
{
    private static readonly WimImageInfo Adk = new(9, 26100, 1, "en-US");
    private static readonly string[] English = ["en-us"];

    [Fact]
    public void AMatchingSource_HasNoProblems()
    {
        WindowsRecoverySource.Evaluate(new WimImageInfo(9, 26100, 4061, "en-US"), Adk, English, MachineArchitecture.X64)
            .Should().BeEmpty();
    }

    [Fact]
    public void ABuildMismatch_NamesBothBuildsAndTheFix()
    {
        var problem = WindowsRecoverySource.Evaluate(new WimImageInfo(9, 22631, 1, "en-US"), Adk, English, MachineArchitecture.X64).Should().ContainSingle().Subject;

        problem.Problem.Should().Be("Windows image build 22631 doesn't match Windows ADK build 26100.");
        problem.Fix.Should().Be("Use a build 26100 source.");
    }

    [Fact]
    public void AnArchitectureMismatch_AndALanguageMismatch_AreBothReported()
    {
        var problems = WindowsRecoverySource.Evaluate(new WimImageInfo(9, 26100, 1, "sv-SE"), Adk, English, MachineArchitecture.Arm64);

        problems.Select(p => p.Problem).Should().Equal(
            "This source is x64, but the target architecture is ARM64.",
            "Windows image language (sv-SE) doesn't match the Windows ADK (en-us).");
    }

    [Fact]
    public void ImageInfo_IsReadFromWimXml()
    {
        var xml = System.Text.Encoding.Unicode.GetBytes("\uFEFF<WIM><IMAGE INDEX=\"1\"><NAME>Windows 11 Enterprise</NAME><WINDOWS><ARCH>12</ARCH><LANGUAGES><LANGUAGE>en-US</LANGUAGE><DEFAULT>en-US</DEFAULT></LANGUAGES><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD><SPBUILD>4061</SPBUILD></VERSION></WINDOWS></IMAGE></WIM>");

        WimMetadataReader.ParseImageInfo(xml).Should().Be(new WimImageInfo(12, 26100, 4061, "en-US"));
        WimMetadataReader.ParseImageInfo(xml, imageIndex: 2).Should().BeNull();
    }

    [Fact]
    public async Task GenerateIsBlocked_UntilTheWindowsSourcePassesItsChecks()
    {
        var outcome = new SourceCheckResult(string.Empty, [new SourceProblem("This ISO has no sources\\install.wim.", "Use another ISO.")]);
        var vm = new CloudImaging.MediaBuilder.ViewModels.GenerateBootImageViewModel(
            new BootImageGenerationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<BootImageGenerationService>.Instance),
            new EntraAuthenticationService("test-client", "test-tenant", "api://test-client/.default", Microsoft.Extensions.Logging.Abstractions.NullLogger<EntraAuthenticationService>.Instance),
            new GitHubReleasesClient(new System.Net.Http.HttpClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubReleasesClient>.Instance),
            navigateBack: () => { },
            sourceCheck: (_, _, _) => Task.FromResult(outcome));

        vm.CanGenerate.Should().BeTrue("Windows PE needs no source");

        vm.IsWindowsReSelected = true;
        vm.CanGenerate.Should().BeFalse("Windows RE needs a source");

        vm.WindowsSourcePath = @"D:\ISO\Windows11_MediaCreationTool.iso";
        await Task.Yield();
        vm.SourceCheckFailed.Should().BeTrue();
        vm.SourceCheckDetails.Should().Be("This ISO has no sources\\install.wim. Use another ISO.");
        vm.CanGenerate.Should().BeFalse();

        outcome = new SourceCheckResult("Build 26100.4061, x64, en-US. Matches the Windows ADK.", []);
        vm.WindowsSourcePath = @"D:\ISO\Win11_24H2_English_x64.iso";
        await Task.Yield();
        vm.SourceCheckPassed.Should().BeTrue();
        vm.CanGenerate.Should().BeTrue();
    }
}

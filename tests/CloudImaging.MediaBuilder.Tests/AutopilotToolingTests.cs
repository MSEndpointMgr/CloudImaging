using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Autopilot hash tooling: which builds can include it, and that the choice reaches the elevated
/// worker and the embedded manifest.
/// </summary>
public sealed class AutopilotToolingTests
{
    private const string Adk = @"C:\ADK";

    private static HashSet<string> AllFiles(MachineArchitecture architecture) => new(StringComparer.OrdinalIgnoreCase)
    {
        AutopilotTooling.Oa3ToolSourcePath(Adk, architecture),
        AutopilotTooling.SecureStartupPackagePaths(Adk, architecture).BaseCab,
        AutopilotTooling.SecureStartupPackagePaths(Adk, architecture).LanguageCab,
        AutopilotTooling.PlatformIdPackagePath(Adk, architecture),
    };

    private static string? Reason(MachineArchitecture target = MachineArchitecture.X64, string? adk = Adk, Func<string, bool>? exists = null) =>
        AutopilotTooling.GetUnavailableReason(adk, target, exists ?? AllFiles(target).Contains);

    [Theory]
    [InlineData(MachineArchitecture.X64, @"C:\ADK\Deployment Tools\amd64\Licensing\OA30\oa3tool.exe", @"C:\ADK\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-PlatformId.cab")]
    [InlineData(MachineArchitecture.Arm64, @"C:\ADK\Deployment Tools\arm64\Licensing\OA30\oa3tool.exe", @"C:\ADK\Windows Preinstallation Environment\arm64\WinPE_OCs\WinPE-PlatformId.cab")]
    public void EveryArchitecture_IsAvailable_WhenTheAdkHasTheFiles(MachineArchitecture architecture, string oa3Tool, string platformId)
    {
        Reason(architecture).Should().BeNull();
        AutopilotTooling.Oa3ToolSourcePath(Adk, architecture).Should().Be(oa3Tool);
        AutopilotTooling.PlatformIdPackagePath(Adk, architecture).Should().Be(platformId);
    }

    [Fact]
    public void MissingAdk_IsNotAvailable()
    {
        Reason(adk: null).Should().Contain("Windows ADK");
    }

    [Theory]
    [InlineData("oa3tool.exe", "OA3Tool")]
    [InlineData("WinPE-SecureStartup.cab", "WinPE-SecureStartup")]
    [InlineData("WinPE-SecureStartup_en-us.cab", "WinPE-SecureStartup")]
    [InlineData("WinPE-PlatformId.cab", "WinPE-PlatformId")]
    public void AMissingSourceFile_IsNamed(string missingFile, string expectedInReason)
    {
        Reason(exists: path => AllFiles(MachineArchitecture.X64).Contains(path) && !path.EndsWith(missingFile, StringComparison.OrdinalIgnoreCase))
            .Should().Contain(expectedInReason);
    }

    [Fact]
    public void Manifest_RecordsWhetherTheToolingWasIncluded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            BootImageManifestService.Build(dir.FullName, "v1", 0, null, null, null, autopilotToolingIncluded: true)
                .AutopilotToolingIncluded.Should().BeTrue();
            BootImageManifestService.Build(dir.FullName, "v1", 0, null, null, null)
                .AutopilotToolingIncluded.Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThreadsIncludeAutopilotTooling_IntoElevatedWorkerParams()
    {
        string? capturedParamsJson = null;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                capturedParamsJson = File.ReadAllText(files[0]);
                File.WriteAllText(files[2], """{"Success":true,"WimPath":"C:\\out\\cloud-imaging-boot.wim","Sha256Hash":"deadbeef"}""");
                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            });

        await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath(),
            includeAutopilotTooling: true);

        capturedParamsJson.Should().Contain("\"IncludeAutopilotTooling\":true");
    }
}

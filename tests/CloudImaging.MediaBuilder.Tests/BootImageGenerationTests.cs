using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for Media Builder boot image generation (T057, FR-051, FR-067, FR-070).
/// </summary>
public sealed class BootImageGenerationTests
{
    // ── ADK detection ─────────────────────────────────────────────────────────

    [Fact]
    public void BootImageGenerationService_CanBeInstantiated()
    {
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        svc.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAsync_ThrowsAndLeavesNoOutputWim_WhenPrerequisitesAreMissing()
    {
        // Whether ADK is installed varies by machine (it genuinely IS installed on some
        // developer/imaging workstations), so the specific failure point (ADK detection vs.
        // DISM mount vs. missing client path) is environment-dependent and not asserted here.
        // What must always hold regardless of environment: generation never succeeds against a
        // bogus client binaries path, and it never leaves a partial/corrupt WIM artifact behind
        // in the output directory for the technician to mistakenly pick up.
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var outputDir = Directory.CreateTempSubdirectory("ci-genoutput-").FullName;
        try
        {
            Func<Task> act = async () => await svc.GenerateAsync(
                clientBinariesPath: Path.Combine(Path.GetTempPath(), $"ci-nonexistent-{Guid.NewGuid():N}"),
                pfxBytes: null,
                outputDirectory: outputDir);

            await act.Should().ThrowAsync<Exception>(
                "generation must not silently succeed when the client binaries path or ADK prerequisite is invalid");

            Directory.GetFiles(outputDir, "*.wim").Should().BeEmpty(
                "a failed generation must not leave a WIM artifact behind in the output directory");
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_Throws_WhenClientBinariesFolderHasNoExe()
    {
        // Checked before the (environment-dependent) ADK check, so this is deterministic
        // regardless of whether ADK happens to be installed on the machine running the test.
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var clientDir = Directory.CreateTempSubdirectory("ci-client-empty-").FullName;
        var outputDir = Directory.CreateTempSubdirectory("ci-genoutput-").FullName;
        try
        {
            Func<Task> act = async () => await svc.GenerateAsync(
                clientBinariesPath: clientDir,
                pfxBytes: null,
                outputDirectory: outputDir);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*CloudImaging.Client.exe*");
        }
        finally
        {
            Directory.Delete(clientDir, recursive: true);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_Throws_WhenClientBinariesAreFrameworkDependent()
    {
        // A folder with CloudImaging.Client.exe but no hostfxr.dll looks exactly like a plain
        // "dotnet build"/"dotnet publish" (framework-dependent) output — the scenario that
        // produces "You must install .NET Desktop Runtime to run this application." once
        // booted in WinPE, which has no runtime of its own. Checked before the (environment-
        // dependent) ADK check, so this is deterministic regardless of whether ADK happens to
        // be installed on the machine running the test.
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var clientDir = Directory.CreateTempSubdirectory("ci-client-fxdep-").FullName;
        var outputDir = Directory.CreateTempSubdirectory("ci-genoutput-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(clientDir, "CloudImaging.Client.exe"), "fake exe");

            Func<Task> act = async () => await svc.GenerateAsync(
                clientBinariesPath: clientDir,
                pfxBytes: null,
                outputDirectory: outputDir);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*self-contained*");
        }
        finally
        {
            Directory.Delete(clientDir, recursive: true);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    // ── PFX embedding ─────────────────────────────────────────────────────────

    [Fact]
    public void GenerationResult_ContainsSha256Hash()
    {
        // The GenerationResult record must have both WimPath and Sha256Hash
        var result = new BootImageGenerationService.GenerationResult(
            WimPath:    @"C:\output\cloud-imaging-boot.wim",
            Sha256Hash: "aabbccddee001122334455667788990011223344556677889900aabb00112233");

        result.WimPath.Should().EndWith(".wim");
        result.Sha256Hash.Should().HaveLength(64, "SHA-256 hex is 64 characters");
    }

    // ── Progress events ───────────────────────────────────────────────────────

    [Fact]
    public void ProgressChanged_InvokesSubscriber_WhenEventIsRaised()
    {
        // ADK/DISM is required to actually drive GenerateAsync progress reporting end-to-end, so
        // this verifies the event delegate itself correctly relays (message, percent) to
        // subscribers by invoking the field-backed event directly via reflection.
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var events = new List<(string Message, int Percent)>();
        svc.ProgressChanged += (_, e) => events.Add(e);

        var field = typeof(BootImageGenerationService)
            .GetField("ProgressChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = (MulticastDelegate?)field?.GetValue(svc);
        handler.Should().NotBeNull("at least one subscriber must be registered on the backing field");
        handler!.DynamicInvoke(svc, ("Working…", 42));

        events.Should().ContainSingle().Which.Should().Be(("Working…", 42));
    }

    // ── Certificate embedding path ────────────────────────────────────────────

    [Fact]
    public void CertificateEmbeddedAt_ExpectedPath_MatchesClientStartupCoordinatorExpectation()
    {
        // FR-070/FR-071: BootImageGenerationService embeds the PFX at
        // {clientDestDir}\certificates\bootmedia.pfx (Path.Combine("certificates", "bootmedia.pfx")
        // in BootImageGenerationService.cs). CloudImaging.Client.Services.SessionStartupCoordinator
        // .PfxRelativePath (asserted independently in tests/CloudImaging.Client.Tests) MUST use
        // this exact same relative path — cross-checked here as a literal contract, since the
        // MediaBuilder test project does not reference the Client project.
        var embeddedRelativePath = Path.Combine("certificates", "bootmedia.pfx");

        embeddedRelativePath.Should().Be(@"certificates\bootmedia.pfx",
            "the cert embedded in the WIM must be at the path the Client's SessionStartupCoordinator.PfxRelativePath expects (FR-070/FR-071)");
    }
}


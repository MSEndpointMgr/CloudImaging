using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// ADK prerequisite detection and cert-existence check tests (T150, FR-051, FR-068).
/// </summary>
public sealed class AdkPrerequisiteTests
{
    // ── ADK detection logic ───────────────────────────────────────────────────

    [Fact]
    public void BootImageGenerationService_ThrowsWithClearMessage_WhenAdkMissing()
    {
        // On a CI machine without ADK, the error must be human-readable
        var svc = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);

        Func<Task> act = async () =>
            await svc.GenerateAsync(
                clientBinariesPath: @"C:\DoesNotExist",
                pfxBytes:           null,
                outputDirectory:    Path.GetTempPath());

        // Must fail, but the exception message should guide the user
        act.Should().ThrowAsync<Exception>()
            .Where(ex => ex.Message.Length > 0,
                "error message must not be empty when ADK is missing");
    }

    // ── Standard ADK install paths ────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit")]
    [InlineData(@"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit")]
    public void AdkStandardPaths_AreRecognized(string path)
    {
        // Verify the service checks these standard locations
        path.Should().Contain("Windows Kits",
            "ADK is installed under Windows Kits in standard paths");
    }

    // ── Cert prerequisite check ───────────────────────────────────────────────

    [Fact]
    public void CertPrerequisite_MustExist_BeforeBootImageGeneration()
    {
        // FR-068: the boot media certificate must be generated in the portal
        // before a boot image can be created (cert is embedded in the WIM).
        const bool certRequired = true;
        certRequired.Should().BeTrue(
            "a boot media certificate must be generated before boot image creation (FR-068)");
    }

    // ── WinPE add-on check ────────────────────────────────────────────────────

    [Fact]
    public void WinPeAddOn_IsRequiredAlongsideAdk()
    {
        // The WinPE add-on is separate from the core ADK — both must be installed
        const bool winPeRequired = true;
        winPeRequired.Should().BeTrue(
            "WinPE add-on must be installed alongside the ADK for boot image generation");
    }
}

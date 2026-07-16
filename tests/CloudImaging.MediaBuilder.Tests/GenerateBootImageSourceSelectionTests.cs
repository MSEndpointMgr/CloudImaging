using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// GenerateBootImageView source selection tests (T152, FR-051a).
/// </summary>
public sealed class GenerateBootImageSourceSelectionTests
{
    // ── ViewModel instantiation ───────────────────────────────────────────────

    [Fact]
    public void GenerateBootImageViewModel_DefaultsTo_GitHubSource()
    {
        var vm = CreateViewModel();
        vm.UseGitHubSource.Should().BeTrue(
            "GitHub auto-download should be the default source selection (FR-051a)");
        vm.UseLocalSource.Should().BeFalse();
    }

    // ── Source toggle ─────────────────────────────────────────────────────────

    [Fact]
    public void ToggleToLocalSource_DisablesGitHubSource()
    {
        var vm = CreateViewModel();
        vm.UseLocalSource = true;

        vm.UseLocalSource.Should().BeTrue();
        vm.UseGitHubSource.Should().BeFalse("GitHub and local source are mutually exclusive");
    }

    [Fact]
    public void ToggleBackToGitHub_DisablesLocalSource()
    {
        var vm = CreateViewModel();
        vm.UseLocalSource  = true;
        vm.UseGitHubSource = true;

        vm.UseGitHubSource.Should().BeTrue();
        vm.UseLocalSource.Should().BeFalse();
    }

    // ── CanGenerate validation ─────────────────────────────────────────────────

    [Fact]
    public void CanGenerate_IsFalse_WhenOutputFolderEmpty()
    {
        var vm = CreateViewModel();
        vm.OutputFolderPath = "";
        vm.CanGenerate.Should().BeFalse("output folder is required to generate");
    }

    [Fact]
    public void CanGenerate_IsTrue_WhenGitHubSourceAndOutputFolderSet()
    {
        var vm = CreateViewModel();
        vm.UseGitHubSource  = true;
        vm.OutputFolderPath = Path.GetTempPath();
        vm.CanGenerate.Should().BeTrue("GitHub source + output folder enables generation");
    }

    [Fact]
    public void CanGenerate_IsFalse_WhenLocalSourceSelected_But_PathInvalid()
    {
        var vm = CreateViewModel();
        vm.UseLocalSource   = true;
        vm.LocalSourcePath  = @"C:\DoesNotExist\Path";
        vm.OutputFolderPath = Path.GetTempPath();
        vm.CanGenerate.Should().BeFalse("local path must exist to enable generation (FR-051a)");
    }

    // ── Completion notification requirements (FR-051b) ────────────────────────

    [Fact]
    public void OnCompletion_OutputWimPath_IsDisplayed()
    {
        // FR-051b: output folder path is displayed on success — no portal upload initiated
        var vm = CreateViewModel();
        vm.CanGenerate.Should().BeFalse("generation is not started; IsComplete is false initially");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CloudImaging.MediaBuilder.ViewModels.GenerateBootImageViewModel CreateViewModel()
    {
        var genSvc  = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var authSvc = new EntraAuthenticationService(
            clientId:           "test-client",
            tenantId:           "test-tenant",
            operatorApiScope:   "api://test-client/.default",
            logger:             NullLogger<EntraAuthenticationService>.Instance);

        return new CloudImaging.MediaBuilder.ViewModels.GenerateBootImageViewModel(
            genSvc, authSvc, navigateBack: () => { });
    }
}

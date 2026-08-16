using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// GenerateBootImageViewModel support reference code tests (T160, FR-058).
/// Every Media Builder failure path must surface a support reference code so a technician
/// can quote it to support without needing log access.
/// </summary>
public sealed class GenerateBootImageSupportReferenceTests
{
    [Fact]
    public async Task GenerateAsync_SurfacesSupportReferenceCode_OnFailure()
    {
        // No Entra sign-in is performed in this test, so GetAccessTokenAsync returns null
        // and generation fails immediately — before any stage transition, i.e. during the
        // default/earliest DWN stage.
        var genService = new BootImageGenerationService(NullLogger<BootImageGenerationService>.Instance);
        var authService = new EntraAuthenticationService(
            "test-client", "test-tenant", "api://test-client/.default",
            NullLogger<EntraAuthenticationService>.Instance);
        var gitHubClient = new GitHubReleasesClient(new HttpClient(), NullLogger<GitHubReleasesClient>.Instance);

        var vm = new GenerateBootImageViewModel(genService, authService, gitHubClient, navigateBack: () => { });

        vm.GenerateCommand.Execute(null);
        await WaitUntilIdleAsync(vm);

        vm.ErrorMessage.Should().NotBeNullOrEmpty("generation must fail without a signed-in Operator API session");
        vm.ErrorMessage.Should().MatchRegex(@"CMB-GENBOOT-(DWN|APL)-\d+",
            "every Media Builder failure path must surface a support reference code (T160, FR-058)");
    }

    private static async Task WaitUntilIdleAsync(GenerateBootImageViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsGenerating && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}

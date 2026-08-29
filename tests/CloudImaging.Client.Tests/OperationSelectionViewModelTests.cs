using System.Net.Http;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for <see cref="OperationSelectionViewModel"/>'s Command Prompt support-tool gating
/// (FR-051d): the button/command must only be available when the boot image was built with the
/// Media Builder's "Enable command prompt access" opt-in, and launching must toggle the Client's
/// always-on-top MainWindow off/on around the console session.
/// </summary>
public sealed class OperationSelectionViewModelTests
{
    private static DeviceGatewayApiClient CreateGatewayClient() =>
        new(new HttpClient { BaseAddress = new Uri("https://gw.example.com") });

    [Fact]
    public void IsCommandPromptAvailable_IsFalse_ByDefault()
    {
        var vm = new OperationSelectionViewModel(CreateGatewayClient(), (_, _) => { });

        vm.IsCommandPromptAvailable.Should().BeFalse("the opt-in defaults to off (FR-051d)");
        vm.LaunchCommandPromptCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void IsCommandPromptAvailable_IsTrue_WhenOptInEnabled()
    {
        var vm = new OperationSelectionViewModel(CreateGatewayClient(), (_, _) => { }, commandPromptEnabled: true);

        vm.IsCommandPromptAvailable.Should().BeTrue();
        vm.LaunchCommandPromptCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LaunchCommandPromptCommand_TogglesMainWindowTopmost_AroundLaunch()
    {
        var topmostCalls = new List<bool>();
        var launcher = new CommandPromptLauncherService(startProcessOverride: _ =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            })!);
        var vm = new OperationSelectionViewModel(
            CreateGatewayClient(), (_, _) => { },
            commandPromptEnabled: true,
            setMainWindowTopmost: topmostCalls.Add,
            commandPromptLauncher: launcher);

        vm.LaunchCommandPromptCommand.Execute(null);

        topmostCalls.Should().Contain(false, "Topmost must be dropped while the console is open");
    }

    [Fact]
    public void LaunchCommandPromptCommand_DoesNothing_WhenSetMainWindowTopmostCallbackMissing()
    {
        // setMainWindowTopmost is optional only to simplify test construction — in the real app
        // App.xaml.cs always supplies it. Guard against a null-reference if it's ever omitted.
        var launcherInvoked = false;
        var launcher = new CommandPromptLauncherService(startProcessOverride: _ =>
        {
            launcherInvoked = true;
            throw new InvalidOperationException("Should not be reached without a Topmost callback.");
        });
        var vm = new OperationSelectionViewModel(
            CreateGatewayClient(), (_, _) => { },
            commandPromptEnabled: true,
            commandPromptLauncher: launcher);

        var act = () => vm.LaunchCommandPromptCommand.Execute(null);

        act.Should().NotThrow();
        launcherInvoked.Should().BeFalse();
    }
}

using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for <see cref="WifiConnectionViewModel"/> — drives <see cref="Views.WifiConnectionWindow"/>,
/// the WinPE Client's "Connect to Wi-Fi" support tool (FR-051d companion feature). Uses a real
/// <see cref="WirelessConnectionService"/> with <c>runNetshOverride</c> injected so no real
/// <c>netsh.exe</c>/network stack is touched.
/// </summary>
public sealed class WifiConnectionViewModelTests
{
    private const string ScanFixture = """
        SSID 1 : HomeNetwork
            Authentication          : WPA2-Personal
                 Signal             : 78%

        SSID 2 : CorpWifi
            Authentication          : WPA2-Enterprise
                 Signal             : 90%

        """;

    [Fact]
    public void Constructor_ScansImmediately_AndPopulatesNetworks_OrderedBySignalDescending()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));

        var vm = new WifiConnectionViewModel(wifiService);

        vm.Networks.Should().HaveCount(2);
        vm.Networks[0].Ssid.Should().Be("CorpWifi", "networks are ordered by signal strength descending");
        vm.IsScanning.Should().BeFalse();
    }

    [Fact]
    public void CanConnect_IsFalse_WhenNoNetworkSelected()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));
        var vm = new WifiConnectionViewModel(wifiService);

        vm.CanConnect.Should().BeFalse();
    }

    [Fact]
    public void CanConnect_IsFalse_WhenSelectedNetworkIsUnsupported()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));
        var vm = new WifiConnectionViewModel(wifiService);

        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "CorpWifi");

        vm.CanConnect.Should().BeFalse("Enterprise networks are not connectable via this simple PSK-only flow");
    }

    [Fact]
    public void CanConnect_IsTrue_WhenSupportedNetworkSelected()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));
        var vm = new WifiConnectionViewModel(wifiService);

        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "HomeNetwork");

        vm.CanConnect.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectCommand_SetsSuccessStatus_OnSuccessfulConnect()
    {
        const string connectedFixture = """
            Name                    : Wi-Fi
            State                   : connected

            """;
        var wifiService = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show networks", StringComparison.Ordinal))
                return Task.FromResult((0, ScanFixture, string.Empty));
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, connectedFixture, string.Empty));
            return Task.FromResult((0, string.Empty, string.Empty));
        });
        var vm = new WifiConnectionViewModel(wifiService)
        {
            SelectedNetwork = null,
        };
        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "HomeNetwork");

        vm.ConnectCommand.Execute("password123");
        await Task.Delay(1500); // allow the fire-and-forget async command + one poll tick to complete

        vm.HasSucceeded.Should().BeTrue();
        vm.HasError.Should().BeFalse();
        vm.StatusMessage.Should().Contain("HomeNetwork");
    }

    [Fact]
    public async Task ConnectCommand_SetsErrorStatus_WhenNoAdapterFound()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show networks", StringComparison.Ordinal))
                return Task.FromResult((0, ScanFixture, string.Empty));
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, "There is 0 interface on the system:", string.Empty));
            return Task.FromResult((0, string.Empty, string.Empty));
        });
        var vm = new WifiConnectionViewModel(wifiService);
        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "HomeNetwork");

        vm.ConnectCommand.Execute("password123");
        await Task.Delay(250);

        vm.HasError.Should().BeTrue();
        vm.HasSucceeded.Should().BeFalse();
        vm.StatusMessage.Should().Contain("adapter");
    }

    [Fact]
    public void CloseCommand_InvokesCloseRequestedCallback()
    {
        var closeCalled = false;
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, string.Empty, string.Empty)));
        var vm = new WifiConnectionViewModel(wifiService, closeRequested: () => closeCalled = true);

        vm.CloseCommand.Execute(null);

        closeCalled.Should().BeTrue();
    }
}

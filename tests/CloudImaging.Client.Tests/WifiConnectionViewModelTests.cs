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

        SSID 3 : CafeOpen
            Authentication          : Open
                 Signal             : 85%

        """;

    [Fact]
    public void Constructor_ScansImmediately_AndPopulatesNetworks_OrderedBySignalDescending()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));

        var vm = new WifiConnectionViewModel(wifiService);

        vm.Networks.Select(n => n.Ssid).Should().Equal(["CafeOpen", "HomeNetwork"], "networks are ordered by signal strength descending");
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
    public void EnterpriseNetworks_AreLeftOutOfTheList()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (_, _) =>
            Task.FromResult((0, ScanFixture, string.Empty)));
        var vm = new WifiConnectionViewModel(wifiService);

        vm.Networks.Should().NotContain(n => n.Ssid == "CorpWifi", "Enterprise networks are not connectable via this simple PSK-only flow");
        vm.EnterpriseNote.Should().Be("1 network that needs Enterprise (802.1X) sign-in is not shown. It is not supported.");
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
        await WaitUntil(() => vm.StatusMessage is not null);

        vm.HasSucceeded.Should().BeTrue();
        vm.HasConnected.Should().BeTrue();
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
        await WaitUntil(() => vm.StatusMessage is not null);

        vm.HasError.Should().BeTrue();
        vm.HasSucceeded.Should().BeFalse();
        vm.StatusMessage.Should().Contain("adapter");
    }

    [Fact]
    public void Refresh_ExplainsWhy_WhenTheBootImageHasNoWifiSupport()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (args, _) =>
            Task.FromResult((1, $"The following command was not found: {args}.", string.Empty)));

        var vm = new WifiConnectionViewModel(wifiService);

        vm.Networks.Should().BeEmpty();
        vm.HasError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("not available in this boot image");
    }

    [Fact]
    public void Refresh_ShowsANeutralNote_WhenTheAdapterHasNotFoundNetworksYet()
    {
        var wifiService = new WirelessConnectionService(runNetshOverride: (args, _) =>
            Task.FromResult(args.StartsWith("wlan show interfaces", StringComparison.Ordinal)
                ? (0, "Name                   : Wi-Fi\nState                  : disconnected", string.Empty)
                : (0, "There are 0 networks currently visible.", string.Empty)));

        var vm = new WifiConnectionViewModel(wifiService);

        vm.HasError.Should().BeFalse();
        vm.HasInfo.Should().BeTrue();
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}

using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Wpf.Ui.Controls;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Falling back to Ethernet when Wi-Fi fails: Ethernet replacing a Wi-Fi attempt at startup,
/// Check again on Operation Selection, Ethernet always winning over Wi-Fi, the footer network
/// button and adapters without a driver. Adapters and netsh are faked.
/// </summary>
public sealed class NetworkFallbackTests
{
    private const string Usb = "Realtek USB GbE Family Controller";
    private static readonly NetworkAdapterInfo Ethernet = new(Usb, NetworkAdapterKind.Ethernet, NetworkAdapterState.Connected, "10.20.4.40");
    private static readonly NetworkAdapterInfo WiFi = new("Intel(R) Wi-Fi 6E AX211 160MHz", NetworkAdapterKind.WiFi, NetworkAdapterState.Connected, "10.20.8.17");

    private const string ConnectedToOffice = """
        Name                   : Wi-Fi
        State                  : connected
        SSID                   : Office
        BSSID                  : 00:11:22:33:44:55
        """;

    /// <summary>netsh fake that records every call; <paramref name="connect"/> decides how <c>wlan connect</c> behaves.</summary>
    private static WirelessConnectionService Netsh(List<string> calls, string interfaces, Func<CancellationToken, Task<int>>? connect = null) =>
        new(runNetshOverride: async (args, ct) =>
        {
            calls.Add(args);
            if (args.StartsWith("wlan show networks", StringComparison.Ordinal))
                return (0, "SSID 1 : Office\n    Authentication          : WPA2-Personal\n         Signal             : 80%", string.Empty);
            if (args.StartsWith("wlan connect", StringComparison.Ordinal))
                return (connect is null ? 0 : await connect(ct), string.Empty, string.Empty);
            return (0, interfaces, string.Empty);
        });

    /// <summary>Managed networks as if read from the USB media; the envelope itself is covered by ManagedWifiTests.</summary>
    private static ManagedWifiService Wifi(WirelessConnectionService netsh, params string[] ssids)
    {
        var service = new ManagedWifiService(null, netsh, findBootDrive: () => null, findAdapter: () => null, addressWait: TimeSpan.FromMilliseconds(50));
        typeof(ManagedWifiService).GetField("_networks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, new ManagedWifiNetworkSet { LocationId = Guid.NewGuid(), GeneratedAt = DateTimeOffset.UtcNow, Networks = [.. ssids.Select(s => new ManagedWifiNetwork(s, false, WifiSecurityType.Wpa2Personal, "correct horse battery"))] });
        return service;
    }

    private static Task<GatewayProbeResult> Probe(GatewayReachability reachability) => Task.FromResult(new GatewayProbeResult(reachability, null));

    // Adapters

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "10.0.0.5", NetworkAdapterKind.Ethernet, NetworkAdapterState.Connected)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, OperationalStatus.Up, null, NetworkAdapterKind.Ethernet, NetworkAdapterState.NoAddress)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Down, null, NetworkAdapterKind.Ethernet, NetworkAdapterState.Disconnected)]
    [InlineData(NetworkInterfaceType.Wireless80211, OperationalStatus.Up, "10.0.0.9", NetworkAdapterKind.WiFi, NetworkAdapterState.Connected)]
    public void Classify_TellsWiredFromWireless_AndWhetherItHasAnAddress(NetworkInterfaceType type, OperationalStatus status, string? address, NetworkAdapterKind kind, NetworkAdapterState state)
    {
        var info = NetworkStatus.Classify(type, status, address is null ? null : IPAddress.Parse(address));

        info!.Kind.Should().Be(kind);
        info.State.Should().Be(state);
    }

    [Fact]
    public void Classify_IgnoresLoopbackAndTunnels_AndLinkLocalIsNotRoutable()
    {
        NetworkStatus.Classify(NetworkInterfaceType.Loopback, OperationalStatus.Up, IPAddress.Loopback).Should().BeNull();
        NetworkStatus.Classify(NetworkInterfaceType.Tunnel, OperationalStatus.Up, null).Should().BeNull();
        NetworkStatus.IsRoutableIPv4(IPAddress.Parse("169.254.10.1")).Should().BeFalse();
        NetworkStatus.IsRoutableIPv4(IPAddress.Parse("10.20.4.40")).Should().BeTrue();
    }

    [Theory]
    [InlineData("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter", new string[0], true)]
    [InlineData("USB 10/100/1000 LAN", new string[0], true)]
    [InlineData(null, new[] { @"PCI\VEN_8086&DEV_15BB&CC_020000" }, true)]
    [InlineData("Unknown device", new[] { @"USB\Class_02&SubClass_06" }, true)]
    [InlineData("Plantronics headset", new[] { @"USB\Class_01" }, false)]
    [InlineData("PCI Simple Communications Controller", new[] { @"PCI\VEN_8086&DEV_A0E0&CC_078000" }, false)]
    [InlineData("BT_FUNCTION", new[] { @"USB\Class_E0&SubClass_01&Prot_01" }, false)]
    [InlineData("Intel(R) Wireless Bluetooth(R)", new[] { @"USB\VID_8087&PID_0033" }, false)]
    [InlineData("Remote NDIS based Internet Sharing Device", new[] { @"USB\Class_E0&SubClass_01&Prot_03" }, true)]
    public void IsNetworkDevice_RecognisesAdaptersWithoutADriver(string? name, string[] ids, bool expected)
    {
        NetworkStatus.IsNetworkDevice(name, ids).Should().Be(expected);
    }

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2", true)]
    [InlineData("Microsoft Kernel Debug Network Adapter", true)]
    [InlineData("Bluetooth Device (Personal Area Network)", true)]
    [InlineData("MediaTek Wi-Fi 7 MT7925 160MHz PCIe Adapter", false)]
    [InlineData("Realtek USB GbE Family Controller", false)]
    [InlineData("Microsoft Hyper-V Network Adapter", false)]
    public void IsVirtualAdapter_LeavesOutAdaptersThatNeverCarryTraffic(string description, bool expected)
    {
        NetworkStatus.IsVirtualAdapter(description).Should().Be(expected);
    }

    // netsh

    [Fact]
    public void ParseConnectedSsid_ReadsTheNetwork_OnlyWhenConnected()
    {
        WirelessConnectionService.ParseConnectedSsid(ConnectedToOffice).Should().Be("Office");
        WirelessConnectionService.ParseConnectedSsid("Name : Wi-Fi\nState : disconnected\nSSID : Office").Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_LeavesTheConnectedNetwork_AndDoesNothingWhenNoneIs()
    {
        var calls = new List<string>();
        (await Netsh(calls, ConnectedToOffice).DisconnectAsync()).Should().Be("Office");
        calls.Should().Contain("wlan disconnect");

        calls.Clear();
        (await Netsh(calls, "Name : Wi-Fi\nState : disconnected").DisconnectAsync()).Should().BeNull();
        calls.Should().NotContain("wlan disconnect");
    }

    // Startup

    [Fact]
    public async Task Startup_StopsTryingWifi_WhenEthernetGetsAnAddress()
    {
        var calls = new List<string>();
        var connecting = new TaskCompletionSource();
        var netsh = Netsh(calls, "Name : Wi-Fi\nState : disconnected", async ct => { connecting.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; });
        var ethernetChecks = 0;
        var vm = new StartupViewModel(_ => Probe(GatewayReachability.Reachable), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => null,
            networkWait: TimeSpan.FromMilliseconds(50),
            managedWifi: Wifi(netsh, "Office"), wifiSupported: true,
            findEthernetAdapter: () => connecting.Task.IsCompleted && ++ethernetChecks > 1 ? $"{Usb} (10.20.4.40)" : null);
        var sawHint = false;
        vm.PropertyChanged += (_, e) => sawHint |= e.PropertyName == nameof(StartupViewModel.IsTryingWifi) && vm.IsTryingWifi;

        var readiness = await vm.RunAsync();

        sawHint.Should().BeTrue("the screen says Ethernet can be plugged in while Wi-Fi is tried");
        vm.IsTryingWifi.Should().BeFalse();
        readiness.CanReachService.Should().BeTrue();
        readiness.WifiJoin.Should().BeNull();
        vm.NetworkCheck.Detail.Should().Be($"Ethernet: {Usb} (10.20.4.40)");
    }

    [Fact]
    public async Task Recheck_DropsWifi_WhenEthernetHasAnAddress()
    {
        var calls = new List<string>();
        var ethernet = false;
        var vm = new StartupViewModel(_ => Probe(GatewayReachability.Reachable), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => "Wi-Fi (10.20.8.17)",
            networkWait: TimeSpan.FromMilliseconds(50),
            managedWifi: Wifi(Netsh(calls, ConnectedToOffice), "Office"), wifiSupported: true,
            findEthernetAdapter: () => ethernet ? $"{Usb} (10.20.4.40)" : null);
        var before = new StartupReadiness(1, GatewayReachability.Reachable, null, WifiSupported: true, new ManagedWifiJoinResult(ManagedWifiOutcome.Connected, "Office"));

        ethernet = true;
        var after = await vm.RecheckNetworkAsync(before);

        calls.Should().Contain("wlan disconnect", "Ethernet always wins");
        after.WifiJoin.Should().BeNull();
        after.Rechecked.Should().BeTrue();
        after.LocalDiskCount.Should().Be(1);
    }

    [Fact]
    public async Task Startup_NamesAnAdapterWithoutADriver()
    {
        var vm = new StartupViewModel(_ => Probe(GatewayReachability.Unreachable), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => null,
            networkWait: TimeSpan.FromMilliseconds(50),
            findAdaptersWithoutDriver: () => ["ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter"]);

        var readiness = await vm.RunAsync();

        readiness.Gateway.Should().Be(GatewayReachability.NoNetwork);
        readiness.AdaptersWithoutDriver.Should().Equal("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter");
        vm.NetworkCheck.Detail.Should().Be("No IP address. ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter has no driver in the boot image.");
    }

    // Operation Selection

    private static OperationSelectionViewModel Selection(
        StartupReadiness readiness,
        Func<CancellationToken, Task<StartupReadiness>>? recheck = null,
        Func<NetworkSnapshot>? network = null,
        ManagedWifiService? managedWifi = null,
        TimeSpan? messageFade = null) =>
        new(new DeviceGatewayApiClient(new HttpClient { BaseAddress = new Uri("https://gw.example.com") }), (_, _) => { },
            readiness: readiness, recheck: recheck, managedWifi: managedWifi,
            readNetwork: network ?? (() => NetworkSnapshot.Empty),
            messageFade: messageFade ?? TimeSpan.FromMinutes(1), networkSettle: TimeSpan.Zero);

    private static readonly StartupReadiness Offline = new(1, GatewayReachability.NoNetwork, null, WifiSupported: true, new ManagedWifiJoinResult(ManagedWifiOutcome.NoneInRange));

    [Fact]
    public void Blocked_OffersCheckAgain_NextToConnectToWifi()
    {
        var vm = Selection(Offline, _ => Task.FromResult(Offline));

        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Error);
        vm.ConnectivityHeading.Should().Be("No network connection");
        vm.ConnectivityDetail.Should().Be("No saved Wi-Fi network is in range. Plug in Ethernet and select Check again, or connect to Wi-Fi.");
        vm.ShowBannerWifiAction.Should().BeTrue();
        vm.ShowBannerRecheck.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAgain_ShowsProgressInTheCard_ThenUnlocksWithASuccessMessage()
    {
        (string? Heading, string? Detail, bool Actions) during = default;
        OperationSelectionViewModel? vm = null;
        vm = Selection(Offline,
            _ =>
            {
                during = (vm!.ConnectivityHeading, vm.ConnectivityDetail, vm.ShowBannerRecheck);
                return Task.FromResult(Offline with { Gateway = GatewayReachability.Reachable, WifiJoin = null, Rechecked = true });
            },
            () => new NetworkSnapshot([Ethernet]));

        await vm.RecheckAsync();

        during.Should().Be(("Ethernet connected", $"{Usb} (10.20.4.40). Checking the Cloud Imaging service\u2026", false));
        vm.IsServiceBlocked.Should().BeFalse();
        vm.SelectedOperation.Should().Be("Imaging");
        vm.CanContinue.Should().BeTrue();
        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Success);
        vm.ConnectivityHeading.Should().Be("Connected through Ethernet");
        vm.ConnectivityDetail.Should().Be($"{Usb} (10.20.4.40)");
        vm.NetworkLabel.Should().Be("Ethernet");
        vm.IsNetworkMissing.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAgain_SuccessMessageFades()
    {
        var vm = Selection(Offline, _ => Task.FromResult(Offline with { Gateway = GatewayReachability.Reachable, Rechecked = true }),
            () => new NetworkSnapshot([Ethernet]), messageFade: TimeSpan.FromMilliseconds(20));

        await vm.RecheckAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.HasConnectivityMessage && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        vm.HasConnectivityMessage.Should().BeFalse();
        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.None);
    }

    [Fact]
    public async Task CheckAgain_WithStillNoNetwork_SaysNoAdapterGotAnAddress()
    {
        var vm = Selection(Offline, _ => Task.FromResult(Offline with { WifiJoin = null, Rechecked = true }));

        await vm.RecheckAsync();

        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Error);
        vm.ConnectivityHeading.Should().Be("No network connection");
        vm.ConnectivityDetail.Should().Be("No adapter got an IP address. Check the cable, or add the adapter's driver to the boot image. Then select Check again.");
        vm.ShowBannerRecheck.Should().BeTrue();
    }

    [Fact]
    public void Blocked_ByAnAdapterWithoutADriver_IsAWarningThatNamesIt()
    {
        var vm = Selection(Offline with { AdaptersWithoutDriver = ["ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter"] }, _ => Task.FromResult(Offline));

        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Warning);
        vm.ConnectivityHeading.Should().Be("Network adapter without a driver");
        vm.ConnectivityDetail.Should().StartWith("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter was found, but the boot image has no driver for it.");
    }

    [Fact]
    public async Task EthernetAlwaysWins_LeavesWifi_AndSaysSo()
    {
        var calls = new List<string>();
        var online = new StartupReadiness(1, GatewayReachability.Reachable, null, WifiSupported: true, new ManagedWifiJoinResult(ManagedWifiOutcome.Connected, "Office"));
        var vm = Selection(online, network: () => new NetworkSnapshot([Ethernet, WiFi]), managedWifi: Wifi(Netsh(calls, ConnectedToOffice), "Office"));

        await vm.OnNetworkChangedAsync();

        calls.Should().Contain("wlan disconnect");
        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Success);
        vm.ConnectivityHeading.Should().Be("Switched to Ethernet");
        vm.ConnectivityDetail.Should().Be($"{Usb} is in use. Wi-Fi Office was disconnected.");
        vm.ConnectedWifiSsid.Should().BeNull();
    }

    [Fact]
    public async Task EthernetAlwaysWins_WaitsForCheckAgain_WhileTheServiceIsBlocked()
    {
        var calls = new List<string>();
        var vm = Selection(Offline, _ => Task.FromResult(Offline), () => new NetworkSnapshot([Ethernet, WiFi]), Wifi(Netsh(calls, ConnectedToOffice), "Office"));

        await vm.OnNetworkChangedAsync();

        calls.Should().NotContain("wlan disconnect");
        vm.ConnectivityKind.Should().Be(ConnectivityMessageKind.Error);
        vm.NetworkLabel.Should().Be("Ethernet", "the button follows the adapters even before Check again");
    }

    [Fact]
    public void NetworkRows_ShowOneLineForEthernet_AndOneForWifi()
    {
        var rows = OperationSelectionViewModel.BuildNetworkRows(new NetworkSnapshot(
        [
            new("Intel(R) Ethernet Connection I219-LM", NetworkAdapterKind.Ethernet, NetworkAdapterState.Disconnected),
            Ethernet,
            new(WiFi.Name, NetworkAdapterKind.WiFi, NetworkAdapterState.Disconnected),
        ]), wifiSsid: null, wifiSupported: true);

        rows.Select(r => (r.Symbol, r.Title, r.Detail, r.Badge, r.IsWarning)).Should().Equal(
            (SymbolRegular.PlugConnected24, "Ethernet", $"{Usb} (10.20.4.40)", "In use", false),
            (SymbolRegular.WifiOff24, "Wi-Fi", "Not connected", (string?)null, false));
    }

    [Fact]
    public void NetworkRows_NameAnEthernetAdapterWithoutADriver_OverAnUnpluggedOne()
    {
        var rows = OperationSelectionViewModel.BuildNetworkRows(new NetworkSnapshot(
        [
            new("Intel(R) Ethernet Connection I219-LM", NetworkAdapterKind.Ethernet, NetworkAdapterState.Disconnected),
            new("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter", NetworkAdapterKind.Ethernet, NetworkAdapterState.NoDriver),
        ]), wifiSsid: null, wifiSupported: false);

        rows.Should().ContainSingle("Windows PE without a Wi-Fi adapter shows no Wi-Fi line")
            .Which.Should().Be(new NetworkPanelRow(SymbolRegular.Warning24, "Ethernet", "ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter has no driver in the boot image", IsWarning: true));
    }

    [Theory]
    [InlineData(95, SymbolRegular.Wifi124)]
    [InlineData(60, SymbolRegular.Wifi224)]
    [InlineData(35, SymbolRegular.Wifi324)]
    [InlineData(10, SymbolRegular.Wifi424)]
    public void SignalSymbol_ShowsMoreArcs_ForAStrongerSignal(int signal, SymbolRegular expected)
    {
        new VisibleWifiNetwork("Office", signal, WifiAuthKind.PersonalPsk, true).SignalSymbol.Should().Be(expected);
    }

    [Fact]
    public async Task WifiWindow_LeavesOutEnterpriseNetworks_AndSaysHowMany()
    {
        const string scan = "SSID 1 : Office\n    Authentication          : WPA2-Personal\n         Signal             : 80%\n\n"
            + "SSID 2 : Corp\n    Authentication          : WPA2-Enterprise\n         Signal             : 90%\n\n"
            + "SSID 3 : Corp IoT\n    Authentication          : WPA2-Enterprise\n         Signal             : 60%";
        var vm = new WifiConnectionViewModel(new WirelessConnectionService(runNetshOverride: (_, _) => Task.FromResult((0, scan, string.Empty))));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsScanning && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        vm.Networks.Select(n => n.Ssid).Should().Equal("Office");
        vm.HasEnterpriseNote.Should().BeTrue();
        vm.EnterpriseNote.Should().Be("2 networks that need Enterprise (802.1X) sign-in are not shown. They are not supported.");
    }

    [Fact]
    public async Task NetworkButton_NamesTheWifiNetwork_WhenThereIsNoEthernet()
    {
        var online = new StartupReadiness(1, GatewayReachability.Reachable, null, WifiSupported: true);
        var vm = Selection(online, network: () => new NetworkSnapshot([WiFi]), managedWifi: Wifi(Netsh([], ConnectedToOffice), "Office"));

        await vm.RefreshNetworkAsync();

        vm.NetworkLabel.Should().Be("Office");
        vm.NetworkSymbol.Should().Be(SymbolRegular.Wifi124);
        vm.NetworkRows.Single(r => r.Title == "Wi-Fi").Detail.Should().Be($"Office, {WiFi.Name} (10.20.8.17)");
    }
}

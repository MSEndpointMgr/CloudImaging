using System.IO;
using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for <see cref="WirelessConnectionService"/> — the WinPE Client's "Connect to Wi-Fi"
/// support tool (netsh-based scan/connect, FR-051d companion feature). Uses fixture text
/// (English/en-us locale, matching this project's WinPE build) instead of a real <c>netsh.exe</c>
/// invocation, and injects <c>runNetshOverride</c> to avoid ever touching the real network stack
/// or the developer machine's Wi-Fi state.
/// </summary>
public sealed class WirelessConnectionServiceTests
{
    private const string ScanFixture = """
        Interfaces on wlan0:

        There are 3 networks currently visible.

        SSID 1 : HomeNetwork
            Network type            : Infrastructure
            Authentication          : WPA2-Personal
            Encryption              : CCMP
            BSSID 1                 : 00:11:22:33:44:55
                 Signal             : 78%
                 Radio type         : 802.11ac
                 Channel            : 36

        SSID 2 : GuestOpen
            Network type            : Infrastructure
            Authentication          : Open
            Encryption              : None
            BSSID 1                 : 00:11:22:33:44:66
                 Signal             : 55%
                 Radio type         : 802.11n
                 Channel            : 6

        SSID 3 : CorpWifi
            Network type            : Infrastructure
            Authentication          : WPA2-Enterprise
            Encryption              : CCMP
            BSSID 1                 : 00:11:22:33:44:77
                 Signal             : 90%
                 Radio type         : 802.11ac
                 Channel            : 149

        """;

    private const string InterfacesFixtureConnected = """
        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description             : Intel(R) Wireless-AC 9560
            GUID                    : 00000000-0000-0000-0000-000000000000
            Physical address        : 00:11:22:33:44:55
            State                   : connected
            SSID                    : HomeNetwork

        """;

    private const string InterfacesFixtureDisconnected = """
        There is 1 interface on the system:

            Name                   : Wi-Fi
            State                   : disconnected

        """;

    [Fact]
    public void ParseNetworks_ExtractsSsidSignalAndAuthKind_ForEachNetwork()
    {
        var networks = WirelessConnectionService.ParseNetworks(ScanFixture);

        networks.Should().HaveCount(3);
        networks.Should().ContainSingle(n => n.Ssid == "HomeNetwork" && n.SignalPercent == 78 && n.AuthKind == WifiAuthKind.PersonalPsk && n.IsSupported);
        networks.Should().ContainSingle(n => n.Ssid == "GuestOpen" && n.SignalPercent == 55 && n.AuthKind == WifiAuthKind.Open && n.IsSupported);
        networks.Should().ContainSingle(n => n.Ssid == "CorpWifi" && n.SignalPercent == 90 && n.AuthKind == WifiAuthKind.Enterprise && !n.IsSupported);
    }

    [Fact]
    public void ParseNetworks_MarksEnterpriseNetworks_NotSupported_ButStillReturnsThem()
    {
        // Decision from planning: Enterprise/802.1X networks are shown (greyed-out with a "Not
        // supported" note), never hidden entirely.
        var networks = WirelessConnectionService.ParseNetworks(ScanFixture);

        var enterprise = networks.Single(n => n.Ssid == "CorpWifi");
        enterprise.IsSupported.Should().BeFalse();
        enterprise.NotSupportedLabel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ParseNetworks_ClassifiesWpa3Personal_AsPersonalPsk()
    {
        const string fixture = """
            SSID 1 : Wpa3Network
                Authentication          : WPA3-Personal
                     Signal             : 40%

            """;

        var networks = WirelessConnectionService.ParseNetworks(fixture);

        networks.Should().ContainSingle(n => n.Ssid == "Wpa3Network" && n.AuthKind == WifiAuthKind.PersonalPsk && n.IsSupported);
    }

    [Fact]
    public void ParseNetworks_ReturnsEmpty_ForEmptyOutput()
    {
        WirelessConnectionService.ParseNetworks(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ParseInterfaceName_ExtractsFirstInterfaceName()
    {
        WirelessConnectionService.ParseInterfaceName(InterfacesFixtureConnected).Should().Be("Wi-Fi");
    }

    [Fact]
    public void ParseInterfaceName_ReturnsNull_WhenNoInterfacePresent()
    {
        WirelessConnectionService.ParseInterfaceName("There is 0 interface on the system:").Should().BeNull();
    }

    [Fact]
    public void IsConnected_ReturnsTrue_ForConnectedState()
    {
        WirelessConnectionService.IsConnected(InterfacesFixtureConnected).Should().BeTrue();
    }

    [Fact]
    public void IsConnected_ReturnsFalse_ForDisconnectedState()
    {
        WirelessConnectionService.IsConnected(InterfacesFixtureDisconnected).Should().BeFalse();
    }

    [Fact]
    public void BuildProfileXml_UsesOpenAuthentication_WhenPasswordIsNullOrEmpty()
    {
        var xml = WirelessConnectionService.BuildProfileXml("GuestOpen", null);

        xml.Should().Contain("<authentication>open</authentication>");
        xml.Should().NotContain("sharedKey", "an open network profile must never include a key element");
    }

    [Fact]
    public void BuildProfileXml_UsesWpa2Psk_AndEmbedsKey_WhenPasswordProvided()
    {
        var xml = WirelessConnectionService.BuildProfileXml("HomeNetwork", "s3cr3t!");

        xml.Should().Contain("<authentication>WPA2PSK</authentication>");
        xml.Should().Contain("<keyMaterial>s3cr3t!</keyMaterial>");
    }

    [Fact]
    public void BuildProfileXml_EscapesXmlSpecialCharacters_InSsidAndPassword()
    {
        var xml = WirelessConnectionService.BuildProfileXml("Guest & Co <Wifi>", "p&ss\"word");

        xml.Should().NotContain("Guest & Co <Wifi>", "raw unescaped SSID must not appear verbatim in the XML");
        xml.Should().Contain("&amp;");
    }

    [Fact]
    public async Task ScanAsync_ReturnsEmptyList_WhenNetshFails_AndDoesNotThrow()
    {
        var svc = new WirelessConnectionService(runNetshOverride: (_, _) => Task.FromResult((1, string.Empty, "access denied")));

        var result = await svc.ScanAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanAsync_ReturnsParsedNetworks_OnSuccess()
    {
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            args.Should().Be("wlan show networks mode=bssid");
            return Task.FromResult((0, ScanFixture, string.Empty));
        });

        var result = await svc.ScanAsync();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ConnectAsync_ReturnsNoAdapter_WhenNoInterfacePresent()
    {
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
            Task.FromResult((0, "There is 0 interface on the system:", string.Empty)));

        var result = await svc.ConnectAsync("HomeNetwork", "password");

        result.Should().Be(WifiConnectResult.NoAdapter);
    }

    [Fact]
    public async Task ConnectAsync_ReturnsFailed_WhenAddProfileFails()
    {
        var callCount = 0;
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            callCount++;
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, InterfacesFixtureDisconnected, string.Empty));
            if (args.StartsWith("wlan add profile", StringComparison.Ordinal))
                return Task.FromResult((1, string.Empty, "profile invalid"));

            throw new InvalidOperationException($"Unexpected netsh call: {args}");
        });

        var result = await svc.ConnectAsync("HomeNetwork", "password");

        result.Should().Be(WifiConnectResult.Failed);
    }

    [Fact]
    public async Task ConnectAsync_DeletesTempProfileFile_EvenOnFailure()
    {
        string? capturedProfilePath = null;
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, InterfacesFixtureDisconnected, string.Empty));
            if (args.StartsWith("wlan add profile", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(args, "filename=\"([^\"]+)\"");
                capturedProfilePath = match.Groups[1].Value;
                return Task.FromResult((1, string.Empty, "profile invalid"));
            }

            throw new InvalidOperationException($"Unexpected netsh call: {args}");
        });

        await svc.ConnectAsync("HomeNetwork", "password");

        capturedProfilePath.Should().NotBeNull();
        File.Exists(capturedProfilePath).Should().BeFalse("the PSK-bearing temp profile must be deleted immediately after use, even on failure");
    }

    [Fact]
    public async Task ConnectAsync_ReturnsSuccess_WhenInterfaceReportsConnected()
    {
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, InterfacesFixtureConnected, string.Empty));
            if (args.StartsWith("wlan add profile", StringComparison.Ordinal))
                return Task.FromResult((0, string.Empty, string.Empty));
            if (args.StartsWith("wlan connect", StringComparison.Ordinal))
                return Task.FromResult((0, string.Empty, string.Empty));

            throw new InvalidOperationException($"Unexpected netsh call: {args}");
        });

        var result = await svc.ConnectAsync("HomeNetwork", "password");

        result.Should().Be(WifiConnectResult.Success);
    }

    [Fact]
    public async Task ConnectAsync_ReturnsFailed_WhenConnectCommandFails()
    {
        var svc = new WirelessConnectionService(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show interfaces", StringComparison.Ordinal))
                return Task.FromResult((0, InterfacesFixtureDisconnected, string.Empty));
            if (args.StartsWith("wlan add profile", StringComparison.Ordinal))
                return Task.FromResult((0, string.Empty, string.Empty));
            if (args.StartsWith("wlan connect", StringComparison.Ordinal))
                return Task.FromResult((1, string.Empty, "connect failed"));

            throw new InvalidOperationException($"Unexpected netsh call: {args}");
        });

        var result = await svc.ConnectAsync("HomeNetwork", "password");

        result.Should().Be(WifiConnectResult.Failed);
    }
}

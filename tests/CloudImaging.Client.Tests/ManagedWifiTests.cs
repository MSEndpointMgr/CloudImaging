using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The portal's Wi-Fi networks on the device: reading them from the USB media, joining them when
/// Ethernet has no address, refreshing them from the gateway, and how the screens present them.
/// netsh, the boot drive and adapters are faked; nothing touches the real network stack.
/// </summary>
public sealed class ManagedWifiTests : IDisposable
{
    private const string InterfaceConnected = """
        Name                    : Wi-Fi
        State                   : connected
        """;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _drive = Directory.CreateTempSubdirectory("ci-wifi-").FullName;
    private readonly X509Certificate2 _certificate = CreateCertificate();

    public void Dispose()
    {
        _certificate.Dispose();
        Directory.Delete(_drive, recursive: true);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Cloud Imaging Boot Media Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Scan(params string[] ssids) =>
        string.Join("\n\n", ssids.Select((ssid, i) => $"SSID {i + 1} : {ssid}\n    Authentication          : WPA2-Personal\n         Signal             : 80%"));

    /// <summary>netsh fake: records each connect, and fails the connect for SSIDs in <paramref name="rejects"/>.</summary>
    private static WirelessConnectionService Netsh(string scan, List<string> connects, params string[] rejects) =>
        new(runNetshOverride: (args, _) =>
        {
            if (args.StartsWith("wlan show networks", StringComparison.Ordinal))
                return Task.FromResult((0, scan, string.Empty));
            if (args.StartsWith("wlan connect", StringComparison.Ordinal))
            {
                var ssid = args.Split('"')[1];
                connects.Add(ssid);
                return Task.FromResult((rejects.Contains(ssid) ? 1 : 0, string.Empty, string.Empty));
            }
            return Task.FromResult((0, InterfaceConnected, string.Empty));
        });

    private static ManagedWifiNetworkSet Set(params ManagedWifiNetwork[] networks) =>
        new() { LocationId = Guid.NewGuid(), GeneratedAt = DateTimeOffset.UtcNow, Networks = networks };

    private static ManagedWifiNetwork Network(string ssid, bool hidden = false) =>
        new(ssid, hidden, WifiSecurityType.Wpa2Personal, "correct horse battery");

    private ManagedWifiService Service(WirelessConnectionService netsh, Func<string?>? findAdapter = null) =>
        new(_certificate, netsh, findBootDrive: () => _drive, findAdapter: findAdapter ?? (() => "Wi-Fi (10.0.0.9)"), addressWait: TimeSpan.FromMilliseconds(50));

    private string MediaFile => Path.Combine(_drive, ManagedWifiEnvelope.FileName);

    private ManagedWifiService Loaded(WirelessConnectionService netsh, ManagedWifiNetworkSet set, Func<string?>? findAdapter = null)
    {
        File.WriteAllBytes(MediaFile, ManagedWifiEnvelope.Encrypt(set, _certificate));
        var service = Service(netsh, findAdapter);
        service.LoadFromMedia();
        return service;
    }

    // Reading the USB media

    [Fact]
    public void LoadFromMedia_ReadsTheNetworksMediaBuilderWrote()
    {
        var service = Loaded(Netsh(Scan(), []), Set(Network("Office"), Network("Lab", hidden: true)));

        service.Networks.Select(n => n.Ssid).Should().Equal("Office", "Lab");
        service.Find("Lab")!.Hidden.Should().BeTrue();
    }

    [Fact]
    public void LoadFromMedia_FindsNothing_WhenTheFileIsForAnotherCertificate()
    {
        using var other = CreateCertificate();
        File.WriteAllBytes(MediaFile, ManagedWifiEnvelope.Encrypt(Set(Network("Office")), other));
        var service = Service(Netsh(Scan(), []));

        service.LoadFromMedia().Should().BeEmpty("a stick prepared for another certificate cannot be read, and must not crash startup");
    }

    // Joining at startup

    [Fact]
    public async Task Join_ReportsNoNetworksSaved_WhenTheMediaHasNone()
    {
        var service = Service(Netsh(Scan("Office"), []));

        (await service.JoinAsync(_ => { })).Outcome.Should().Be(ManagedWifiOutcome.NoNetworksSaved);
    }

    [Fact]
    public async Task Join_SkipsSavedNetworksThatAreNotInRange_AndJoinsTheNextInOrder()
    {
        var connects = new List<string>();
        var service = Loaded(Netsh(Scan("Office"), connects), Set(Network("Headquarters"), Network("Office")));

        var result = await service.JoinAsync(_ => { });

        result.Should().Be(new ManagedWifiJoinResult(ManagedWifiOutcome.Connected, "Office"));
        connects.Should().Equal("Office");
    }

    [Fact]
    public async Task Join_TriesHiddenNetworks_EvenThoughTheScanNeverShowsThem()
    {
        var connects = new List<string>();
        var service = Loaded(Netsh(Scan("Neighbour"), connects), Set(Network("Lab", hidden: true)));

        var result = await service.JoinAsync(_ => { });

        result.Outcome.Should().Be(ManagedWifiOutcome.Connected);
        connects.Should().Equal("Lab");
    }

    [Fact]
    public async Task Join_ReportsTheRejectedPassword_WhenAVisibleNetworkRefusesIt()
    {
        var service = Loaded(Netsh(Scan("Office"), [], "Office"), Set(Network("Office")));

        (await service.JoinAsync(_ => { })).Should().Be(new ManagedWifiJoinResult(ManagedWifiOutcome.PasswordRejected, "Office"));
    }

    [Fact]
    public async Task Join_ReportsNoneInRange_WhenAHiddenNetworkDoesNotAnswer()
    {
        var service = Loaded(Netsh(Scan(), [], "Lab"), Set(Network("Office"), Network("Lab", hidden: true)));

        (await service.JoinAsync(_ => { })).Outcome.Should().Be(ManagedWifiOutcome.NoneInRange, "a hidden network that fails is most likely just not here");
    }

    [Fact]
    public async Task Join_ReportsNoIpAddress_WhenTheNetworkGivesNone()
    {
        var service = Loaded(Netsh(Scan("Office"), []), Set(Network("Office")), findAdapter: () => null);

        (await service.JoinAsync(_ => { })).Should().Be(new ManagedWifiJoinResult(ManagedWifiOutcome.NoIpAddress, "Office"));
    }

    // Refreshing from the gateway

    [Fact]
    public async Task Refresh_ReplacesTheNetworksAndTheUsbCopy()
    {
        var service = Loaded(Netsh(Scan(), []), Set(Network("Office")));
        var fresh = ManagedWifiEnvelope.Encrypt(Set(Network("Office 2")), _certificate);

        (await service.RefreshAsync(_ => Task.FromResult<byte[]?>(fresh))).Should().BeTrue();

        service.Networks.Select(n => n.Ssid).Should().Equal("Office 2");
        File.ReadAllBytes(MediaFile).Should().Equal(fresh);
        File.Exists(MediaFile + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_KeepsTheUsbCopy_WhenTheResponseCannotBeDecrypted()
    {
        var service = Loaded(Netsh(Scan(), []), Set(Network("Office")));
        var original = File.ReadAllBytes(MediaFile);

        (await service.RefreshAsync(_ => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("{}")))).Should().BeFalse();

        service.Networks.Select(n => n.Ssid).Should().Equal("Office");
        File.ReadAllBytes(MediaFile).Should().Equal(original);
    }

    [Fact]
    public async Task GatewayRequest_SignsTheLocation_WithTheBootMediaCertificate()
    {
        string? body = null;
        var handler = new CapturingHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, _certificate);
        var location = Guid.NewGuid();

        var envelope = await gateway.GetManagedWifiNetworksAsync("SN123", location);

        envelope.Should().Equal(1, 2, 3);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/wifi-networks");
        var request = JsonSerializer.Deserialize<ManagedWifiRequest>(body!, WebJson)!;
        request.LocationId.Should().Be(location);
        var proof = request.ProofOfPossession!;
        var challenge = DevicePayloadSignature.BuildWifiChallenge("SN123", location, proof.TimestampUtc, proof.Nonce);
        using var rsa = _certificate.GetRSAPublicKey()!;
        rsa.VerifyData(challenge, Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Theory]
    [InlineData(BootImageBase.WindowsPE)]
    [InlineData(BootImageBase.WindowsRE)]
    public async Task SelfUpdate_AsksForTheSameBase_SoWindowsReMediaStaysOnWindowsRe(BootImageBase bootImageBase)
    {
        string? body = null;
        var handler = new CapturingHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, _certificate);

        await gateway.GetLatestBootImageAsync(MachineArchitecture.X64, null, bootImageBase, "SN123");

        JsonSerializer.Deserialize<LatestBootImageRequest>(body!, WebJson)!.Base.Should().Be(bootImageBase);
    }

    // Wi-Fi profile

    [Theory]
    [InlineData(WifiSecurityType.Wpa2Personal, "WPA2PSK", false)]
    [InlineData(WifiSecurityType.Wpa3Personal, "WPA3SAE", false)]
    [InlineData(WifiSecurityType.Wpa2Wpa3Personal, "WPA3SAE", true)]
    public void ProfileXml_NamesTheSavedSecurityType(WifiSecurityType security, string authentication, bool transition)
    {
        var xml = WirelessConnectionService.BuildProfileXml("Office", "correct horse battery", security);

        xml.Should().Contain($"<authentication>{authentication}</authentication>");
        xml.Contains("transitionMode", StringComparison.Ordinal).Should().Be(transition);
        xml.Should().Contain("<keyType>passPhrase</keyType>");
    }

    [Fact]
    public void ProfileXml_MarksHiddenNetworks_AndTypesARawHexKey()
    {
        var xml = WirelessConnectionService.BuildProfileXml("Lab", new string('a', 64), WifiSecurityType.Wpa2Personal, hidden: true);

        xml.Should().Contain("<nonBroadcast>true</nonBroadcast>");
        xml.Should().Contain("<keyType>networkKey</keyType>");
    }

    // Wi-Fi window

    [Fact]
    public void WifiWindow_ListsManagedNetworksFirst_InPortalOrder_IncludingHiddenOnes()
    {
        IReadOnlyList<VisibleWifiNetwork> scanned =
        [
            new("Cafe", 95, WifiAuthKind.Open, true),
            new("Office", 40, WifiAuthKind.PersonalPsk, true),
            new("Guest", 70, WifiAuthKind.PersonalPsk, true),
        ];

        var merged = WifiConnectionViewModel.Merge(scanned, [Network("Office"), Network("Lab", hidden: true), Network("Away")]).ToList();

        merged.Select(n => n.Ssid).Should().Equal("Office", "Lab", "Cafe", "Guest");
        merged.Take(2).Should().OnlyContain(n => n.IsManaged);
        merged[1].IsHidden.Should().BeTrue();
        merged[1].Subtitle.Should().Be("Hidden network");
    }

    [Fact]
    public void WifiWindow_AsksForNoPassword_OnAManagedNetwork()
    {
        var service = Loaded(Netsh(Scan("Office", "Guest"), []), Set(Network("Office")));
        var vm = new WifiConnectionViewModel(Netsh(Scan("Office", "Guest"), []), managedWifi: service);

        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "Office");
        vm.ShowPasswordField.Should().BeFalse();
        vm.IsManagedSelection.Should().BeTrue();

        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "Guest");
        vm.ShowPasswordField.Should().BeTrue();
    }

    [Fact]
    public async Task WifiWindow_OffersThePasswordField_WhenTheSavedPasswordIsRejected()
    {
        var service = Loaded(Netsh(Scan("Office"), []), Set(Network("Office")));
        var vm = new WifiConnectionViewModel(Netsh(Scan("Office"), [], "Office"), managedWifi: service);
        vm.SelectedNetwork = vm.Networks.Single(n => n.Ssid == "Office");

        vm.ConnectCommand.Execute(null);
        await WaitUntil(() => !vm.IsConnecting && vm.StatusMessage is not null);

        vm.ShowPasswordField.Should().BeTrue();
        vm.ManagedNote.Should().StartWith("The saved password was rejected.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    // Boot image capabilities

    [Theory]
    [InlineData(new[] { "wlansvc.dll", "dmcmnutils.dll", "mdmregistration.dll" }, true)]
    [InlineData(new[] { "wlansvc.dll" }, false)]
    [InlineData(new string[0], false)]
    public void SupportsWifi_NeedsTheWlanServiceAndBothDlls(string[] present, bool expected)
    {
        BootImageCapabilities.SupportsWifi(path => present.Contains(Path.GetFileName(path)), @"X:\Windows\System32").Should().Be(expected);
    }

    // Startup and Operation Selection

    [Fact]
    public async Task Startup_JoinsManagedWifi_OnlyWhenEthernetHasNoAddress()
    {
        var connects = new List<string>();
        var service = Loaded(Netsh(Scan("Office"), connects), Set(Network("Office")));
        var joined = false;
        var vm = new StartupViewModel(_ => Task.FromResult(new GatewayProbeResult(GatewayReachability.Reachable, null)), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => (joined = connects.Count > 0) ? "Wi-Fi (10.0.0.9)" : null,
            networkWait: TimeSpan.FromMilliseconds(50),
            managedWifi: service, wifiSupported: true);

        var readiness = await vm.RunAsync();

        joined.Should().BeTrue();
        readiness.ConnectedWifiSsid.Should().Be("Office");
        vm.NetworkCheck.Detail.Should().Be("Wi-Fi Office: Wi-Fi (10.0.0.9)");
    }

    [Fact]
    public async Task Startup_LeavesWifiAlone_WhenEthernetIsUp()
    {
        var connects = new List<string>();
        var service = Loaded(Netsh(Scan("Office"), connects), Set(Network("Office")));
        var vm = new StartupViewModel(_ => Task.FromResult(new GatewayProbeResult(GatewayReachability.Reachable, null)), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => "Ethernet (10.0.0.5)",
            networkWait: TimeSpan.FromMilliseconds(50),
            managedWifi: service, wifiSupported: true);

        var readiness = await vm.RunAsync();

        connects.Should().BeEmpty();
        readiness.ConnectedWifiSsid.Should().BeNull();
    }

    [Fact]
    public async Task Startup_SaysWhichNetworkRejectedThePassword()
    {
        var service = Loaded(Netsh(Scan("Office"), [], "Office"), Set(Network("Office")));
        var vm = new StartupViewModel(_ => Task.FromResult(new GatewayProbeResult(GatewayReachability.Unreachable, null)), false,
            findLocalDisks: () => [],
            findNetworkAdapter: () => null,
            networkWait: TimeSpan.FromMilliseconds(50),
            managedWifi: service, wifiSupported: true);

        var readiness = await vm.RunAsync();

        readiness.Gateway.Should().Be(GatewayReachability.NoNetwork);
        readiness.WifiJoin!.Outcome.Should().Be(ManagedWifiOutcome.PasswordRejected);
        vm.NetworkCheck.Detail.Should().Be("No Ethernet. Office rejected the saved password.");
    }

    private static OperationSelectionViewModel Selection(StartupReadiness readiness) =>
        new(new DeviceGatewayApiClient(new HttpClient { BaseAddress = new Uri("https://gw.example.com") }), (_, _) => { }, readiness: readiness, recheck: _ => Task.FromResult(readiness));

    [Fact]
    public void Selection_HidesWifi_OnWindowsPe()
    {
        var vm = Selection(new StartupReadiness(1, GatewayReachability.NoNetwork, null, WifiSupported: false));

        vm.IsWifiAvailable.Should().BeFalse();
        vm.ShowBannerWifiAction.Should().BeFalse();
        vm.ShowBannerRecheck.Should().BeTrue();
    }

    [Fact]
    public void Selection_ExplainsARejectedPassword_AndOffersWifi()
    {
        var vm = Selection(new StartupReadiness(1, GatewayReachability.NoNetwork, null, WifiSupported: true, new ManagedWifiJoinResult(ManagedWifiOutcome.PasswordRejected, "Office")));

        vm.ConnectivityTitle.Should().Be("Wi-Fi password rejected");
        vm.ConnectivityMessage.Should().StartWith("Office rejected the saved password.");
        vm.ShowBannerWifiAction.Should().BeTrue();
        vm.ShowBannerRecheck.Should().BeTrue("Check again is offered next to Connect to Wi-Fi, for a cable plugged in later");
        vm.NetworkLabel.Should().Be("No network");
        vm.IsNetworkMissing.Should().BeTrue();
    }

    [Fact]
    public void Selection_NamesTheJoinedNetwork_OnTheNetworkButton()
    {
        var vm = Selection(new StartupReadiness(1, GatewayReachability.Reachable, null, WifiSupported: true, new ManagedWifiJoinResult(ManagedWifiOutcome.Connected, "Office")));

        vm.IsWifiAvailable.Should().BeTrue();
        vm.NetworkLabel.Should().Be("Office");
        vm.NetworkSymbol.Should().Be(Wpf.Ui.Controls.SymbolRegular.Wifi124);
        vm.IsServiceBlocked.Should().BeFalse();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request);
        }
    }
}

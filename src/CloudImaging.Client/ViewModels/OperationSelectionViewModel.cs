using System.ComponentModel;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using CloudImaging.Client.Logging;
using CloudImaging.Contracts.Models;
using CloudImaging.Client.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.ViewModels;

/// <summary>What the message card under the operation cards is showing.</summary>
public enum ConnectivityMessageKind
{
    /// <summary>Nothing; the card's space stays reserved.</summary>
    None,

    /// <summary>The service cannot be used.</summary>
    Error,

    /// <summary>The service cannot be used, for a reason the technician can fix on the device.</summary>
    Warning,

    /// <summary>A check is running.</summary>
    Progress,

    /// <summary>Something just worked; fades after a few seconds.</summary>
    Success,
}

/// <summary>One line of the Network panel.</summary>
/// <param name="Symbol">Adapter icon.</param>
/// <param name="Title">"Ethernet", "Wi-Fi", or the name of a device without a driver.</param>
/// <param name="Detail">Adapter, address and state.</param>
/// <param name="Badge">"In use" on the connection traffic goes through.</param>
/// <param name="IsWarning">True for a device without a driver.</param>
public sealed record NetworkPanelRow(SymbolRegular Symbol, string Title, string Detail, string? Badge = null, bool IsWarning = false)
{
    /// <summary>True when <see cref="Badge"/> is shown.</summary>
    public bool HasBadge => Badge is not null;
}

/// <summary>
/// View model for the OperationSelectionView (T032).
/// Collects device hardware metadata silently before session registration (FR-001a).
/// </summary>
public sealed partial class OperationSelectionViewModel : INotifyPropertyChanged
{
    /// <summary>Maximum time to wait for a routable network adapter before giving up and proceeding anyway.</summary>
    private static readonly TimeSpan NetworkWaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkWaitPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultMessageFade = TimeSpan.FromSeconds(3);

    // Address changes arrive in bursts while an adapter comes up.
    private static readonly TimeSpan DefaultNetworkSettle = TimeSpan.FromSeconds(1);

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly SystemClockSynchronizationService _clockSync;
    private readonly Action<CreateSessionResponse, string> _navigate;
    private readonly Action<bool>? _setMainWindowTopmost;
    private readonly CommandPromptLauncherService _commandPromptLauncher;
    private readonly DevicePostureDetector _postureDetector;
    private readonly Action? _navigateToAutopilot;
    private readonly Func<CancellationToken, Task<StartupReadiness>>? _recheck;
    private readonly Func<NetworkSnapshot> _readNetwork;
    private readonly TimeSpan _messageFade;
    private readonly TimeSpan _networkSettle;
    private readonly ILogger _logger;
    private StartupReadiness? _readiness;
    private NetworkSnapshot? _network;
    private string? _wifiSsid;
    private (ConnectivityMessageKind Kind, string Title, string Text)? _message;
    private int _messageVersion;
    private int _networkChangeVersion;
    private bool _isRechecking;
    private bool _isWatchingNetwork;
    private SynchronizationContext? _uiContext;
    private AutopilotAvailabilityState _autopilotAvailability = AutopilotAvailabilityState.Checking;
    // Decommissioning card is hidden (not implemented for the first release), so Imaging is
    // pre-selected rather than requiring an operator click before Continue is enabled.
    private string? _selectedOperation = "Imaging";
    private string? _statusMessage;
    private bool _isWaitingForNetwork;
    private bool _isBusy;

    /// <param name="gatewayClient">Device Gateway API client used to register the session.</param>
    /// <param name="navigate">Invoked with the session response once registration succeeds.</param>
    /// <param name="clockSync">Optional override; defaults to a real NTP sync service.</param>
    /// <param name="commandPromptEnabled">
    /// Whether this boot image was built with the "Enable command prompt access" opt-in
    /// (FR-051d) — drives <see cref="IsCommandPromptAvailable"/>. Off by default.
    /// </param>
    /// <param name="setMainWindowTopmost">
    /// Callback bound to the real <see cref="Views.MainWindow"/> instance, used to toggle
    /// Topmost off/on around an interactive command prompt session (see
    /// <see cref="Services.CommandPromptLauncherService"/>). Optional only for simpler test
    /// construction — required in practice whenever <paramref name="commandPromptEnabled"/> is true.
    /// </param>
    /// <param name="commandPromptLauncher">Optional override; defaults to a real launcher.</param>
    /// <param name="autopilotToolingPresent">Whether this boot image carries the Autopilot hash tooling.</param>
    /// <param name="navigateToAutopilot">Opens the Autopilot registration view. Null hides the option.</param>
    /// <param name="postureDetector">Optional override; defaults to a detector without logging.</param>
    /// <param name="startImagingImmediately">Starts a new imaging session at once, as if Continue was pressed (blocked screen's Try again).</param>
    /// <param name="autopilotAvailability">Current Autopilot registration availability state, refining the Autopilot tile's enabled/disabled reason.</param>
    /// <param name="readiness">What the startup checks found. Null skips the gating (screens opened without startup checks).</param>
    /// <param name="recheck">Runs the network and service checks again and returns what they found. Null hides Check again.</param>
    /// <param name="managedWifi">The portal's Wi-Fi networks, listed first in the Wi-Fi window; null on Windows PE.</param>
    /// <param name="logger">Defaults to a no-op logger.</param>
    /// <param name="readNetwork">Test seam; defaults to reading the device's adapters.</param>
    /// <param name="messageFade">Test seam; how long a success message stays.</param>
    /// <param name="networkSettle">Test seam; how long to wait for a burst of address changes to end.</param>
    public OperationSelectionViewModel(
        DeviceGatewayApiClient gatewayClient,
        Action<CreateSessionResponse, string> navigate,
        SystemClockSynchronizationService? clockSync = null,
        bool commandPromptEnabled = false,
        Action<bool>? setMainWindowTopmost = null,
        CommandPromptLauncherService? commandPromptLauncher = null,
        bool autopilotToolingPresent = false,
        Action? navigateToAutopilot = null,
        DevicePostureDetector? postureDetector = null,
        bool startImagingImmediately = false,
        AutopilotAvailabilityState? autopilotAvailability = null,
        StartupReadiness? readiness = null,
        Func<CancellationToken, Task<StartupReadiness>>? recheck = null,
        ManagedWifiService? managedWifi = null,
        ILogger<OperationSelectionViewModel>? logger = null,
        Func<NetworkSnapshot>? readNetwork = null,
        TimeSpan? messageFade = null,
        TimeSpan? networkSettle = null)
    {
        _gatewayClient          = gatewayClient;
        _navigate               = navigate;
        _clockSync              = clockSync ?? new SystemClockSynchronizationService();
        IsCommandPromptAvailable = commandPromptEnabled;
        _setMainWindowTopmost   = setMainWindowTopmost;
        _commandPromptLauncher  = commandPromptLauncher ?? new CommandPromptLauncherService();
        _postureDetector        = postureDetector ?? new DevicePostureDetector();
        _navigateToAutopilot = navigateToAutopilot;
        _readiness = readiness;
        _recheck = recheck;
        _readNetwork = readNetwork ?? NetworkStatus.Read;
        _messageFade = messageFade ?? DefaultMessageFade;
        _networkSettle = networkSettle ?? DefaultNetworkSettle;
        ManagedWifi = managedWifi;
        IsWifiAvailable = readiness?.WifiSupported ?? BootImageCapabilities.SupportsWifi();
        _logger = logger ?? NullLogger<OperationSelectionViewModel>.Instance;
        IsAutopilotOptionVisible = autopilotToolingPresent && navigateToAutopilot is not null;

        SelectOperationCommand   = new RelayCommand(op => SelectOperation(op?.ToString()));
        ContinueCommand          = new RelayCommand(async _ => await ContinueAsync(), _ => CanContinue);
        LaunchCommandPromptCommand = new RelayCommand(_ => LaunchCommandPrompt(), _ => IsCommandPromptAvailable);
        RecheckCommand           = new RelayCommand(async _ => await RecheckAsync(), _ => CanRecheck && !_isRechecking);

        if (!IsImagingSelectable)
            _selectedOperation = null;

        if (IsAutopilotOptionVisible)
        {
            // Startup resolves this before the screen is shown; checking here as well would make the card visibly change state.
            if (autopilotAvailability is { } known)
                _autopilotAvailability = known;
            else
                _ = LoadAutopilotAvailabilityAsync();
        }

        if (startImagingImmediately && IsImagingSelectable)
        {
            SelectedOperation = "Imaging";
            _ = ContinueAsync();
        }
    }

    /// <summary>What the last startup or network check found; null for screens opened without startup checks.</summary>
    public StartupReadiness? Readiness => _readiness;

    /// <summary>False when startup could not use the Device Gateway API, which every operation needs.</summary>
    public bool IsServiceAvailable => _readiness?.CanReachService ?? true;

    /// <summary>True when the connectivity banner is shown and every operation is disabled.</summary>
    public bool IsServiceBlocked => !IsServiceAvailable;

    /// <summary>Imaging needs the service and exactly one local disk; unreadable disks are left for formatting to report.</summary>
    public bool IsImagingSelectable => IsServiceAvailable && _readiness?.LocalDiskCount is null or 1;

    /// <summary>Helper text on the Imaging card.</summary>
    public string ImagingCardDescription => _readiness?.LocalDiskCount switch
    {
        0 => "No local disk detected. Add the storage driver to the boot image.",
        > 1 and var count => $"{count} local disks detected. Imaging requires exactly one.",
        _ => "Apply a fresh OS image to this device.",
    };

    /// <summary>True when the only adapter Windows found has no driver in the boot image.</summary>
    private bool IsBlockedByMissingDriver => _readiness is { Gateway: GatewayReachability.NoNetwork, AdaptersWithoutDriver.Count: > 0 };

    /// <summary>Heading of the connectivity banner.</summary>
    public string ConnectivityTitle => _readiness?.Gateway switch
    {
        GatewayReachability.NoNetwork when IsBlockedByMissingDriver => "Network adapter without a driver",
        GatewayReachability.NoNetwork when !_readiness.Rechecked && _readiness.WifiJoin?.Outcome == ManagedWifiOutcome.PasswordRejected => "Wi-Fi password rejected",
        GatewayReachability.NoNetwork => "No network connection",
        GatewayReachability.ServiceUnavailable => "Cloud Imaging service error",
        GatewayReachability.CertificateRejected => "Boot media certificate rejected",
        GatewayReachability.NotConfigured => "Service URL missing",
        _ => "Cloud Imaging service unreachable",
    };

    /// <summary>What went wrong and what to do about it.</summary>
    public string ConnectivityMessage => _readiness?.Gateway switch
    {
        GatewayReachability.NoNetwork when IsBlockedByMissingDriver => $"{_readiness.AdaptersWithoutDriver![0]} was found, but the boot image has no driver for it. Add the driver in Media Builder, or use another adapter.",
        GatewayReachability.NoNetwork when _readiness.Rechecked => "No adapter got an IP address. Check the cable, or add the adapter's driver to the boot image. Then select Check again.",
        GatewayReachability.NoNetwork when IsWifiAvailable => _readiness.WifiJoin?.Outcome switch
        {
            ManagedWifiOutcome.PasswordRejected => $"{_readiness.WifiJoin.Ssid} rejected the saved password. It may have changed since this USB media was prepared. Plug in Ethernet and select Check again, or enter the password.",
            ManagedWifiOutcome.NoAdapter => "No Ethernet and no Wi-Fi adapter was found. Plug in Ethernet and select Check again, or add the Wi-Fi driver to the boot image.",
            ManagedWifiOutcome.NoIpAddress => $"{_readiness.WifiJoin.Ssid} accepted the device but gave it no IP address. Plug in Ethernet and select Check again, or choose another Wi-Fi network.",
            _ => "No saved Wi-Fi network is in range. Plug in Ethernet and select Check again, or connect to Wi-Fi.",
        },
        GatewayReachability.NoNetwork => "No adapter has an IP address. Plug in Ethernet and select Check again. If Ethernet is already connected, add the network adapter driver to the boot image.",
        GatewayReachability.ServiceUnavailable => "The service returned a server error. Retry in a few minutes; if it persists, contact your Cloud Imaging administrator.",
        GatewayReachability.CertificateRejected => "The service rejected the boot media certificate. Rebuild the boot image in Media Builder and re-prepare the USB drive.",
        GatewayReachability.NotConfigured => "The boot image has no Cloud Imaging service URL. Rebuild it in Media Builder.",
        _ => "The network is up but the service did not respond. Check DNS, proxy and outbound HTTPS (TCP 443).",
    };

    /// <summary>What the message card under the operation cards shows: a check in progress or its result, else the blocking problem.</summary>
    public ConnectivityMessageKind ConnectivityKind => _message?.Kind
        ?? (IsServiceAvailable ? ConnectivityMessageKind.None : IsBlockedByMissingDriver ? ConnectivityMessageKind.Warning : ConnectivityMessageKind.Error);

    /// <summary>Message card heading.</summary>
    public string? ConnectivityHeading => _message?.Title ?? (IsServiceAvailable ? null : ConnectivityTitle);

    /// <summary>Message card text.</summary>
    public string? ConnectivityDetail => _message?.Text ?? (IsServiceAvailable ? null : ConnectivityMessage);

    /// <summary>True while the message card shows anything.</summary>
    public bool HasConnectivityMessage => ConnectivityKind != ConnectivityMessageKind.None;

    /// <summary>True for the error style.</summary>
    public bool IsConnectivityError => ConnectivityKind == ConnectivityMessageKind.Error;

    /// <summary>True for the warning style.</summary>
    public bool IsConnectivityWarning => ConnectivityKind == ConnectivityMessageKind.Warning;

    /// <summary>True while a check runs.</summary>
    public bool IsConnectivityProgress => ConnectivityKind == ConnectivityMessageKind.Progress;

    /// <summary>True for a success message.</summary>
    public bool IsConnectivitySuccess => ConnectivityKind == ConnectivityMessageKind.Success;

    /// <summary>True when Check again is offered.</summary>
    public bool CanRecheck => _recheck is not null;

    /// <summary>True while Check again runs.</summary>
    public bool IsRechecking
    {
        get => _isRechecking;
        private set { _isRechecking = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>True on a Windows RE boot image. Windows PE has no Wi-Fi, so the button is not shown at all.</summary>
    public bool IsWifiAvailable { get; }

    /// <summary>The portal's Wi-Fi networks, handed to the Wi-Fi window; null on Windows PE.</summary>
    public ManagedWifiService? ManagedWifi { get; }

    /// <summary>The managed network startup joined, or null.</summary>
    public string? ConnectedWifiSsid => _readiness?.ConnectedWifiSsid;

    /// <summary>The banner's actions belong to the blocking problem, not to a check in progress or its result.</summary>
    private bool ShowsBlockingProblem => _message is null && IsServiceBlocked;

    /// <summary>On a Wi-Fi image without a network, the banner also offers Connect to Wi-Fi.</summary>
    public bool ShowBannerWifiAction => ShowsBlockingProblem && IsWifiAvailable && _readiness?.Gateway == GatewayReachability.NoNetwork;

    /// <summary>Check again is always offered with the blocking problem.</summary>
    public bool ShowBannerRecheck => ShowsBlockingProblem && CanRecheck;

    /// <summary>Footer network button: "Ethernet", the Wi-Fi network's name, or "No network".</summary>
    public string NetworkLabel => _network switch
    {
        null => ConnectedWifiSsid ?? (_readiness?.Gateway == GatewayReachability.NoNetwork ? "No network" : "Network"),
        { Ethernet: not null } => "Ethernet",
        { WiFi: not null } => _wifiSsid ?? ConnectedWifiSsid ?? "Wi-Fi",
        _ => "No network",
    };

    /// <summary>Icon of the footer network button.</summary>
    public SymbolRegular NetworkSymbol => IsNetworkMissing ? SymbolRegular.PlugDisconnected24
        : _network is { Ethernet: null, WiFi: not null } || (_network is null && ConnectedWifiSsid is not null) ? SymbolRegular.Wifi124
        : SymbolRegular.PlugConnected24;

    /// <summary>True when no adapter has an address; the button then shows a red dot.</summary>
    public bool IsNetworkMissing => _network is null
        ? _readiness?.Gateway == GatewayReachability.NoNetwork
        : _network.Ethernet is null && _network.WiFi is null;

    /// <summary>The Network panel's lines, one per adapter.</summary>
    public IReadOnlyList<NetworkPanelRow> NetworkRows => BuildNetworkRows(_network ?? NetworkSnapshot.Empty, _wifiSsid);

    internal static IReadOnlyList<NetworkPanelRow> BuildNetworkRows(NetworkSnapshot network, string? wifiSsid)
    {
        var rows = new List<NetworkPanelRow>();
        var inUse = network.Ethernet ?? network.WiFi;
        foreach (var adapter in network.Adapters)
        {
            var badge = ReferenceEquals(adapter, inUse) ? "In use" : null;
            rows.Add((adapter.Kind, adapter.State) switch
            {
                (_, NetworkAdapterState.NoDriver) => new NetworkPanelRow(SymbolRegular.Warning24, adapter.Name, "No driver in the boot image", IsWarning: true),
                (NetworkAdapterKind.Ethernet, NetworkAdapterState.Connected) => new NetworkPanelRow(SymbolRegular.PlugConnected24, "Ethernet", adapter.Display, badge),
                (NetworkAdapterKind.Ethernet, NetworkAdapterState.NoAddress) => new NetworkPanelRow(SymbolRegular.PlugConnected24, "Ethernet", $"{adapter.Name}, no IP address"),
                (NetworkAdapterKind.Ethernet, _) => new NetworkPanelRow(SymbolRegular.PlugDisconnected24, "Ethernet", $"{adapter.Name}, cable unplugged"),
                (_, NetworkAdapterState.Connected) => new NetworkPanelRow(SymbolRegular.Wifi124, "Wi-Fi", wifiSsid is null ? adapter.Display : $"{wifiSsid}, {adapter.Display}", badge),
                (_, NetworkAdapterState.NoAddress) => new NetworkPanelRow(SymbolRegular.Wifi124, "Wi-Fi", $"{wifiSsid ?? adapter.Name}, no IP address"),
                _ => new NetworkPanelRow(SymbolRegular.WifiOff24, "Wi-Fi", "Not connected"),
            });
        }

        if (rows.Count == 0)
            rows.Add(new NetworkPanelRow(SymbolRegular.PlugDisconnected24, "No network adapter", "Windows found none. Add the adapter's driver to the boot image."));
        return rows;
    }

    /// <summary>Runs the network and service checks again, on this screen.</summary>
    public ICommand RecheckCommand { get; }

    /// <summary>The Autopilot card is shown only on boot media built with the hash tooling.</summary>
    public bool IsAutopilotOptionVisible { get; }

    /// <summary>Whether Autopilot registration is currently offered to the operator.</summary>
    public AutopilotAvailabilityState AutopilotAvailability
    {
        get => _autopilotAvailability;
        private set
        {
            _autopilotAvailability = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutopilotSelectable));
            OnPropertyChanged(nameof(AutopilotCardDescription));
        }
    }

    /// <summary>Selectable unless the portal has it turned off; an unreachable service gets its error on the next screen.</summary>
    public bool IsAutopilotSelectable => IsAutopilotOptionVisible && IsServiceAvailable
        && _autopilotAvailability is AutopilotAvailabilityState.Enabled or AutopilotAvailabilityState.Unreachable;

    /// <summary>Helper text shown on the Autopilot card for the current <see cref="AutopilotAvailability"/>.</summary>
    public string AutopilotCardDescription => _autopilotAvailability switch
    {
        AutopilotAvailabilityState.Checking => "Checking availability…",
        AutopilotAvailabilityState.Disabled => "Disabled in the Cloud Imaging portal.",
        AutopilotAvailabilityState.Unreachable => "Submit the hardware hash for approval. Availability could not be confirmed.",
        _ => "Submit the hardware hash for approval. The disk is not modified.",
    };

    private void SelectOperation(string? operation)
    {
        if (operation == "Autopilot" && !IsAutopilotSelectable)
            return;
        if (operation == "Imaging" && !IsImagingSelectable)
            return;
        SelectedOperation = operation;
    }

    private async Task LoadAutopilotAvailabilityAsync()
    {
        AutopilotAvailability = await ResolveAutopilotAvailabilityAsync(_gatewayClient);

        if (SelectedOperation == "Autopilot" && !IsAutopilotSelectable)
            SelectedOperation = IsImagingSelectable ? "Imaging" : null;
    }

    // ── Network: Check again, the message card and the footer network button ─────────────

    /// <summary>
    /// Check again: shows progress in the message card while the network and service checks run,
    /// then either unlocks the operations with a short success message or shows what is still wrong.
    /// </summary>
    internal async Task RecheckAsync()
    {
        if (_recheck is null || _readiness is null || IsRechecking)
            return;

        IsRechecking = true;
        try
        {
            ShowMessage(ConnectivityMessageKind.Progress, "Checking the network", "Looking for a network connection\u2026");
            await RefreshNetworkAsync();
            if ((_network?.Ethernet ?? _network?.WiFi) is { } via)
            {
                ShowMessage(ConnectivityMessageKind.Progress, via.Kind == NetworkAdapterKind.Ethernet ? "Ethernet connected" : "Wi-Fi connected", $"{via.Display}. Checking the Cloud Imaging service\u2026");
            }

            StartupReadiness result;
            try
            {
                result = await _recheck(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogRecheckFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
                result = _readiness with { Gateway = GatewayReachability.Unreachable, Rechecked = true };
            }

            ApplyReadiness(result);
            await RefreshNetworkAsync();
            if (result.CanReachService)
            {
                var connected = _network?.Ethernet ?? _network?.WiFi;
                ShowMessage(ConnectivityMessageKind.Success,
                    connected?.Kind == NetworkAdapterKind.WiFi ? "Connected through Wi-Fi" : connected is null ? "Connected" : "Connected through Ethernet",
                    connected is null ? "The Cloud Imaging service answered." : connected.Kind == NetworkAdapterKind.WiFi && _wifiSsid is not null ? $"{_wifiSsid}, {connected.Display}" : connected.Display,
                    fade: true);
            }
            else
            {
                ClearMessage();
            }
        }
        finally
        {
            IsRechecking = false;
        }
    }

    private void ApplyReadiness(StartupReadiness readiness)
    {
        _readiness = readiness;
        if (readiness.AutopilotAvailability is { } autopilot)
            _autopilotAvailability = autopilot;
        if (_selectedOperation is null && IsImagingSelectable)
            _selectedOperation = "Imaging";
        else if (_selectedOperation == "Imaging" && !IsImagingSelectable)
            _selectedOperation = null;

        // Nearly every binding on this screen depends on the readiness.
        OnPropertyChanged(string.Empty);
    }

    private void ShowMessage(ConnectivityMessageKind kind, string title, string text, bool fade = false)
    {
        var version = ++_messageVersion;
        _message = (kind, title, text);
        RaiseMessageChanged();
        if (fade)
            _ = FadeMessageAsync(version);
    }

    private void ClearMessage()
    {
        ++_messageVersion;
        _message = null;
        RaiseMessageChanged();
    }

    private async Task FadeMessageAsync(int version)
    {
        await Task.Delay(_messageFade);
        if (version == _messageVersion)
            ClearMessage();
    }

    private void RaiseMessageChanged()
    {
        OnPropertyChanged(nameof(ConnectivityKind));
        OnPropertyChanged(nameof(ConnectivityHeading));
        OnPropertyChanged(nameof(ConnectivityDetail));
        OnPropertyChanged(nameof(HasConnectivityMessage));
        OnPropertyChanged(nameof(IsConnectivityError));
        OnPropertyChanged(nameof(IsConnectivityWarning));
        OnPropertyChanged(nameof(IsConnectivityProgress));
        OnPropertyChanged(nameof(IsConnectivitySuccess));
        OnPropertyChanged(nameof(ShowBannerWifiAction));
        OnPropertyChanged(nameof(ShowBannerRecheck));
    }

    /// <summary>Re-reads the adapters (and the Wi-Fi network's name) for the footer button and the Network panel. Never throws.</summary>
    public async Task RefreshNetworkAsync()
    {
        NetworkSnapshot network;
        try
        {
            network = await Task.Run(_readNetwork);
        }
        catch (Exception ex)
        {
            LogNetworkReadFailed(_logger, ex.Message);
            network = NetworkSnapshot.Empty;
        }

        string? ssid = null;
        if (network.WiFi is not null && ManagedWifi is not null)
            ssid = await ManagedWifi.Wireless.GetConnectedSsidAsync();

        _network = network;
        _wifiSsid = ssid;
        OnPropertyChanged(nameof(NetworkLabel));
        OnPropertyChanged(nameof(NetworkSymbol));
        OnPropertyChanged(nameof(IsNetworkMissing));
        OnPropertyChanged(nameof(NetworkRows));
    }

    /// <summary>Follows adapter changes while this screen is shown, so the network button stays current and Ethernet replaces Wi-Fi.</summary>
    public void StartWatchingNetwork()
    {
        if (_isWatchingNetwork)
            return;

        _isWatchingNetwork = true;
        _uiContext = SynchronizationContext.Current;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _ = OnNetworkChangedAsync();
    }

    /// <summary>Stops following adapter changes; the event is static, so a screen left behind must unsubscribe.</summary>
    public void StopWatchingNetwork()
    {
        _isWatchingNetwork = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_uiContext is { } context)
            context.Post(_ => _ = OnNetworkChangedAsync(), null);
        else
            _ = OnNetworkChangedAsync();
    }

    /// <summary>
    /// Ethernet always wins: once a wired adapter has an address while Wi-Fi is connected, Wi-Fi is
    /// left and the message card says so. Only while the service is usable; a blocked screen waits
    /// for Check again, which applies the same rule.
    /// </summary>
    internal async Task OnNetworkChangedAsync()
    {
        var version = ++_networkChangeVersion;
        await Task.Delay(_networkSettle);
        if (version != _networkChangeVersion)
            return;

        await RefreshNetworkAsync();
        if (IsServiceBlocked || IsRechecking || ManagedWifi is null || _network is not { Ethernet: { } ethernet, WiFi: not null })
            return;

        var left = await ManagedWifi.Wireless.DisconnectAsync();
        if (left is null)
            return;

        if (_readiness is not null)
            _readiness = _readiness with { WifiJoin = null };
        ShowMessage(ConnectivityMessageKind.Success, "Switched to Ethernet", $"{ethernet.Name} is in use. Wi-Fi {left} was disconnected.", fade: true);
        await RefreshNetworkAsync();
    }

    /// <summary>
    /// Asks the Device Gateway whether the portal allows Autopilot registration, after giving the
    /// NIC the same grace period registration gets. Never throws; no answer means Unreachable.
    /// </summary>
    internal static async Task<AutopilotAvailabilityState> ResolveAutopilotAvailabilityAsync(DeviceGatewayApiClient gateway, CancellationToken ct = default)
    {
        try
        {
            var deadline = DateTime.UtcNow + NetworkWaitTimeout;
            while (!NetworkReadiness.IsReady() && DateTime.UtcNow < deadline)
                await Task.Delay(NetworkWaitPollInterval, ct);

            return await gateway.GetAutopilotAvailabilityAsync(ct) switch
            {
                true => AutopilotAvailabilityState.Enabled,
                false => AutopilotAvailabilityState.Disabled,
                null => AutopilotAvailabilityState.Unreachable,
            };
        }
        catch
        {
            return AutopilotAvailabilityState.Unreachable;
        }
    }

    /// <summary>Which card the operator picked: "Imaging" or "Autopilot".</summary>
    public string? SelectedOperation
    {
        get => _selectedOperation;
        set { _selectedOperation = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanContinue)); }
    }

    /// <summary>Error text shown when registration fails; null while no error has occurred.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>True when <see cref="StatusMessage"/> is set.</summary>
    public bool HasError     => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>True when an operation is selected and no registration is already in flight.</summary>
    public bool CanContinue  => SelectedOperation is not null && !_isBusy && IsServiceAvailable;

    /// <summary>
    /// True from the moment Continue is pressed until either navigation away from this view
    /// occurs or an error is surfaced — drives the spinner shown on the Continue button.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    /// <summary>Inverse of <see cref="IsBusy"/>, bound directly rather than via a value converter.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// True while waiting for a network adapter to come up (T032a). Distinct from
    /// <see cref="StatusMessage"/>/<see cref="HasError"/>, which are reserved for actual
    /// failures — this is an informational, non-error waiting state.
    /// </summary>
    public bool IsWaitingForNetwork
    {
        get => _isWaitingForNetwork;
        private set { _isWaitingForNetwork = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// True when this boot image was built with the "Enable command prompt access" opt-in
    /// (FR-051d) — drives visibility of the Command Prompt support-tool button.
    /// </summary>
    public bool IsCommandPromptAvailable { get; }

    /// <summary>Picks "Imaging" or "Autopilot" as the selected operation.</summary>
    public ICommand SelectOperationCommand   { get; }

    /// <summary>Registers a new session (Imaging) or opens the Autopilot flow, for the selected operation.</summary>
    public ICommand ContinueCommand          { get; }

    /// <summary>Opens the command prompt support tool.</summary>
    public ICommand LaunchCommandPromptCommand { get; }

    private void LaunchCommandPrompt()
    {
        if (_setMainWindowTopmost is null)
            return;

        _commandPromptLauncher.Launch(_setMainWindowTopmost);
    }

    private async Task ContinueAsync()
    {
        if (SelectedOperation is null) return;

        // Autopilot registration is its own flow and never creates an imaging session.
        if (SelectedOperation == "Autopilot")
        {
            if (IsAutopilotSelectable)
                _navigateToAutopilot?.Invoke();
            return;
        }

        IsBusy = true;
        StatusMessage = null;

        try
        {
            // In WinPE, the NIC driver/DHCP lease can take a few seconds to finish
            // initializing after this view appears; wait briefly for a routable adapter
            // before registering, instead of spuriously failing on a network error (T032a).
            await WaitForNetworkAsync();

            // WinPE devices frequently boot with a wildly incorrect system clock (dead/unset
            // RTC, never synced) — correct it against external NTP servers before signing the
            // proof-of-possession challenge below, otherwise an entirely valid signature can be
            // rejected as "Expired" by the Device Gateway API's clock-skew check (FR-069).
            await _clockSync.TrySynchronizeAsync();

            // Collect hardware metadata AND identity fields (serial/manufacturer/model/MAC) in a
            // single background call. These are all synchronous WMI/COM queries — previously
            // only CollectHardwareMetadata ran via Task.Run while GetSerialNumber/Manufacturer/
            // Model ran directly on the UI thread right after, which blocked the dispatcher (and
            // froze the just-shown spinner's indeterminate animation) for as long as those WMI
            // round-trips took (FR-001a).
            var (serialNumber, manufacturer, model, macAddress, hardware, locationId, locationName, posture) = await Task.Run(() =>
            {
                var serial = GetSerialNumber();
                var mfr    = GetManufacturer();
                var mdl    = GetModel();
                var mac    = GetMacAddress();
                var hw     = CollectHardwareMetadata();
                // Location Labels feature: if this Client was booted from USB media that Media
                // Builder tagged with a site label, thread it through registration so the device
                // shows up already labeled in the portal (FR). Best-effort — no manifest, or no
                // location recorded in it, just registers without one, same as before this
                // feature. Read here (not on the UI thread) since it does blocking WMI + file I/O,
                // same reasoning as the other calls in this batch.
                var (locId, locName) = ReadLocationFromManifest();
                var securityPosture = _postureDetector.Detect();
                return (serial, mfr, mdl, mac, hw, locId, locName, securityPosture);
            });

            var payload = new DeviceRegistrationPayload
            {
                SerialNumber = serialNumber,
                Manufacturer = manufacturer,
                Model        = model,
                MacAddress   = macAddress,
                Hardware     = hardware,
                LocationId   = locationId,
                LocationName = locationName,
                Architecture = MachineArchitecturePlatform.HostArchitecture(),
                SecurityPosture = posture,
            };

            var sessionResponse = await _gatewayClient.CreateSessionAsync(payload);
            if (sessionResponse is null)
            {
                StatusMessage = "Session registration returned no session. Restart the device.";
                return;
            }

            // Set Bearer token for subsequent Device Gateway calls
            _gatewayClient.SetSessionToken(sessionResponse.DeviceSessionToken);

            // Navigate to SessionInitView — carry the locally-collected serial number forward
            // so it can be displayed later (e.g. ResultsView's NotAuthorized outcome, FR-026)
            // instead of being discarded once sent to the server.
            _navigate(sessionResponse, serialNumber);
        }
        catch (HttpRequestException ex)
        {
            LogRegistrationFailed(_logger, LoggedFailure.Reason(ex), null);
            StatusMessage = $"Cloud Imaging service unreachable: {ex.Message}";
        }
        catch (Exception ex)
        {
            LogRegistrationFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsWaitingForNetwork = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Best-effort wait (not a hard gate) for at least one non-loopback network adapter to be
    /// up with a routable (non link-local) IPv4 address. If none appears within
    /// <see cref="NetworkWaitTimeout"/>, registration proceeds anyway and any real
    /// connectivity problem surfaces through the normal CreateSessionAsync error path.
    /// </summary>
    private async Task WaitForNetworkAsync()
    {
        if (NetworkReadiness.IsReady())
            return;

        IsWaitingForNetwork = true;
        var deadline = DateTime.UtcNow + NetworkWaitTimeout;
        while (!NetworkReadiness.IsReady() && DateTime.UtcNow < deadline)
            await Task.Delay(NetworkWaitPollInterval);

        IsWaitingForNetwork = false;
    }

    // ── Location Labels feature: read the technician-selected location, if any, from the USB
    //    preparation manifest Media Builder wrote to the BOOT volume ─────────────────────────

    /// <summary>
    /// Best-effort read of <see cref="UsbPreparationManifest.LocationId"/>/
    /// <see cref="UsbPreparationManifest.LocationName"/> from the manifest on the BOOT volume
    /// this Client is running from. Returns (null, null) on any failure (no BOOT volume, no
    /// manifest, unreadable/corrupt JSON, no location recorded) — registration must never be
    /// blocked by this.
    /// </summary>
    private static (Guid? LocationId, string? LocationName) ReadLocationFromManifest()
    {
        try
        {
            var manifestPath = BootImageDetails.FindMediaManifestPath(out _);
            if (manifestPath is null) return (null, null);

            var manifest = JsonSerializer.Deserialize<UsbPreparationManifest>(File.ReadAllText(manifestPath));
            return manifest is null ? (null, null) : (manifest.LocationId, manifest.LocationName);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── Hardware metadata collection (runs on background thread) ─────────────

    private static DeviceHardwareMetadata CollectHardwareMetadata()
    {
        return new DeviceHardwareMetadata
        {
            MotherboardManufacturer = GetWmiValue("Win32_BaseBoard", "Manufacturer"),
            MotherboardModel        = GetWmiValue("Win32_BaseBoard", "Product"),
            BiosVersion             = GetWmiValue("Win32_BIOS", "SMBIOSBIOSVersion"),
            NicIdentifiers          = GetNetworkAdapters(),
            StorageLayout           = GetStorageLayout(),
        };
    }

    private static string GetSerialNumber() =>
        GetWmiValue("Win32_BIOS", "SerialNumber") ?? "UNKNOWN";

    private static string GetManufacturer() =>
        GetWmiValue("Win32_ComputerSystem", "Manufacturer") ?? "UNKNOWN";

    private static string GetModel() =>
        GetWmiValue("Win32_ComputerSystem", "Model") ?? "UNKNOWN";

    /// <summary>
    /// Serial, manufacturer, model and USB-manifest location, read the same way registration does.
    /// Blocking WMI and file I/O: call off the UI thread.
    /// </summary>
    internal static DeviceIdentity ReadDeviceIdentity()
    {
        var (locationId, locationName) = ReadLocationFromManifest();
        return new DeviceIdentity(GetSerialNumber(), GetManufacturer(), GetModel(), locationId, locationName);
    }

    private static string? GetMacAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress().ToString())
            .FirstOrDefault(a => !string.IsNullOrEmpty(a));
    }

    private static string? GetWmiValue(string className, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
            using var results  = searcher.Get();
            foreach (ManagementObject obj in results)
                return obj[property]?.ToString()?.Trim();
        }
        catch { /* WMI may be unavailable in WinPE — return null */ }
        return null;
    }

    private static List<string> GetNetworkAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => $"{n.Name}|{n.GetPhysicalAddress()}")
                .ToList();
        }
        catch { return []; }
    }

    private static List<string> GetStorageLayout()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Size FROM Win32_DiskDrive");
            using var results  = searcher.Get();
            var layout = new List<string>();
            foreach (ManagementObject d in results.Cast<ManagementObject>())
            {
                var caption = d["Caption"]?.ToString() ?? "Unknown";
                // Win32_DiskDrive.Size is a scripting-era string property, but be defensive
                // against a differently-typed/boxed value (e.g. a raw numeric) rather than
                // silently embedding an unparsed WMI type name in device telemetry.
                var sizeBytes = d["Size"] switch
                {
                    string s when long.TryParse(s, out var parsed) => parsed,
                    IConvertible c => Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture),
                    _ => 0L,
                };
                layout.Add($"{caption}|{sizeBytes}");
            }
            return layout;
        }
        catch { return []; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session registration failed: {Reason}")]
    private static partial void LogRegistrationFailed(ILogger logger, string reason, Exception? ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Check again could not finish the network checks: {Reason}")]
    private static partial void LogRecheckFailed(ILogger logger, string reason, Exception? ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the network adapters: {Reason}")]
    private static partial void LogNetworkReadFailed(ILogger logger, string reason);

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Minimal relay command implementation for WPF view models.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add    => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)    => _execute(parameter);
}

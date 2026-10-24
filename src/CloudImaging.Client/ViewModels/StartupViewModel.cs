using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Client.Logging;
using CloudImaging.Client.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.ViewModels;

/// <summary>Progress of one startup check.</summary>
public enum StartupCheckState
{
    /// <summary>Checking.</summary>
    Running,

    /// <summary>The prerequisite is met.</summary>
    Passed,

    /// <summary>Met only partly: some operations stay available.</summary>
    Warning,

    /// <summary>The prerequisite is not met.</summary>
    Failed,

    /// <summary>Not run, or not needed; nothing depends on it.</summary>
    Skipped,
}

/// <summary>One row on the Getting ready screen.</summary>
public sealed class StartupCheckRow : INotifyPropertyChanged
{
    private StartupCheckState _state = StartupCheckState.Running;
    private string _detail;
    private int? _percent;
    private string? _badge;

    /// <summary>Builds a row in the <see cref="StartupCheckState.Running"/> state.</summary>
    public StartupCheckRow(string title, string runningDetail)
    {
        Title = title;
        _detail = runningDetail;
    }

    /// <summary>What is being checked.</summary>
    public string Title { get; }

    /// <summary>Current finding, or what is being waited for.</summary>
    public string Detail { get => _detail; private set { _detail = value; OnPropertyChanged(); } }

    /// <summary>0 to 100 while a long step runs; null hides the bar.</summary>
    public int? Percent
    {
        get => _percent;
        private set
        {
            _percent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPercent));
        }
    }

    /// <summary>True while <see cref="Percent"/> is shown.</summary>
    public bool HasPercent => _percent is not null;

    /// <summary>Short label beside the title for what happens next; null hides it.</summary>
    public string? Badge
    {
        get => _badge;
        private set
        {
            _badge = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasBadge));
        }
    }

    /// <summary>True while <see cref="Badge"/> is shown.</summary>
    public bool HasBadge => _badge is not null;

    /// <summary>Current state.</summary>
    public StartupCheckState State
    {
        get => _state;
        private set
        {
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsPassed));
            OnPropertyChanged(nameof(IsWarning));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsSkipped));
        }
    }

    /// <summary>True while checking.</summary>
    public bool IsRunning => _state == StartupCheckState.Running;

    /// <summary>True once met.</summary>
    public bool IsPassed => _state == StartupCheckState.Passed;

    /// <summary>True once partly met.</summary>
    public bool IsWarning => _state == StartupCheckState.Warning;

    /// <summary>True once not met.</summary>
    public bool IsFailed => _state == StartupCheckState.Failed;

    /// <summary>True once skipped.</summary>
    public bool IsSkipped => _state == StartupCheckState.Skipped;

    /// <summary>Records the outcome.</summary>
    public void Complete(StartupCheckState state, string detail, string? badge = null)
    {
        Detail = detail;
        Percent = null;
        Badge = badge;
        State = state;
    }

    /// <summary>Reports a running step's progress.</summary>
    public void ReportProgress(string detail, int? percent)
    {
        Detail = detail;
        Percent = percent;
        State = StartupCheckState.Running;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>What startup found, used to decide which operations Operation Selection offers.</summary>
/// <param name="LocalDiskCount">Non-USB disks found; null when they could not be read.</param>
/// <param name="Gateway">Whether the Device Gateway API can be used.</param>
/// <param name="AutopilotAvailability">The portal's Autopilot setting; null when this boot image cannot register.</param>
/// <param name="WifiSupported">True on a Windows RE boot image, which can join Wi-Fi.</param>
/// <param name="WifiJoin">How joining the managed Wi-Fi networks ended; null when it was not needed or not possible.</param>
/// <param name="RestartRequired">True once a newer boot image was written to the media: startup stops and asks for a restart.</param>
/// <param name="AdaptersWithoutDriver">Network devices Windows found without a driver, looked for only when there is no network.</param>
/// <param name="Rechecked">True once Check again has run the network checks on Operation Selection.</param>
public sealed record StartupReadiness(int? LocalDiskCount, GatewayReachability Gateway, AutopilotAvailabilityState? AutopilotAvailability, bool WifiSupported = false, ManagedWifiJoinResult? WifiJoin = null, bool RestartRequired = false, IReadOnlyList<string>? AdaptersWithoutDriver = null, bool Rechecked = false)
{
    /// <summary>Every operation needs the Device Gateway.</summary>
    public bool CanReachService => Gateway == GatewayReachability.Reachable;

    /// <summary>The managed network startup joined, or null.</summary>
    public string? ConnectedWifiSsid => WifiJoin is { Outcome: ManagedWifiOutcome.Connected, Ssid: var ssid } ? ssid : null;
}

/// <summary>
/// Drives the Getting ready screen: checks the prerequisites every operation depends on (a local
/// disk to image, a network connection, the Device Gateway API) so Operation Selection opens
/// already knowing what this device can do. A newer boot image for the media is written before
/// that, and then the screen stops and asks for a restart.
/// </summary>
public sealed partial class StartupViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan DefaultNetworkWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    // Check again follows a cable being plugged in: long enough for DHCP, short enough to feel like a button.
    private static readonly TimeSpan RecheckNetworkWait = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<GatewayProbeResult>> _probeGateway;
    private readonly bool _autopilotToolingPresent;
    private readonly Func<IReadOnlyList<LocalDisk>> _findLocalDisks;
    private readonly Func<string?> _findNetworkAdapter;
    private readonly Func<string?> _findEthernetAdapter;
    private readonly Func<IReadOnlyList<string>> _findAdaptersWithoutDriver;
    private readonly TimeSpan _networkWait;
    private readonly ManagedWifiService? _managedWifi;
    private readonly bool _wifiSupported;
    private readonly Func<CancellationToken, Task<BootImageUpdateCheck>>? _checkBootImageUpdate;
    private readonly Func<Action<BootImageUpdateProgress>, CancellationToken, Task<bool>>? _applyBootImageUpdate;
    private readonly ILogger _logger;
    private ManagedWifiJoinResult? _wifiJoin;
    private IReadOnlyList<string> _adaptersWithoutDriver = [];
    private string? _updatedVersion;
    private bool _isTryingWifi;

    /// <param name="probeGateway">Asks the Device Gateway whether it can be used; normally <see cref="DeviceGatewayApiClient.ProbeAsync"/>.</param>
    /// <param name="autopilotToolingPresent">Whether this boot image can register devices with Autopilot.</param>
    /// <param name="findLocalDisks">Test seam; defaults to the disks formatting would consider.</param>
    /// <param name="findNetworkAdapter">Test seam; defaults to the first adapter with a routable IPv4 address.</param>
    /// <param name="networkWait">How long to wait for a network adapter to come up.</param>
    /// <param name="logger">Defaults to a no-op logger.</param>
    /// <param name="managedWifi">Joins the portal's Wi-Fi networks when Ethernet has no address; null on a Windows PE image.</param>
    /// <param name="wifiSupported">True on a Windows RE boot image.</param>
    /// <param name="checkBootImageUpdate">Checks for a newer boot image once the service is reachable; null hides the row.</param>
    /// <param name="applyBootImageUpdate">Writes the newer image the check found to the media; returns true on success.</param>
    /// <param name="restartDevice">Restart now, offered once the media holds a newer boot image.</param>
    /// <param name="findEthernetAdapter">Test seam; defaults to the first wired adapter with a routable address, or none when <paramref name="findNetworkAdapter"/> is faked.</param>
    /// <param name="findAdaptersWithoutDriver">Test seam; defaults to network devices Windows found without a driver, or none when <paramref name="findNetworkAdapter"/> is faked.</param>
    public StartupViewModel(
        Func<CancellationToken, Task<GatewayProbeResult>> probeGateway,
        bool autopilotToolingPresent,
        Func<IReadOnlyList<LocalDisk>>? findLocalDisks = null,
        Func<string?>? findNetworkAdapter = null,
        TimeSpan? networkWait = null,
        ILogger<StartupViewModel>? logger = null,
        ManagedWifiService? managedWifi = null,
        bool wifiSupported = false,
        Func<CancellationToken, Task<BootImageUpdateCheck>>? checkBootImageUpdate = null,
        Func<Action<BootImageUpdateProgress>, CancellationToken, Task<bool>>? applyBootImageUpdate = null,
        Action? restartDevice = null,
        Func<string?>? findEthernetAdapter = null,
        Func<IReadOnlyList<string>>? findAdaptersWithoutDriver = null)
    {
        _probeGateway = probeGateway;
        _autopilotToolingPresent = autopilotToolingPresent;
        _findLocalDisks = findLocalDisks ?? DiskFormatService.FindLocalDisks;
        _findNetworkAdapter = findNetworkAdapter ?? NetworkReadiness.FindRoutableAdapter;

        // A test that fakes the adapter must not see the real machine's Ethernet or devices.
        _findEthernetAdapter = findEthernetAdapter ?? (findNetworkAdapter is null ? NetworkStatus.FindRoutableEthernet : () => null);
        _findAdaptersWithoutDriver = findAdaptersWithoutDriver ?? (findNetworkAdapter is null ? NetworkStatus.FindDevicesWithoutDriver : () => []);
        _networkWait = networkWait ?? DefaultNetworkWait;
        _managedWifi = managedWifi;
        _wifiSupported = wifiSupported;
        _checkBootImageUpdate = checkBootImageUpdate;
        _applyBootImageUpdate = applyBootImageUpdate;
        _logger = logger ?? NullLogger<StartupViewModel>.Instance;
        RestartCommand = new RelayCommand(_ => restartDevice?.Invoke());
        Checks = new ReadOnlyCollection<StartupCheckRow>(checkBootImageUpdate is null
            ? [DiskCheck, NetworkCheck, ServiceCheck]
            : [DiskCheck, NetworkCheck, ServiceCheck, UpdateCheck]);
    }

    /// <summary>The rows shown on the Getting ready screen, in display order.</summary>
    public IReadOnlyList<StartupCheckRow> Checks { get; }

    /// <summary>True once the media holds a newer boot image; startup stops here.</summary>
    public bool RestartRequired => _updatedVersion is not null;

    /// <summary>Restarts the device.</summary>
    public ICommand RestartCommand { get; }

    /// <summary>True while saved Wi-Fi networks are being tried; the screen then says Ethernet can still be plugged in.</summary>
    public bool IsTryingWifi
    {
        get => _isTryingWifi;
        private set { _isTryingWifi = value; OnPropertyChanged(); }
    }

    internal StartupCheckRow DiskCheck { get; } = new("Local disk", "Detecting disks…");

    internal StartupCheckRow NetworkCheck { get; } = new("Network connection", "Waiting for an IP address…");

    internal StartupCheckRow ServiceCheck { get; } = new("Cloud Imaging service", "Waiting for the network…");

    internal StartupCheckRow UpdateCheck { get; } = new("Boot media update", "Waiting for the service…");

    /// <summary>Runs every check, updating <see cref="Checks"/> as each finishes. Never throws.</summary>
    public async Task<StartupReadiness> RunAsync(CancellationToken ct = default)
    {
        var diskCount = CheckLocalDisksAsync();
        var (gateway, autopilotEnabled) = await CheckConnectivityAsync(joinWifi: true, _networkWait, ct);
        await CheckBootImageUpdateAsync(gateway, ct);
        var readiness = new StartupReadiness(await diskCount, gateway, ResolveAutopilot(gateway, autopilotEnabled), _wifiSupported, _wifiJoin, RestartRequired, _adaptersWithoutDriver);
        LogReadiness(_logger, readiness.LocalDiskCount, readiness.Gateway, readiness.AutopilotAvailability);
        return readiness;
    }

    /// <summary>
    /// Check again on Operation Selection: runs the network and service checks only, without the
    /// saved Wi-Fi networks (Connect to Wi-Fi is next to it), and keeps everything else. Never throws.
    /// </summary>
    public async Task<StartupReadiness> RecheckNetworkAsync(StartupReadiness previous, CancellationToken ct = default)
    {
        var (gateway, autopilotEnabled) = await CheckConnectivityAsync(joinWifi: false, RecheckNetworkWait, ct);
        var readiness = previous with
        {
            Gateway = gateway,
            AutopilotAvailability = ResolveAutopilot(gateway, autopilotEnabled),
            WifiJoin = _wifiJoin,
            AdaptersWithoutDriver = _adaptersWithoutDriver,
            Rechecked = true,
        };
        LogReadiness(_logger, readiness.LocalDiskCount, readiness.Gateway, readiness.AutopilotAvailability);
        return readiness;
    }

    /// <summary>
    /// Only runs once the service answered, so an offline device never waits on it. A newer image
    /// holds startup until it is on the media. Any failure is fine: the media keeps its current boot
    /// image, so the row never shows a failure and startup carries on.
    /// </summary>
    private async Task CheckBootImageUpdateAsync(GatewayReachability gateway, CancellationToken ct)
    {
        if (_checkBootImageUpdate is null)
        {
            return;
        }

        if (gateway != GatewayReachability.Reachable)
        {
            UpdateCheck.Complete(StartupCheckState.Skipped, gateway == GatewayReachability.NoNetwork ? "Skipped: no network." : "Skipped: the service is unavailable.");
            return;
        }

        UpdateCheck.Complete(StartupCheckState.Running, "Checking for a newer boot image…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        BootImageUpdateCheck result;
        try
        {
            result = await _checkBootImageUpdate(timeout.Token);
        }
        catch (Exception ex)
        {
            LogUpdateCheckFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            result = new BootImageUpdateCheck(BootImageUpdateStatus.Failed);
        }

        if (result.Status == BootImageUpdateStatus.UpdateAvailable && _applyBootImageUpdate is not null)
        {
            await ApplyBootImageUpdateAsync(result.Version ?? "");
            return;
        }

        var (state, detail) = result.Status switch
        {
            BootImageUpdateStatus.UpToDate => (StartupCheckState.Passed, $"Up to date ({result.Version})."),
            BootImageUpdateStatus.NotUsbMedia => (StartupCheckState.Skipped, "Not running from prepared USB media."),
            BootImageUpdateStatus.Unavailable => (StartupCheckState.Skipped, "No update offered for this media."),
            _ => (StartupCheckState.Skipped, "Could not check. The current boot image is kept."),
        };
        UpdateCheck.Complete(state, detail);
    }

    private async Task ApplyBootImageUpdateAsync(string version)
    {
        UpdateCheck.ReportProgress($"Downloading {version}…", 0);
        bool applied;
        try
        {
            // Not tied to the startup time limit: a boot image takes minutes, and a stalled download times out on its own.
            applied = await _applyBootImageUpdate!(p => UpdateCheck.ReportProgress(DescribeProgress(version, p), p.Phase == BootImageUpdatePhase.Applying ? null : p.Percent), CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogUpdateCheckFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            applied = false;
        }

        if (!applied)
        {
            UpdateCheck.Complete(StartupCheckState.Skipped, "Could not update. The current boot image is kept.");
            return;
        }

        UpdateCheck.Complete(StartupCheckState.Passed, $"Updated to {version}. Restart the device to start from it.", badge: "Restart required");
        _updatedVersion = version;
        OnPropertyChanged(nameof(RestartRequired));
    }

    internal static string DescribeProgress(string version, BootImageUpdateProgress progress) => progress.Phase switch
    {
        BootImageUpdatePhase.Verifying => $"Verifying {version}…",
        BootImageUpdatePhase.Applying => $"Writing {version} to the USB drive…",
        _ when progress.BytesTotal is { } total => string.Create(CultureInfo.InvariantCulture, $"Downloading {version}: {progress.BytesDone / 1_000_000:N0} of {total / 1_000_000:N0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"Downloading {version}: {progress.BytesDone / 1_000_000:N0} MB"),
    };

    private async Task<int?> CheckLocalDisksAsync()
    {
        try
        {
            var disks = await Task.Run(_findLocalDisks);
            switch (disks.Count)
            {
                case 0:
                    DiskCheck.Complete(StartupCheckState.Failed, "None detected. Add the storage driver to the boot image.");
                    break;
                case 1:
                    DiskCheck.Complete(StartupCheckState.Passed, DescribeDisk(disks[0]));
                    break;
                default:
                    DiskCheck.Complete(StartupCheckState.Warning, $"{disks.Count} detected. Imaging requires exactly one.");
                    break;
            }

            return disks.Count;
        }
        catch (Exception ex)
        {
            // Unreadable disks must not block the screen; formatting reports its own error later.
            LogDiskCheckFailed(_logger, ex);
            DiskCheck.Complete(StartupCheckState.Warning, "Disk query failed. See the log.");
            return null;
        }
    }

    private async Task<(GatewayReachability Gateway, bool? AutopilotEnabled)> CheckConnectivityAsync(bool joinWifi, TimeSpan networkWait, CancellationToken ct)
    {
        // Check again re-runs this; the Wi-Fi association from an earlier run survives, a failure does not.
        var previousJoin = _wifiJoin;
        _wifiJoin = null;
        _adaptersWithoutDriver = [];

        // A freshly booted NIC can take several seconds to get a DHCP lease.
        var adapter = FindAdapter();
        var deadline = DateTime.UtcNow + networkWait;
        while (adapter is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NetworkPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            adapter = FindAdapter();
        }

        // Ethernet always wins: a wired address ends any Wi-Fi connection, and Wi-Fi is only tried without one.
        string? wifiSsid = null;
        var ethernet = FindEthernet();
        if (ethernet is not null)
        {
            adapter = ethernet;
            await LeaveWifiAsync(ct);
        }
        else if (adapter is not null && previousJoin?.Outcome == ManagedWifiOutcome.Connected)
        {
            _wifiJoin = previousJoin;
            wifiSsid = previousJoin.Ssid;
        }
        else if (adapter is null && joinWifi && _wifiSupported && _managedWifi is not null)
        {
            ethernet = await JoinWifiUnlessEthernetAsync(_managedWifi, ct);
            if (ethernet is not null)
            {
                adapter = ethernet;
            }
            else if (_wifiJoin?.Outcome == ManagedWifiOutcome.Connected)
            {
                wifiSsid = _wifiJoin.Ssid;
                adapter = FindAdapter();
            }
        }

        if (adapter is null)
        {
            _adaptersWithoutDriver = await Task.Run(FindAdaptersWithoutDriver, CancellationToken.None);
        }

        ServiceCheck.Complete(StartupCheckState.Running, "Connecting…");

        // Probe even without an IPv4 adapter: an IPv6-only network can still reach the gateway.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeTimeout.CancelAfter(ProbeTimeout);
        GatewayProbeResult probe;
        try
        {
            probe = await _probeGateway(probeTimeout.Token);
        }
        catch (Exception ex)
        {
            LogProbeFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            probe = new GatewayProbeResult(GatewayReachability.Unreachable, null);
        }

        var reachability = probe.Reachability;
        if (adapter is null && reachability == GatewayReachability.Unreachable)
        {
            reachability = GatewayReachability.NoNetwork;
        }

        if (adapter is not null)
        {
            NetworkCheck.Complete(StartupCheckState.Passed, wifiSsid is not null ? $"Wi-Fi {wifiSsid}: {adapter}" : ethernet is not null && _wifiSupported ? $"Ethernet: {adapter}" : adapter);
        }
        else if (reachability is GatewayReachability.NoNetwork or GatewayReachability.NotConfigured)
        {
            NetworkCheck.Complete(StartupCheckState.Failed, _adaptersWithoutDriver.Count > 0 ? $"No IP address. {_adaptersWithoutDriver[0]} has no driver in the boot image."
                : _wifiSupported ? DescribeWifiFailure(_wifiJoin)
                : "No IP address. If Ethernet is connected, add the network adapter driver to the boot image.");
        }
        else
        {
            NetworkCheck.Complete(StartupCheckState.Passed, "Connected");
        }

        ServiceCheck.Complete(
            reachability == GatewayReachability.Reachable ? StartupCheckState.Passed : StartupCheckState.Failed,
            reachability switch
            {
                GatewayReachability.Reachable => "Connected",
                GatewayReachability.NotConfigured => "No service URL in the boot image.",
                GatewayReachability.NoNetwork => "Skipped: no network.",
                GatewayReachability.ServiceUnavailable => "Server error.",
                GatewayReachability.CertificateRejected => "Boot media certificate rejected.",
                _ => "No response. Check DNS, proxy and outbound HTTPS.",
            });

        return (reachability, probe.AutopilotEnabled);
    }

    /// <summary>
    /// Tries the saved Wi-Fi networks while watching for Ethernet. Returns the Ethernet adapter when
    /// it got an address first, which stops the attempt; otherwise leaves the outcome in <c>_wifiJoin</c>.
    /// </summary>
    private async Task<string?> JoinWifiUnlessEthernetAsync(ManagedWifiService managedWifi, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsTryingWifi = true;
        try
        {
            var join = managedWifi.JoinAsync(detail => NetworkCheck.Complete(StartupCheckState.Running, detail), stop.Token);
            var watch = WaitForEthernetAsync(stop.Token);
            var first = await Task.WhenAny(join, watch);
            await stop.CancelAsync();

            if (first == watch && await watch is { } ethernet)
            {
                try
                {
                    await join;
                }
                catch (OperationCanceledException)
                {
                    // Stopped on purpose.
                }
                catch (Exception ex)
                {
                    LogWifiJoinFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
                }

                // The attempt may have connected just before it was stopped.
                await LeaveWifiAsync(ct);
                LogEthernetTookOver(_logger, ethernet);
                return ethernet;
            }

            try
            {
                _wifiJoin = await join;
            }
            catch (Exception ex)
            {
                LogWifiJoinFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            }
            return null;
        }
        finally
        {
            IsTryingWifi = false;
        }
    }

    private async Task<string?> WaitForEthernetAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(NetworkPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (FindEthernet() is { } ethernet)
            {
                return ethernet;
            }
        }
    }

    private async Task LeaveWifiAsync(CancellationToken ct)
    {
        if (_wifiSupported && _managedWifi is not null)
        {
            try
            {
                await _managedWifi.Wireless.DisconnectAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Startup ran out of time; Windows still prefers the wired route.
            }
        }
    }

    private string? FindEthernet()
    {
        try
        {
            return _findEthernetAdapter();
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<string> FindAdaptersWithoutDriver()
    {
        try
        {
            return _findAdaptersWithoutDriver();
        }
        catch
        {
            return [];
        }
    }

    private string? FindAdapter()
    {
        try
        {
            return _findNetworkAdapter();
        }
        catch
        {
            return null;
        }
    }

    private AutopilotAvailabilityState? ResolveAutopilot(GatewayReachability gateway, bool? enabled)
    {
        if (!_autopilotToolingPresent)
        {
            return null;
        }

        return (gateway, enabled) switch
        {
            (GatewayReachability.Reachable, true) => AutopilotAvailabilityState.Enabled,
            (GatewayReachability.Reachable, false) => AutopilotAvailabilityState.Disabled,
            _ => AutopilotAvailabilityState.Unreachable,
        };
    }

    /// <summary>Why a Windows RE image is still offline, for the Network connection row.</summary>
    internal static string DescribeWifiFailure(ManagedWifiJoinResult? join) => join?.Outcome switch
    {
        ManagedWifiOutcome.PasswordRejected => $"No Ethernet. {join.Ssid} rejected the saved password.",
        ManagedWifiOutcome.NoIpAddress => $"No Ethernet. {join.Ssid} gave no IP address.",
        ManagedWifiOutcome.NoAdapter => "No Ethernet and no Wi-Fi adapter. Add the Wi-Fi driver to the boot image.",
        ManagedWifiOutcome.NoNetworksSaved => "No Ethernet, and no Wi-Fi networks are saved for this location.",
        _ => "No Ethernet, and none of the saved Wi-Fi networks are in range.",
    };

    private static string DescribeDisk(LocalDisk disk)
    {
        var model = string.IsNullOrWhiteSpace(disk.Model) ? $"Disk {disk.Index}" : disk.Model;
        return disk.SizeBytes == 0
            ? model
            : string.Create(CultureInfo.InvariantCulture, $"{model} ({disk.SizeBytes / 1_000_000_000d:0} GB)");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup checks: local disks {DiskCount}, Device Gateway {Gateway}, Autopilot {Autopilot}.")]
    private static partial void LogReadiness(ILogger logger, int? diskCount, GatewayReachability gateway, AutopilotAvailabilityState? autopilot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup check could not read the local disks.")]
    private static partial void LogDiskCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup check could not probe the Device Gateway: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string reason, Exception? ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup could not join the managed Wi-Fi networks: {Reason}")]
    private static partial void LogWifiJoinFailed(ILogger logger, string reason, Exception? ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ethernet got an address while Wi-Fi was being tried; using {Adapter}.")]
    private static partial void LogEthernetTookOver(ILogger logger, string adapter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup could not check for a boot image update: {Reason}")]
    private static partial void LogUpdateCheckFailed(ILogger logger, string reason, Exception? ex);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

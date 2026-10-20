using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
}

/// <summary>One row on the Getting ready screen.</summary>
public sealed class StartupCheckRow : INotifyPropertyChanged
{
    private StartupCheckState _state = StartupCheckState.Running;
    private string _detail;

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

    /// <summary>Records the outcome.</summary>
    public void Complete(StartupCheckState state, string detail)
    {
        Detail = detail;
        State = state;
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
public sealed record StartupReadiness(int? LocalDiskCount, GatewayReachability Gateway, AutopilotAvailabilityState? AutopilotAvailability, bool WifiSupported = false, ManagedWifiJoinResult? WifiJoin = null)
{
    /// <summary>Every operation needs the Device Gateway.</summary>
    public bool CanReachService => Gateway == GatewayReachability.Reachable;

    /// <summary>The managed network startup joined, or null.</summary>
    public string? ConnectedWifiSsid => WifiJoin is { Outcome: ManagedWifiOutcome.Connected, Ssid: var ssid } ? ssid : null;
}

/// <summary>
/// Drives the Getting ready screen: checks the prerequisites every operation depends on (a local
/// disk to image, a network connection, the Device Gateway API) so Operation Selection opens
/// already knowing what this device can do.
/// </summary>
public sealed partial class StartupViewModel
{
    private static readonly TimeSpan DefaultNetworkWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<GatewayProbeResult>> _probeGateway;
    private readonly bool _autopilotToolingPresent;
    private readonly Func<IReadOnlyList<LocalDisk>> _findLocalDisks;
    private readonly Func<string?> _findNetworkAdapter;
    private readonly TimeSpan _networkWait;
    private readonly ManagedWifiService? _managedWifi;
    private readonly bool _wifiSupported;
    private readonly ILogger _logger;
    private ManagedWifiJoinResult? _wifiJoin;

    /// <param name="probeGateway">Asks the Device Gateway whether it can be used; normally <see cref="DeviceGatewayApiClient.ProbeAsync"/>.</param>
    /// <param name="autopilotToolingPresent">Whether this boot image can register devices with Autopilot.</param>
    /// <param name="findLocalDisks">Test seam; defaults to the disks formatting would consider.</param>
    /// <param name="findNetworkAdapter">Test seam; defaults to the first adapter with a routable IPv4 address.</param>
    /// <param name="networkWait">How long to wait for a network adapter to come up.</param>
    /// <param name="logger">Defaults to a no-op logger.</param>
    /// <param name="managedWifi">Joins the portal's Wi-Fi networks when Ethernet has no address; null on a Windows PE image.</param>
    /// <param name="wifiSupported">True on a Windows RE boot image.</param>
    public StartupViewModel(
        Func<CancellationToken, Task<GatewayProbeResult>> probeGateway,
        bool autopilotToolingPresent,
        Func<IReadOnlyList<LocalDisk>>? findLocalDisks = null,
        Func<string?>? findNetworkAdapter = null,
        TimeSpan? networkWait = null,
        ILogger<StartupViewModel>? logger = null,
        ManagedWifiService? managedWifi = null,
        bool wifiSupported = false)
    {
        _probeGateway = probeGateway;
        _autopilotToolingPresent = autopilotToolingPresent;
        _findLocalDisks = findLocalDisks ?? DiskFormatService.FindLocalDisks;
        _findNetworkAdapter = findNetworkAdapter ?? NetworkReadiness.FindRoutableAdapter;
        _networkWait = networkWait ?? DefaultNetworkWait;
        _managedWifi = managedWifi;
        _wifiSupported = wifiSupported;
        _logger = logger ?? NullLogger<StartupViewModel>.Instance;
        Checks = new ReadOnlyCollection<StartupCheckRow>([DiskCheck, NetworkCheck, ServiceCheck]);
    }

    /// <summary>The rows shown on the Getting ready screen, in display order.</summary>
    public IReadOnlyList<StartupCheckRow> Checks { get; }

    internal StartupCheckRow DiskCheck { get; } = new("Local disk", "Detecting disks…");

    internal StartupCheckRow NetworkCheck { get; } = new("Network connection", "Waiting for an IP address…");

    internal StartupCheckRow ServiceCheck { get; } = new("Cloud Imaging service", "Waiting for the network…");

    /// <summary>Runs every check, updating <see cref="Checks"/> as each finishes. Never throws.</summary>
    public async Task<StartupReadiness> RunAsync(CancellationToken ct = default)
    {
        var diskCount = CheckLocalDisksAsync();
        var (gateway, autopilotEnabled) = await CheckConnectivityAsync(ct);
        var readiness = new StartupReadiness(await diskCount, gateway, ResolveAutopilot(gateway, autopilotEnabled), _wifiSupported, _wifiJoin);
        LogReadiness(_logger, readiness.LocalDiskCount, readiness.Gateway, readiness.AutopilotAvailability);
        return readiness;
    }

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

    private async Task<(GatewayReachability Gateway, bool? AutopilotEnabled)> CheckConnectivityAsync(CancellationToken ct)
    {
        // Check again re-runs this; the Wi-Fi association from an earlier run survives, a failure does not.
        var previousJoin = _wifiJoin;
        _wifiJoin = null;

        // A freshly booted NIC can take several seconds to get a DHCP lease.
        var adapter = FindAdapter();
        var deadline = DateTime.UtcNow + _networkWait;
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

        // Ethernet always wins: Wi-Fi is only tried once the usual wait found no address.
        string? wifiSsid = null;
        if (adapter is not null && previousJoin?.Outcome == ManagedWifiOutcome.Connected)
        {
            _wifiJoin = previousJoin;
            wifiSsid = previousJoin.Ssid;
        }
        else if (adapter is null && _wifiSupported && _managedWifi is not null)
        {
            try
            {
                _wifiJoin = await _managedWifi.JoinAsync(detail => NetworkCheck.Complete(StartupCheckState.Running, detail), ct);
            }
            catch (Exception ex)
            {
                LogWifiJoinFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            }
            if (_wifiJoin?.Outcome == ManagedWifiOutcome.Connected)
            {
                wifiSsid = _wifiJoin.Ssid;
                adapter = FindAdapter();
            }
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
            reachability = GatewayReachability.NoNetwork;

        if (adapter is not null)
            NetworkCheck.Complete(StartupCheckState.Passed, wifiSsid is null ? adapter : $"Wi-Fi {wifiSsid}: {adapter}");
        else if (reachability is GatewayReachability.NoNetwork or GatewayReachability.NotConfigured)
            NetworkCheck.Complete(StartupCheckState.Failed, _wifiSupported ? DescribeWifiFailure(_wifiJoin) : "No IP address. If Ethernet is connected, add the network adapter driver to the boot image.");
        else
            NetworkCheck.Complete(StartupCheckState.Passed, "Connected");

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
            return null;

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
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Client.Services;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for <see cref="Views.WifiConnectionWindow"/> — the WinPE Client's "Connect to
/// Wi-Fi" support tool. Scans on construction, lets the technician pick a network and (for
/// password-protected networks) enter a passphrase, then connects via
/// <see cref="WirelessConnectionService"/>. No Wi-Fi credential is ever persisted by this
/// ViewModel or the underlying service — see <see cref="WirelessConnectionService"/> remarks.
/// </summary>
public sealed class WifiConnectionViewModel : INotifyPropertyChanged
{
    private readonly WirelessConnectionService _wifiService;
    private readonly ManagedWifiService? _managedWifi;
    private readonly Action? _closeRequested;
    private VisibleWifiNetwork? _selectedNetwork;
    private bool _isScanning;
    private bool _isConnecting;
    private string? _statusMessage;
    private string? _infoMessage;
    private bool _hasSucceeded;
    private bool _hasConnected;
    private bool _managedPasswordRejected;

    /// <summary>Builds the view model and starts an initial network scan.</summary>
    /// <param name="wifiService">Defaults to a real netsh-based service.</param>
    /// <param name="closeRequested">Closes the window.</param>
    /// <param name="managedWifi">The portal's networks: listed first, in portal order, and joined without a password.</param>
    public WifiConnectionViewModel(WirelessConnectionService? wifiService = null, Action? closeRequested = null, ManagedWifiService? managedWifi = null)
    {
        _wifiService    = wifiService ?? new WirelessConnectionService();
        _managedWifi    = managedWifi;
        _closeRequested = closeRequested;

        RefreshCommand       = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsConnecting);
        ConnectCommand        = new RelayCommand(async password => await ConnectAsync(password as string), _ => CanConnect);
        ClearSelectionCommand = new RelayCommand(_ => SelectedNetwork = null, _ => SelectedNetwork is not null && !IsConnecting);
        CloseCommand          = new RelayCommand(_ => _closeRequested?.Invoke());

        _ = RefreshAsync();
    }

    /// <summary>Networks found by the last scan, ordered by signal strength.</summary>
    public System.Collections.ObjectModel.ObservableCollection<VisibleWifiNetwork> Networks { get; } = [];

    /// <summary>The network currently picked from <see cref="Networks"/>, or null when none is selected.</summary>
    public VisibleWifiNetwork? SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            _selectedNetwork = value;
            _managedPasswordRejected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ShowPasswordField));
            OnPropertyChanged(nameof(IsManagedSelection));
            OnPropertyChanged(nameof(ManagedNote));
            // Picking a (different) network starts the connect flow fresh rather than carrying
            // over a stale error/success message left over from whatever was selected before.
            StatusMessage = null;
            HasSucceeded  = false;
        }
    }

    /// <summary>True only when a supported (Open/Personal-PSK) network is selected and no scan/connect is already in flight.</summary>
    public bool CanConnect => SelectedNetwork is { IsSupported: true } && !IsScanning && !IsConnecting;

    /// <summary>True once a network is picked from the list — reveals the inline connect panel (mirrors the Windows Wi-Fi flyout's per-network expand).</summary>
    public bool HasSelection => SelectedNetwork is not null;

    /// <summary>
    /// True when the selected network needs a passphrase typed here. A managed network needs none,
    /// unless its saved password was just rejected.
    /// </summary>
    public bool ShowPasswordField => SelectedNetwork?.AuthKind == WifiAuthKind.PersonalPsk && (!SelectedNetwork.IsManaged || _managedPasswordRejected);

    /// <summary>True when the selected network was saved by the organization.</summary>
    public bool IsManagedSelection => SelectedNetwork?.IsManaged == true;

    /// <summary>Explains why no password is asked for, or that the saved one was rejected.</summary>
    public string ManagedNote => _managedPasswordRejected
        ? "The saved password was rejected. Enter the current one to connect this device. It is used for this session only."
        : "Saved by your organization. No password needed.";

    /// <summary>True while a network scan is in flight.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConnect)); }
    }

    /// <summary>True while a connection attempt is in flight.</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set { _isConnecting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConnect)); }
    }

    /// <summary>Result text of the last scan or connect attempt; null before either has run.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>True when <see cref="StatusMessage"/> describes a failure (drives an error-styled InfoBar).</summary>
    public bool HasError => !string.IsNullOrEmpty(StatusMessage) && !HasSucceeded;

    /// <summary>True when <see cref="StatusMessage"/> describes a successful connection (drives a success-styled InfoBar).</summary>
    public bool HasSucceeded
    {
        get => _hasSucceeded;
        private set { _hasSucceeded = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>True once any connection attempt in this window succeeded, so the caller can re-check connectivity.</summary>
    public bool HasConnected
    {
        get => _hasConnected;
        private set { _hasConnected = value; OnPropertyChanged(); }
    }

    /// <summary>Neutral note about the last scan, e.g. that no networks were found yet.</summary>
    public string? InfoMessage
    {
        get => _infoMessage;
        private set { _infoMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasInfo)); }
    }

    /// <summary>True when <see cref="InfoMessage"/> is set.</summary>
    public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);

    /// <summary>Re-scans for nearby networks.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Connects to <see cref="SelectedNetwork"/> using the entered passphrase (if any).</summary>
    public ICommand ConnectCommand { get; }

    /// <summary>Clears <see cref="SelectedNetwork"/>, hiding the inline connect panel.</summary>
    public ICommand ClearSelectionCommand { get; }

    /// <summary>Closes the Wi-Fi window.</summary>
    public ICommand CloseCommand { get; }

    private async Task RefreshAsync()
    {
        IsScanning     = true;
        StatusMessage  = null;
        InfoMessage    = null;
        HasSucceeded   = false;
        try
        {
            var result = await _wifiService.ScanAsync();
            Networks.Clear();
            foreach (var network in Merge(result.Networks, _managedWifi?.Networks ?? []))
                Networks.Add(network);

            if (result.Status == WifiScanStatus.Ok)
                InfoMessage = Networks.Count == 0 ? "No networks found. The first scan can take a few seconds; select Refresh to scan again." : null;
            else
                StatusMessage = DescribeScanFailure(result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not scan for networks: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ConnectAsync(string? password)
    {
        if (SelectedNetwork is null)
            return;

        IsConnecting  = true;
        StatusMessage = null;
        HasSucceeded  = false;
        var selected = SelectedNetwork;
        var managed = selected.IsManaged ? _managedWifi?.Find(selected.Ssid) : null;
        try
        {
            // A typed password wins, so a technician can recover from a rejected saved one. It is never stored.
            var result = managed is not null
                ? await _wifiService.ConnectAsync(selected.Ssid, string.IsNullOrEmpty(password) ? managed.Password : password, managed.Security, managed.Hidden)
                : await _wifiService.ConnectAsync(selected.Ssid, password);
            HasSucceeded = result == WifiConnectResult.Success;
            HasConnected |= HasSucceeded;
            if (managed is not null && !HasSucceeded && result != WifiConnectResult.NoAdapter)
            {
                _managedPasswordRejected = true;
                OnPropertyChanged(nameof(ShowPasswordField));
                OnPropertyChanged(nameof(ManagedNote));
            }
            StatusMessage = result switch
            {
                WifiConnectResult.Success   => $"Connected to {selected.Ssid}.",
                WifiConnectResult.NoAdapter => "No wireless adapter detected.",
                WifiConnectResult.Timeout when managed is not null => $"{selected.Ssid} did not accept the connection. It may be out of range, or its password changed.",
                WifiConnectResult.Timeout   => "Connection not confirmed within 15 seconds. Check the passphrase.",
                _ when managed is not null  => $"{selected.Ssid} rejected the saved password.",
                _                           => "Connection failed. Check the passphrase.",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// Managed networks first, in portal order: those in range with their signal, hidden ones even
    /// though a scan never shows them. Then every other network by signal strength.
    /// </summary>
    internal static IEnumerable<VisibleWifiNetwork> Merge(IReadOnlyList<VisibleWifiNetwork> scanned, IReadOnlyList<CloudImaging.Contracts.Models.ManagedWifiNetwork> managed)
    {
        var bySsid = scanned.GroupBy(n => n.Ssid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.MaxBy(n => n.SignalPercent)!, StringComparer.Ordinal);
        var managedSsids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var network in managed)
        {
            if (!managedSsids.Add(network.Ssid))
                continue;
            if (bySsid.TryGetValue(network.Ssid, out var seen))
                yield return seen with { AuthKind = WifiAuthKind.PersonalPsk, IsSupported = true, IsManaged = true, IsHidden = network.Hidden };
            else if (network.Hidden)
                yield return new VisibleWifiNetwork(network.Ssid, 0, WifiAuthKind.PersonalPsk, true, IsManaged: true, IsHidden: true);
        }

        foreach (var network in scanned.Where(n => !managedSsids.Contains(n.Ssid)).OrderByDescending(n => n.SignalPercent))
            yield return network;
    }

    private static string DescribeScanFailure(WifiScanResult result) => result.Status switch
    {
        WifiScanStatus.NotSupported => "Wi-Fi is not available in this boot image (WinPE has no WLAN stack). Use Ethernet.",
        WifiScanStatus.ServiceNotRunning => "WLAN AutoConfig service (wlansvc) failed to start.",
        WifiScanStatus.NoAdapter => "No wireless adapter detected. Add the Wi-Fi driver to the boot image.",
        WifiScanStatus.LocationPermissionRequired => "Windows requires location services to list Wi-Fi networks. Turn them on in Settings.",
        _ => string.IsNullOrWhiteSpace(result.Detail) ? "Scan failed." : $"Scan failed: {result.Detail}",
    };

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

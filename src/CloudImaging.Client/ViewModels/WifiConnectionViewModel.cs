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
    private readonly Action? _closeRequested;
    private WifiNetwork? _selectedNetwork;
    private bool _isScanning;
    private bool _isConnecting;
    private string? _statusMessage;
    private bool _hasSucceeded;

    public WifiConnectionViewModel(WirelessConnectionService? wifiService = null, Action? closeRequested = null)
    {
        _wifiService    = wifiService ?? new WirelessConnectionService();
        _closeRequested = closeRequested;

        RefreshCommand       = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsConnecting);
        ConnectCommand        = new RelayCommand(async password => await ConnectAsync(password as string), _ => CanConnect);
        ClearSelectionCommand = new RelayCommand(_ => SelectedNetwork = null, _ => SelectedNetwork is not null && !IsConnecting);
        CloseCommand          = new RelayCommand(_ => _closeRequested?.Invoke());

        _ = RefreshAsync();
    }

    public System.Collections.ObjectModel.ObservableCollection<WifiNetwork> Networks { get; } = [];

    public WifiNetwork? SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            _selectedNetwork = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ShowPasswordField));
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

    /// <summary>True when the selected network needs a passphrase (WPA/WPA2/WPA3-Personal). Open networks show no password field at all.</summary>
    public bool ShowPasswordField => SelectedNetwork?.AuthKind == WifiAuthKind.PersonalPsk;

    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConnect)); }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set { _isConnecting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConnect)); }
    }

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

    public ICommand RefreshCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CloseCommand { get; }

    private async Task RefreshAsync()
    {
        IsScanning     = true;
        StatusMessage  = null;
        HasSucceeded   = false;
        try
        {
            var networks = await _wifiService.ScanAsync();
            Networks.Clear();
            foreach (var network in networks.OrderByDescending(n => n.SignalPercent))
                Networks.Add(network);
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
        try
        {
            var result = await _wifiService.ConnectAsync(SelectedNetwork.Ssid, password);
            HasSucceeded = result == WifiConnectResult.Success;
            StatusMessage = result switch
            {
                WifiConnectResult.Success   => $"Connected to {SelectedNetwork.Ssid}.",
                WifiConnectResult.NoAdapter => "No wireless adapter was found.",
                WifiConnectResult.Timeout   => "Could not confirm the connection in time. Check the password and try again.",
                _                           => "Could not connect \u2014 check the password and try again.",
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

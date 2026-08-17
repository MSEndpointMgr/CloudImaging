using System.ComponentModel;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Contracts.Models;
using CloudImaging.Client.Services;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the OperationSelectionView (T032).
/// Collects device hardware metadata silently before session registration (FR-001a).
/// </summary>
public sealed class OperationSelectionViewModel : INotifyPropertyChanged
{
    /// <summary>Maximum time to wait for a routable network adapter before giving up and proceeding anyway.</summary>
    private static readonly TimeSpan NetworkWaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkWaitPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Action<CreateSessionResponse, string> _navigate;
    private string? _selectedOperation;
    private string? _statusMessage;
    private bool _isWaitingForNetwork;
    private bool _isBusy;

    public OperationSelectionViewModel(
        DeviceGatewayApiClient gatewayClient,
        Action<CreateSessionResponse, string> navigate)
    {
        _gatewayClient = gatewayClient;
        _navigate      = navigate;

        SelectOperationCommand = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand        = new RelayCommand(async _ => await ContinueAsync(), _ => CanContinue);
    }

    public string? SelectedOperation
    {
        get => _selectedOperation;
        set { _selectedOperation = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanContinue)); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError     => !string.IsNullOrEmpty(StatusMessage);
    public bool CanContinue  => SelectedOperation is not null && !_isBusy;

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

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    private async Task ContinueAsync()
    {
        if (SelectedOperation is null) return;
        _isBusy = true;
        StatusMessage = null;
        OnPropertyChanged(nameof(CanContinue));

        try
        {
            // In WinPE, the NIC driver/DHCP lease can take a few seconds to finish
            // initializing after this view appears; wait briefly for a routable adapter
            // before registering, instead of spuriously failing on a network error (T032a).
            await WaitForNetworkAsync();

            // Collect hardware metadata silently (FR-001a)
            var hardware = await Task.Run(CollectHardwareMetadata);
            var serialNumber = GetSerialNumber();

            var payload = new DeviceRegistrationPayload
            {
                SerialNumber = serialNumber,
                Manufacturer = GetManufacturer(),
                Model        = GetModel(),
                MacAddress   = GetMacAddress(),
                Hardware     = hardware,
            };

            var sessionResponse = await _gatewayClient.CreateSessionAsync(payload);
            if (sessionResponse is null)
            {
                StatusMessage = "Session creation failed. Please restart.";
                return;
            }

            // Set Bearer token for subsequent Device Gateway calls
            _gatewayClient.SetSessionToken(sessionResponse.DeviceSessionToken);

            // Navigate to SessionInitView — carry the locally-collected serial number forward
            // so it can be displayed later (e.g. ResultsView's NotAuthorized outcome, FR-026)
            // instead of being discarded once sent to the server.
            _navigate(sessionResponse, serialNumber);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsWaitingForNetwork = false;
            _isBusy = false;
            OnPropertyChanged(nameof(CanContinue));
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
        if (IsNetworkReady())
            return;

        IsWaitingForNetwork = true;
        var deadline = DateTime.UtcNow + NetworkWaitTimeout;
        while (!IsNetworkReady() && DateTime.UtcNow < deadline)
            await Task.Delay(NetworkWaitPollInterval);

        IsWaitingForNetwork = false;
    }

    private static bool IsNetworkReady()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.OperationalStatus == OperationalStatus.Up
                && n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a.Address)));
        }
        catch
        {
            return true; // Can't determine in this environment — don't block on it.
        }
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
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
            return results.Cast<ManagementObject>()
                .Select(d => $"{d["Caption"]}|{d["Size"]}")
                .ToList();
        }
        catch { return []; }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

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

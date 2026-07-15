using System.ComponentModel;
using System.Management;
using System.Net.NetworkInformation;
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
    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Action<object> _navigate;
    private string? _selectedOperation;
    private string? _statusMessage;
    private bool _isBusy;

    public OperationSelectionViewModel(
        DeviceGatewayApiClient gatewayClient,
        Action<object> navigate)
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
            // Collect hardware metadata silently (FR-001a)
            var hardware = await Task.Run(CollectHardwareMetadata);

            var payload = new DeviceRegistrationPayload
            {
                SerialNumber = GetSerialNumber(),
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

            // Navigate to SessionInitView
            _navigate(sessionResponse);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(CanContinue));
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

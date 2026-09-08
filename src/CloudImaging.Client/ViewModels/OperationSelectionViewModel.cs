using System.ComponentModel;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private readonly SystemClockSynchronizationService _clockSync;
    private readonly Action<CreateSessionResponse, string> _navigate;
    private readonly Action<bool>? _setMainWindowTopmost;
    private readonly CommandPromptLauncherService _commandPromptLauncher;
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
    public OperationSelectionViewModel(
        DeviceGatewayApiClient gatewayClient,
        Action<CreateSessionResponse, string> navigate,
        SystemClockSynchronizationService? clockSync = null,
        bool commandPromptEnabled = false,
        Action<bool>? setMainWindowTopmost = null,
        CommandPromptLauncherService? commandPromptLauncher = null)
    {
        _gatewayClient          = gatewayClient;
        _navigate               = navigate;
        _clockSync              = clockSync ?? new SystemClockSynchronizationService();
        IsCommandPromptAvailable = commandPromptEnabled;
        _setMainWindowTopmost   = setMainWindowTopmost;
        _commandPromptLauncher  = commandPromptLauncher ?? new CommandPromptLauncherService();

        SelectOperationCommand   = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand          = new RelayCommand(async _ => await ContinueAsync(), _ => CanContinue);
        LaunchCommandPromptCommand = new RelayCommand(_ => LaunchCommandPrompt(), _ => IsCommandPromptAvailable);
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

    public ICommand SelectOperationCommand   { get; }
    public ICommand ContinueCommand          { get; }
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
            var (serialNumber, manufacturer, model, macAddress, hardware, locationId, locationName) = await Task.Run(() =>
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
                return (serial, mfr, mdl, mac, hw, locId, locName);
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
            var bootDrive = FindBootVolumeDriveLetter();
            if (bootDrive is null) return (null, null);

            var manifestPath = Path.Combine(bootDrive, UsbPreparationManifest.FileName);
            if (!File.Exists(manifestPath)) return (null, null);

            var manifest = JsonSerializer.Deserialize<UsbPreparationManifest>(File.ReadAllText(manifestPath));
            return manifest is null ? (null, null) : (manifest.LocationId, manifest.LocationName);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Locates the BOOT-labelled FAT32 volume this Client is running from — mirrors
    /// <c>BootImageSelfUpdateService.FindBootVolumeDriveLetter</c>/
    /// <c>UsbPartitionProvisioningService.FindBootVolumeDriveLetter</c> in the Media Builder,
    /// which creates and labels this same volume during USB preparation.
    /// </summary>
    private static string? FindBootVolumeDriveLetter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, Label FROM Win32_Volume WHERE Label='BOOT'");
            using var results = searcher.Get();
            foreach (ManagementObject volume in results)
            {
                var letter = volume["DriveLetter"]?.ToString();
                if (!string.IsNullOrWhiteSpace(letter))
                    return letter;
            }
        }
        catch (ManagementException)
        {
            // Treated the same as "not found" by the caller.
        }
        return null;
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

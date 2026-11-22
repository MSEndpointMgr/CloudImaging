using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CloudImaging.Client.Services;

/// <summary>Wired or wireless.</summary>
public enum NetworkAdapterKind
{
    /// <summary>A wired adapter, built in or on USB.</summary>
    Ethernet,

    /// <summary>A wireless adapter.</summary>
    WiFi,
}

/// <summary>What an adapter can do right now.</summary>
public enum NetworkAdapterState
{
    /// <summary>Up with a routable IPv4 address.</summary>
    Connected,

    /// <summary>Up, but DHCP has not given it an address.</summary>
    NoAddress,

    /// <summary>Down: no cable, or not joined to a network.</summary>
    Disconnected,

    /// <summary>Windows found the device but the boot image has no driver for it.</summary>
    NoDriver,
}

/// <summary>One network adapter as the Network panel shows it.</summary>
/// <param name="Name">Adapter description, or the device name when it has no driver.</param>
/// <param name="Kind">Wired or wireless; a device without a driver is listed as wired.</param>
/// <param name="State">What it can do right now.</param>
/// <param name="Address">Routable IPv4 address when connected.</param>
public sealed record NetworkAdapterInfo(string Name, NetworkAdapterKind Kind, NetworkAdapterState State, string? Address = null)
{
    /// <summary>"Adapter (address)", the form the startup checks use.</summary>
    public string Display => Address is null ? Name : $"{Name} ({Address})";
}

/// <summary>Every network adapter on the device at one moment.</summary>
/// <param name="Adapters">Adapters with a driver first, then devices without one.</param>
public sealed record NetworkSnapshot(IReadOnlyList<NetworkAdapterInfo> Adapters)
{
    /// <summary>No adapters at all.</summary>
    public static NetworkSnapshot Empty { get; } = new([]);

    /// <summary>The wired adapter in use, if any.</summary>
    public NetworkAdapterInfo? Ethernet => Adapters.FirstOrDefault(a => a is { Kind: NetworkAdapterKind.Ethernet, State: NetworkAdapterState.Connected });

    /// <summary>The wireless adapter with an address, if any.</summary>
    public NetworkAdapterInfo? WiFi => Adapters.FirstOrDefault(a => a is { Kind: NetworkAdapterKind.WiFi, State: NetworkAdapterState.Connected });

    /// <summary>Devices Windows found without a driver.</summary>
    public IReadOnlyList<NetworkAdapterInfo> WithoutDriver => [.. Adapters.Where(a => a.State == NetworkAdapterState.NoDriver)];
}

/// <summary>Reads the device's network adapters, including network devices whose driver is missing from the boot image.</summary>
public static partial class NetworkStatus
{
    /// <summary>Reads every physical adapter, including devices without a driver. Never throws; unreadable parts are left out.</summary>
    public static NetworkSnapshot Read() =>
        new([.. ReadAdapters(FindPhysicalAdapterIds()), .. FindNetworkDevicesWithoutDriver()]);

    /// <summary>Adapters that have a driver, without virtual ones. Cheap enough to poll when <paramref name="physicalIds"/> is null: no WMI.</summary>
    /// <param name="physicalIds">Interface GUIDs of physical adapters; null when unknown, which leaves only the name filter.</param>
    public static IReadOnlyList<NetworkAdapterInfo> ReadAdapters(IReadOnlySet<string>? physicalIds = null)
    {
        var adapters = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (IsVirtualAdapter(adapter.Description) || (physicalIds is not null && !physicalIds.Contains(adapter.Id)))
                    continue;

                var routable = adapter.OperationalStatus == OperationalStatus.Up
                    ? adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address).FirstOrDefault(IsRoutableIPv4)
                    : null;
                if (Classify(adapter.NetworkInterfaceType, adapter.OperationalStatus, routable) is { } info)
                    adapters.Add(info with { Name = adapter.Description });
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter list means none to show.
        }

        return adapters;
    }

    /// <summary>
    /// Interface GUIDs of the adapters Windows counts as physical, which leaves out Wi-Fi Direct and
    /// other virtual adapters a single wireless card brings along. Null when WMI cannot say.
    /// </summary>
    private static HashSet<string>? FindPhysicalAdapterIds()
    {
        try
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher("SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject adapter in results)
            {
                using (adapter)
                {
                    if (adapter["GUID"] is string guid)
                        ids.Add(guid);
                }
            }
            return ids.Count > 0 ? ids : null;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adapters that never carry the device's traffic: Wi-Fi Direct, the kernel debugger, Bluetooth, tunnels and virtual switches.</summary>
    /// <remarks>"Hyper-V" alone is not excluded: a Hyper-V guest's own adapter is "Microsoft Hyper-V Network Adapter".</remarks>
    internal static bool IsVirtualAdapter(string description) => VirtualAdapterPattern().IsMatch(description);

    /// <summary>"Adapter (address)" for the first wired adapter with a routable address, or null.</summary>
    public static string? FindRoutableEthernet() =>
        new NetworkSnapshot(ReadAdapters()).Ethernet?.Display;

    /// <summary>Kind and state for one interface; null for loopback, tunnels and other non-adapter interfaces.</summary>
    internal static NetworkAdapterInfo? Classify(NetworkInterfaceType type, OperationalStatus status, IPAddress? routable)
    {
        NetworkAdapterKind? kind = type switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkAdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetworkAdapterKind.Ethernet,
            _ => null,
        };
        if (kind is null)
            return null;

        var state = status != OperationalStatus.Up ? NetworkAdapterState.Disconnected
            : routable is null ? NetworkAdapterState.NoAddress
            : NetworkAdapterState.Connected;
        return new NetworkAdapterInfo(string.Empty, kind.Value, state, routable?.ToString());
    }

    /// <summary>A link-local 169.254.x.x address means DHCP has not answered.</summary>
    internal static bool IsRoutableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>Names of devices Windows found without a driver (Device Manager code 28) that look like network adapters.</summary>
    public static IReadOnlyList<string> FindDevicesWithoutDriver() => [.. FindNetworkDevicesWithoutDriver().Select(d => d.Name)];

    private static List<NetworkAdapterInfo> FindNetworkDevicesWithoutDriver()
    {
        var devices = new List<NetworkAdapterInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 28");
            using var results = searcher.Get();
            foreach (ManagementObject device in results)
            {
                using (device)
                {
                    var name = device["Name"] as string;
                    var ids = (device["HardwareID"] as string[] ?? []).Concat(device["CompatibleID"] as string[] ?? []).ToList();
                    if (IsNetworkDevice(name, ids))
                    {
                        var label = string.IsNullOrWhiteSpace(name) ? device["PNPDeviceID"] as string ?? "Unknown network adapter" : name;
                        devices.Add(new NetworkAdapterInfo(label, IsWirelessDevice(name, ids) ? NetworkAdapterKind.WiFi : NetworkAdapterKind.Ethernet, NetworkAdapterState.NoDriver));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // A boot image without WMI simply cannot name the device.
        }

        return devices;
    }

    /// <summary>
    /// True for a PCI network controller (class 02), a USB Ethernet device (CDC ECM, EEM or NCM, or
    /// RNDIS), or a device whose reported name says it is a network adapter: USB Ethernet adapters
    /// often use a vendor class, so the name is the only clue. Bluetooth is never a network adapter here.
    /// </summary>
    internal static bool IsNetworkDevice(string? name, IReadOnlyCollection<string> ids) =>
        !(ids.Any(id => BluetoothIdPattern().IsMatch(id)) || (name is not null && BluetoothNamePattern().IsMatch(name)))
        && (ids.Any(id => NetworkDeviceIdPattern().IsMatch(id)) || (name is not null && NetworkDeviceNamePattern().IsMatch(name)));

    /// <summary>True for a wireless network controller (PCI class 0280) or a device named as one.</summary>
    internal static bool IsWirelessDevice(string? name, IEnumerable<string> ids) =>
        ids.Any(id => WirelessDeviceIdPattern().IsMatch(id)) || (name is not null && WirelessDeviceNamePattern().IsMatch(name));

    [GeneratedRegex(@"^PCI\\.*CC_02|^USB\\CLASS_02&SUBCLASS_(06|0C|0D)|^USB\\CLASS_E0&SUBCLASS_01&PROT_03|^USB\\CLASS_EF&SUBCLASS_04&PROT_01", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkDeviceIdPattern();

    [GeneratedRegex(@"\b(ethernet|lan|wlan|gbe|gigabit|network|wi-?fi|wireless|802\.11)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkDeviceNamePattern();

    [GeneratedRegex(@"^PCI\\.*CC_0280", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WirelessDeviceIdPattern();

    [GeneratedRegex(@"\b(wlan|wi-?fi|wireless|802\.11)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WirelessDeviceNamePattern();

    [GeneratedRegex(@"^(BTH|BTHENUM|BTHLE|BTHLEDEVICE)\\|^USB\\CLASS_E0&SUBCLASS_01&PROT_01", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothIdPattern();

    [GeneratedRegex(@"bluetooth|^BT[_ ]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothNamePattern();

    [GeneratedRegex(@"wi-?fi direct|kernel debug|bluetooth|virtual|loopback|teredo|isatap|6to4|wan miniport|vpn|tap-windows|wintun", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAdapterPattern();
}

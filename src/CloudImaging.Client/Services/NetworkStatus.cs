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
    /// <summary>Reads every adapter, including devices without a driver. Never throws; unreadable parts are left out.</summary>
    public static NetworkSnapshot Read() =>
        new([.. ReadAdapters(), .. FindDevicesWithoutDriver().Select(name => new NetworkAdapterInfo(name, NetworkAdapterKind.Ethernet, NetworkAdapterState.NoDriver))]);

    /// <summary>Adapters that have a driver. Cheap enough to poll: no WMI.</summary>
    public static IReadOnlyList<NetworkAdapterInfo> ReadAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
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
    public static IReadOnlyList<string> FindDevicesWithoutDriver()
    {
        var names = new List<string>();
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
                        names.Add(string.IsNullOrWhiteSpace(name) ? device["PNPDeviceID"] as string ?? "Unknown network adapter" : name);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // A boot image without WMI simply cannot name the device.
        }

        return names;
    }

    /// <summary>
    /// True for a PCI network controller (class 02), a USB communications or wireless controller
    /// (classes 02 and E0), or a device whose reported name says it is a network adapter: USB
    /// Ethernet adapters often use a vendor class, so the name is the only clue.
    /// </summary>
    internal static bool IsNetworkDevice(string? name, IEnumerable<string> ids) =>
        ids.Any(id => NetworkDeviceIdPattern().IsMatch(id)) || (name is not null && NetworkDeviceNamePattern().IsMatch(name));

    [GeneratedRegex(@"^PCI\\.*CC_02|^USB\\CLASS_(02|E0)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkDeviceIdPattern();

    [GeneratedRegex(@"\b(ethernet|lan|wlan|gbe|gigabit|network|wi-?fi|wireless|802\.11)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkDeviceNamePattern();
}

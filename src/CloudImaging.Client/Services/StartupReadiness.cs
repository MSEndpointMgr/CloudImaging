using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CloudImaging.Client.Services;

/// <summary>A non-USB disk the imaging pipeline could target.</summary>
/// <param name="Index">WMI disk index, as diskpart numbers it.</param>
/// <param name="Model">Model reported by the disk, if any.</param>
/// <param name="SizeBytes">Capacity in bytes; 0 when unknown.</param>
public sealed record LocalDisk(int Index, string? Model, ulong SizeBytes);

/// <summary>How far a request to the Device Gateway API got.</summary>
public enum GatewayReachability
{
    /// <summary>The gateway answered and accepted the boot-media certificate.</summary>
    Reachable,

    /// <summary>The boot image carries no Device Gateway address.</summary>
    NotConfigured,

    /// <summary>The device has no usable network connection.</summary>
    NoNetwork,

    /// <summary>The device is online but the gateway could not be reached (DNS, routing, firewall, timeout).</summary>
    Unreachable,

    /// <summary>The gateway host answered with a server error.</summary>
    ServiceUnavailable,

    /// <summary>The gateway refused the boot-media certificate.</summary>
    CertificateRejected,
}

/// <summary>Outcome of <see cref="DeviceGatewayApiClient.ProbeAsync"/>.</summary>
/// <param name="Reachability">How far the request got.</param>
/// <param name="AutopilotEnabled">The portal's Autopilot setting, when the gateway reported it.</param>
public sealed record GatewayProbeResult(GatewayReachability Reachability, bool? AutopilotEnabled);

/// <summary>Whether this device has a network adapter that can carry traffic to the Device Gateway.</summary>
internal static class NetworkReadiness
{
    /// <summary>
    /// "Adapter (address)" for the first adapter that is up with a routable IPv4 address, or null
    /// when there is none. A link-local 169.254.x.x address means DHCP has not answered.
    /// </summary>
    public static string? FindRoutableAdapter()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.OperationalStatus != OperationalStatus.Up)
                continue;

            var address = adapter.GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a));
            if (address is not null)
                return $"{adapter.Description} ({address})";
        }

        return null;
    }

    /// <summary>True when <see cref="FindRoutableAdapter"/> finds one, or when the adapters cannot be read at all.</summary>
    public static bool IsReady()
    {
        try
        {
            return FindRoutableAdapter() is not null;
        }
        catch
        {
            return true; // Can't determine in this environment, so don't block on it.
        }
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}

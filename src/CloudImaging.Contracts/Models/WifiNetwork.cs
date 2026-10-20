using System.Text;
using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Models;

/// <summary>Wi-Fi security types devices can join from boot media. Enterprise (802.1X) is not supported.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WifiSecurityType
{
    /// <summary>WPA2-Personal (AES pre-shared key).</summary>
    Wpa2Personal,

    /// <summary>WPA3-Personal (SAE).</summary>
    Wpa3Personal,

    /// <summary>WPA2/WPA3-Personal transition mode.</summary>
    Wpa2Wpa3Personal,
}

/// <summary>
/// A Wi-Fi network saved by an administrator. Devices started from a Windows RE boot image join it
/// automatically when Ethernet has no IP address. The password lives in Key Vault, never in this record.
/// </summary>
public sealed class WifiNetwork
{
    /// <summary>Maximum number of saved networks.</summary>
    public const int MaxNetworks = 50;

    /// <summary>Identifier for this network.</summary>
    public required Guid NetworkId { get; init; }

    /// <summary>Network name (SSID), 1 to 32 bytes of UTF-8.</summary>
    public required string Ssid { get; init; }

    /// <summary>True when the network does not broadcast its name.</summary>
    public bool Hidden { get; init; }

    /// <summary>Security type.</summary>
    public WifiSecurityType Security { get; init; }

    /// <summary>True when every location receives the network, including locations added later.</summary>
    public bool AllLocations { get; init; }

    /// <summary>Locations that receive the network when <see cref="AllLocations"/> is false.</summary>
    public IReadOnlyList<Guid> LocationIds { get; init; } = [];

    /// <summary>Position in the order devices try networks, starting at 1.</summary>
    public int Order { get; init; }

    /// <summary>Disabled networks stay saved but are not sent to devices.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When the password was last set.</summary>
    public DateTimeOffset PasswordChangedAt { get; init; }

    /// <summary>Who last set the password.</summary>
    public string? PasswordChangedBy { get; init; }

    /// <summary>When the network was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>True when devices at <paramref name="locationId"/> receive this network.</summary>
    public bool AppliesTo(Guid? locationId) =>
        Enabled && (AllLocations || (locationId is { } id && LocationIds.Contains(id)));

    /// <summary>Returns a validation message for an SSID, or null when it can be used.</summary>
    public static string? ValidateSsid(string? ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return "Network name is required.";
        }
        var bytes = Encoding.UTF8.GetByteCount(ssid);
        return bytes > 32 ? "Network name can be at most 32 bytes." : null;
    }

    /// <summary>Returns a validation message for a WPA pre-shared key, or null when it can be used.</summary>
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Password is required.";
        }

        // 64 characters is the raw hex key form; passphrases are 8 to 63 printable ASCII characters.
        if (password.Length == 64)
        {
            return password.All(Uri.IsHexDigit) ? null : "A 64-character password must be a hex key.";
        }
        if (password.Length is < 8 or > 63)
        {
            return "Use 8 to 63 characters.";
        }
        return password.All(c => c is >= ' ' and <= '~') ? null : "Use printable ASCII characters only.";
    }
}

/// <summary>A network as delivered to a device or written to USB media, including its password.</summary>
/// <param name="Ssid">Network name.</param>
/// <param name="Hidden">True when the network does not broadcast its name.</param>
/// <param name="Security">Security type.</param>
/// <param name="Password">Pre-shared key.</param>
public sealed record ManagedWifiNetwork(string Ssid, bool Hidden, WifiSecurityType Security, string Password);

/// <summary>The networks for one location, in the order devices try them.</summary>
public sealed class ManagedWifiNetworkSet
{
    /// <summary>Location the set was built for; null when the media has no location.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>When the set was built.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Networks in the order devices try them.</summary>
    public IReadOnlyList<ManagedWifiNetwork> Networks { get; init; } = [];
}

/// <summary>
/// Device request for its location's managed Wi-Fi networks, signed with the boot media private key
/// over <see cref="DevicePayloadSignature.BuildWifiChallenge"/>.
/// </summary>
public sealed class ManagedWifiRequest
{
    /// <summary>Device serial number.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Location from the USB preparation manifest; null when the media has none.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Proof that the caller holds the boot media private key.</summary>
    public DeviceProofOfPossession? ProofOfPossession { get; init; }
}

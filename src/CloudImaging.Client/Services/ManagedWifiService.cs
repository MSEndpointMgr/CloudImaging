using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Services;

/// <summary>How joining the managed Wi-Fi networks at startup ended.</summary>
public enum ManagedWifiOutcome
{
    /// <summary>Connected and got an IP address.</summary>
    Connected,

    /// <summary>The USB media carries no networks for this location.</summary>
    NoNetworksSaved,

    /// <summary>None of the saved networks is in range.</summary>
    NoneInRange,

    /// <summary>A saved network is in range but did not accept the password.</summary>
    PasswordRejected,

    /// <summary>A network accepted the device but gave it no IP address.</summary>
    NoIpAddress,

    /// <summary>No wireless adapter is visible, usually because its driver is missing.</summary>
    NoAdapter,
}

/// <summary>Outcome of <see cref="ManagedWifiService.JoinAsync"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Ssid">The network joined, or the one that rejected the password.</param>
public sealed record ManagedWifiJoinResult(ManagedWifiOutcome Outcome, string? Ssid = null);

/// <summary>
/// The Wi-Fi networks an administrator saved in the portal, as this device receives them. Media
/// Builder writes them to the USB media encrypted to the boot media certificate, so a device
/// without Ethernet can get online; once online, the Client fetches a fresh copy from the Device
/// Gateway and replaces the file, so a changed password arrives by itself. Passwords live only in
/// memory and are never logged.
/// </summary>
public sealed partial class ManagedWifiService
{
    private static readonly TimeSpan AddressWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AddressPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly X509Certificate2? _certificate;
    private readonly WirelessConnectionService _wifi;
    private readonly Func<string?> _findBootDrive;
    private readonly Func<string?> _findAdapter;
    private readonly TimeSpan _addressWait;
    private readonly ILogger _logger;
    private ManagedWifiNetworkSet? _networks;

    /// <param name="certificate">Boot media certificate with its private key; null means no managed networks can be read.</param>
    /// <param name="wifi">Wireless service used to scan and connect.</param>
    /// <param name="logger">Defaults to a no-op logger.</param>
    /// <param name="findBootDrive">Test seam; defaults to the BOOT volume this Client runs from.</param>
    /// <param name="findAdapter">Test seam; defaults to the first adapter with a routable IPv4 address.</param>
    /// <param name="addressWait">Test seam; how long to wait for an IP address after connecting.</param>
    public ManagedWifiService(
        X509Certificate2? certificate,
        WirelessConnectionService wifi,
        ILogger<ManagedWifiService>? logger = null,
        Func<string?>? findBootDrive = null,
        Func<string?>? findAdapter = null,
        TimeSpan? addressWait = null)
    {
        _certificate = certificate;
        _wifi = wifi;
        _logger = logger ?? NullLogger<ManagedWifiService>.Instance;
        _findBootDrive = findBootDrive ?? BootImageSelfUpdateService.FindBootVolumeDriveLetter;
        _findAdapter = findAdapter ?? NetworkReadiness.FindRoutableAdapter;
        _addressWait = addressWait ?? AddressWait;
    }

    /// <summary>The networks this device receives, in the order they are tried; empty until loaded.</summary>
    public IReadOnlyList<ManagedWifiNetwork> Networks => _networks?.Networks ?? [];

    /// <summary>The managed network with this SSID, or null.</summary>
    public ManagedWifiNetwork? Find(string ssid) => Networks.FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal));

    /// <summary>Reads and decrypts the networks on the USB media. Never throws; an unreadable file means no networks.</summary>
    public IReadOnlyList<ManagedWifiNetwork> LoadFromMedia()
    {
        try
        {
            var path = MediaFilePath();
            if (path is null || !File.Exists(path) || _certificate is null)
                return Networks;

            _networks = ManagedWifiEnvelope.Decrypt(File.ReadAllBytes(path), _certificate);
            LogLoaded(_logger, _networks.Networks.Count);
        }
        catch (Exception ex)
        {
            LogUnreadable(_logger, ex.Message);
        }
        return Networks;
    }

    /// <summary>
    /// Tries each saved network in order until one gives an IP address. Networks that do not
    /// broadcast their name are always tried; visible ones only when the scan sees them.
    /// </summary>
    /// <param name="progress">Receives "Joining Wi-Fi network X" style status for the startup screen.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ManagedWifiJoinResult> JoinAsync(Action<string> progress, CancellationToken ct = default)
    {
        if (Networks.Count == 0)
            return new ManagedWifiJoinResult(ManagedWifiOutcome.NoNetworksSaved);

        var scan = await _wifi.ScanAsync(ct);
        if (scan.Status == WifiScanStatus.NoAdapter)
            return new ManagedWifiJoinResult(ManagedWifiOutcome.NoAdapter);
        var visible = scan.Networks.Select(n => n.Ssid).ToHashSet(StringComparer.Ordinal);

        string? rejected = null;
        string? noAddress = null;
        foreach (var network in Networks)
        {
            if (!network.Hidden && !visible.Contains(network.Ssid))
                continue;

            progress($"No Ethernet. Joining Wi-Fi network {network.Ssid}\u2026");
            var result = await _wifi.ConnectAsync(network.Ssid, network.Password, network.Security, network.Hidden, ct);
            if (result == WifiConnectResult.NoAdapter)
                return new ManagedWifiJoinResult(ManagedWifiOutcome.NoAdapter);
            if (result != WifiConnectResult.Success)
            {
                // A hidden network that does not connect is most likely simply not here.
                if (!network.Hidden)
                    rejected ??= network.Ssid;
                LogJoinFailed(_logger, network.Ssid, result);
                continue;
            }

            progress($"Connected to {network.Ssid}. Getting an IP address\u2026");
            if (await WaitForAddressAsync(ct))
            {
                LogJoined(_logger, network.Ssid);
                return new ManagedWifiJoinResult(ManagedWifiOutcome.Connected, network.Ssid);
            }
            noAddress ??= network.Ssid;
        }

        return rejected is not null ? new ManagedWifiJoinResult(ManagedWifiOutcome.PasswordRejected, rejected)
            : noAddress is not null ? new ManagedWifiJoinResult(ManagedWifiOutcome.NoIpAddress, noAddress)
            : new ManagedWifiJoinResult(ManagedWifiOutcome.NoneInRange);
    }

    /// <summary>
    /// Replaces the networks with the gateway's current list and writes the encrypted copy to the
    /// USB media, so the next boot has it even without Ethernet. The bytes are decrypted first, so
    /// a damaged response never overwrites a good file. Never throws.
    /// </summary>
    /// <param name="fetchEnvelope">Fetches the encrypted list from the Device Gateway; null on failure.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RefreshAsync(Func<CancellationToken, Task<byte[]?>> fetchEnvelope, CancellationToken ct = default)
    {
        try
        {
            if (_certificate is null)
                return false;
            var envelope = await fetchEnvelope(ct);
            if (envelope is null)
                return false;

            var fresh = ManagedWifiEnvelope.Decrypt(envelope, _certificate);
            _networks = fresh;

            var path = MediaFilePath();
            if (path is not null)
            {
                // Write beside the target first, so losing power mid-write never leaves a half file.
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, envelope, ct);
                File.Move(temp, path, overwrite: true);
            }
            LogRefreshed(_logger, fresh.Networks.Count, path is not null);
            return true;
        }
        catch (Exception ex)
        {
            LogRefreshFailed(_logger, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Fetches this location's current networks from the Device Gateway and stores them (see
    /// <see cref="RefreshAsync"/>). The serial number and location come from the device and its USB media.
    /// </summary>
    public Task<bool> RefreshFromGatewayAsync(DeviceGatewayApiClient gateway, CancellationToken ct = default) =>
        RefreshAsync(token => gateway.GetManagedWifiNetworksAsync(ReadSerialNumber(), ReadManifestLocation(), token), ct);

    private Guid? ReadManifestLocation()
    {
        try
        {
            var drive = _findBootDrive();
            if (string.IsNullOrWhiteSpace(drive))
                return null;
            var path = Path.Combine(drive.TrimEnd('\\') + "\\", UsbPreparationManifest.FileName);
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<UsbPreparationManifest>(File.ReadAllText(path))?.LocationId
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadSerialNumber()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["SerialNumber"]?.ToString() is { Length: > 0 } serial)
                        return serial.Trim();
                }
            }
        }
        catch
        {
            // The gateway only uses the serial for its log; the signature still binds the request.
        }
        return "UNKNOWN";
    }

    private async Task<bool> WaitForAddressAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _addressWait;
        while (true)
        {
            try
            {
                if (_findAdapter() is not null)
                    return true;
            }
            catch (NetworkInformationException)
            {
            }
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(AddressPollInterval, ct);
        }
    }

    private string? MediaFilePath()
    {
        var drive = _findBootDrive();
        return string.IsNullOrWhiteSpace(drive) ? null : Path.Combine(drive.TrimEnd('\\') + "\\", ManagedWifiEnvelope.FileName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} managed Wi-Fi networks from the USB media.")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The managed Wi-Fi networks on the USB media could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not join managed Wi-Fi network {Ssid}: {Result}.")]
    private static partial void LogJoinFailed(ILogger logger, string ssid, WifiConnectResult result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Joined managed Wi-Fi network {Ssid}.")]
    private static partial void LogJoined(ILogger logger, string ssid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed {Count} managed Wi-Fi networks from the Device Gateway (USB copy written: {Written}).")]
    private static partial void LogRefreshed(ILogger logger, int count, bool written);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh the managed Wi-Fi networks: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason);
}

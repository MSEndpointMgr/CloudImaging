using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Services;

/// <summary>How a scanned network authenticates, bucketed for the "which networks can this feature connect to" decision.</summary>
public enum WifiAuthKind
{
    /// <summary>Authentication could not be determined from the scan output.</summary>
    Unknown,
    /// <summary>No authentication (open hotspot-style network).</summary>
    Open,
    /// <summary>WPA2-Personal, WPA3-Personal, or WPA-Personal — simple pre-shared-key networks.</summary>
    PersonalPsk,
    /// <summary>802.1X / Enterprise authentication — not supported by this simple netsh-based flow.</summary>
    Enterprise,
}

/// <summary>
/// A network discovered by <see cref="WirelessConnectionService.ScanAsync"/>.
/// <see cref="IsSupported"/> is true only for <see cref="WifiAuthKind.Open"/> and
/// <see cref="WifiAuthKind.PersonalPsk"/> — Enterprise/802.1X networks are shown (not hidden)
/// but cannot be connected to via this simple PSK-only flow.
/// </summary>
public sealed record VisibleWifiNetwork(string Ssid, int SignalPercent, WifiAuthKind AuthKind, bool IsSupported, bool IsManaged = false, bool IsHidden = false)
{
    /// <summary>Second line under the name: hidden managed networks say so, since they never appear in a scan.</summary>
    public string Subtitle => IsHidden ? "Hidden network" : string.Empty;

    /// <summary>True when <see cref="Subtitle"/> has text.</summary>
    public bool HasSubtitle => IsHidden;

    /// <summary>Empty when supported; a short explanatory suffix otherwise (bound directly in the UI, no converter needed).</summary>
    public string NotSupportedLabel => IsSupported ? string.Empty : " (Not supported: Enterprise/802.1X)";

    /// <summary>True for any network that requires a passphrase/certificate (i.e. not Open) — drives the lock glyph shown next to the network name in the list.</summary>
    public bool IsSecured => AuthKind != WifiAuthKind.Open;

    /// <summary>
    /// Tiered Wi-Fi bar-count glyph matching <see cref="SignalPercent"/> (bound directly in the
    /// UI, no converter needed — see the wpftmp source-generator note in
    /// wpf-temp-project-workaround.md for why a new local IValueConverter is avoided here).
    /// Mirrors how Windows' own network flyout renders signal strength as bars, not a percentage.
    /// </summary>
    public Wpf.Ui.Controls.SymbolRegular SignalSymbol => SignalPercent switch
    {
        >= 80 => Wpf.Ui.Controls.SymbolRegular.Wifi424,
        >= 55 => Wpf.Ui.Controls.SymbolRegular.Wifi324,
        >= 30 => Wpf.Ui.Controls.SymbolRegular.Wifi224,
        _ => Wpf.Ui.Controls.SymbolRegular.Wifi124,
    };
}

/// <summary>Outcome of a <see cref="WirelessConnectionService"/> connect attempt.</summary>
public enum WifiConnectResult
{
    /// <summary>Connected successfully.</summary>
    Success,

    /// <summary>The connection attempt failed (e.g. wrong passphrase).</summary>
    Failed,

    /// <summary>Connection could not be confirmed within <see cref="WirelessConnectionService.ConnectPollTimeout"/>.</summary>
    Timeout,

    /// <summary>No wireless adapter is present.</summary>
    NoAdapter,
}

/// <summary>Why a Wi-Fi scan did or did not return networks.</summary>
public enum WifiScanStatus
{
    /// <summary>The scan ran; the list may still be empty while the adapter is scanning.</summary>
    Ok,

    /// <summary>This Windows installation has no WLAN support (netsh has no wlan context).</summary>
    NotSupported,

    /// <summary>The WLAN AutoConfig service is stopped and could not be started.</summary>
    ServiceNotRunning,

    /// <summary>No wireless adapter is visible, usually because its driver is missing.</summary>
    NoAdapter,

    /// <summary>Windows withheld the scan results until location access is granted (desktop Windows only).</summary>
    LocationPermissionRequired,

    /// <summary>netsh failed for another reason.</summary>
    Failed,
}

/// <summary>Outcome of <see cref="WirelessConnectionService.ScanAsync"/>.</summary>
/// <param name="Status">Why the list is what it is.</param>
/// <param name="Networks">Networks found; empty unless <paramref name="Status"/> is <see cref="WifiScanStatus.Ok"/>.</param>
/// <param name="Detail">netsh's own first line of output when <paramref name="Status"/> is <see cref="WifiScanStatus.Failed"/>.</param>
public sealed record WifiScanResult(WifiScanStatus Status, IReadOnlyList<VisibleWifiNetwork> Networks, string? Detail = null);

/// <summary>
/// Live, runtime-only Wi-Fi scan/connect for the WinPE Client's "Connect to Wi-Fi" support tool.
/// Uses the built-in <c>netsh wlan</c> CLI rather than a managed WLAN API, since none is referenced
/// by this project. <c>netsh wlan</c> needs the WLAN AutoConfig service and its netsh helper; the
/// ADK's WinPE supports wired networking only and ships neither, so on such a boot image
/// <see cref="ScanAsync"/> reports <see cref="WifiScanStatus.NotSupported"/>.
///
/// No credential persistence anywhere: the WLAN profile XML (which embeds the PSK in plain text,
/// as required by the netsh profile schema) is written to a temp file only for the duration of
/// the <c>netsh wlan add profile</c> call and deleted immediately afterward — never written to
/// the WIM, USB cache, or Table Storage.
///
/// Output parsing targets English (en-us) <c>netsh</c> locale output only, consistent with this
/// project's WinPE build already being en-us-only.
/// </summary>
public sealed partial class WirelessConnectionService
{
    private static readonly TimeSpan ConnectPollTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectPollInterval = TimeSpan.FromSeconds(1);

    // A freshly started WLAN service needs a moment to enumerate the adapter and finish a first scan.
    private static readonly TimeSpan ServiceStartSettleTime = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan WlanServiceStartTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<WirelessConnectionService> _logger;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _runNetsh;
    private readonly Func<CancellationToken, Task<bool>> _startWlanService;
    private readonly TimeSpan _serviceStartSettleTime;

    /// <param name="logger">Defaults to a no-op logger when not supplied.</param>
    /// <param name="runNetshOverride">Test seam. Defaults to a real <c>netsh.exe</c> invocation.</param>
    /// <param name="startWlanServiceOverride">Test seam. Defaults to starting <c>wlansvc</c> through the Service Control Manager.</param>
    /// <param name="serviceStartSettleTime">Test seam for the wait after starting the WLAN service.</param>
    public WirelessConnectionService(
        ILogger<WirelessConnectionService>? logger = null,
        Func<string, CancellationToken, Task<(int, string, string)>>? runNetshOverride = null,
        Func<CancellationToken, Task<bool>>? startWlanServiceOverride = null,
        TimeSpan? serviceStartSettleTime = null)
    {
        _logger = logger ?? NullLogger<WirelessConnectionService>.Instance;
        _runNetsh = runNetshOverride ?? RunNetshAsync;
        _startWlanService = startWlanServiceOverride ?? StartWlanServiceAsync;
        _serviceStartSettleTime = serviceStartSettleTime ?? ServiceStartSettleTime;
    }

    /// <summary>
    /// Scans for visible Wi-Fi networks via <c>netsh wlan show networks mode=bssid</c>. When nothing
    /// comes back, works out why (no WLAN support, stopped service, no adapter) so the window can say
    /// so instead of showing an empty list. Starts a stopped WLAN service once. Never throws.
    /// </summary>
    public async Task<WifiScanResult> ScanAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ScanOnceAsync(ct);
            if (result.Status != WifiScanStatus.ServiceNotRunning)
            {
                return result;
            }

            LogStartingWlanService(_logger);
            if (!await _startWlanService(ct))
            {
                return result;
            }

            await Task.Delay(_serviceStartSettleTime, ct);
            return await ScanOnceAsync(ct);
        }
        catch (Exception ex)
        {
            LogScanFailed(_logger, -1, ex.Message);
            return new WifiScanResult(WifiScanStatus.Failed, [], ex.Message);
        }
    }

    private async Task<WifiScanResult> ScanOnceAsync(CancellationToken ct)
    {
        var (exitCode, stdOut, stdErr) = await _runNetsh("wlan show networks mode=bssid", ct);
        var output = stdOut + stdErr;
        IReadOnlyList<VisibleWifiNetwork> networks = exitCode == 0 ? ParseNetworks(stdOut) : [];
        if (networks.Count > 0)
        {
            return new WifiScanResult(WifiScanStatus.Ok, networks);
        }

        var status = ClassifyNetshOutput(output);
        if (status == WifiScanStatus.Ok && exitCode == 0)
        {
            // No networks listed: confirm an adapter exists before calling an empty list a result.
            var (ifExitCode, ifStdOut, ifStdErr) = await _runNetsh("wlan show interfaces", ct);
            var interfaceStatus = ClassifyNetshOutput(ifStdOut + ifStdErr);
            status = interfaceStatus != WifiScanStatus.Ok ? interfaceStatus
                : ifExitCode == 0 && ParseInterfaceName(ifStdOut) is null ? WifiScanStatus.NoAdapter
                : WifiScanStatus.Ok;
        }
        else if (status == WifiScanStatus.Ok)
        {
            status = WifiScanStatus.Failed;
        }

        if (status != WifiScanStatus.Ok)
        {
            LogScanFailed(_logger, exitCode, output.Trim());
        }

        return new WifiScanResult(status, [], status == WifiScanStatus.Failed ? FirstLine(output) : null);
    }

    /// <summary>Maps netsh's English error text to a scan status; <see cref="WifiScanStatus.Ok"/> when none matches.</summary>
    internal static WifiScanStatus ClassifyNetshOutput(string output)
    {
        if (output.Contains("command was not found", StringComparison.OrdinalIgnoreCase))
        {
            return WifiScanStatus.NotSupported;
        }

        if (output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase) && output.Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            return WifiScanStatus.ServiceNotRunning;
        }

        if (output.Contains("no wireless interface", StringComparison.OrdinalIgnoreCase))
        {
            return WifiScanStatus.NoAdapter;
        }

        if (output.Contains("location permission", StringComparison.OrdinalIgnoreCase))
        {
            return WifiScanStatus.LocationPermissionRequired;
        }

        return WifiScanStatus.Ok;
    }

    private static string? FirstLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>
    /// Connects to <paramref name="ssid"/> using <paramref name="password"/> (null/empty for open
    /// networks). Builds a temporary WLAN profile XML, adds it via <c>netsh wlan add profile</c>,
    /// issues <c>netsh wlan connect</c>, then polls <c>netsh wlan show interfaces</c> for up to
    /// <see cref="ConnectPollTimeout"/> for <c>State : connected</c>. netsh gives no reliable
    /// "wrong password" signal, so a failed/timed-out connect necessarily surfaces a generic
    /// reason to the caller.
    /// </summary>
    public async Task<WifiConnectResult> ConnectAsync(string ssid, string? password, CancellationToken ct = default) =>
        await ConnectAsync(ssid, password, security: null, hidden: false, ct);

    /// <summary>
    /// Connects with an explicit security type and hidden flag, as saved for a managed network:
    /// WPA3 needs the SAE profile keyword, and a hidden network must be joined without a scan result.
    /// </summary>
    public async Task<WifiConnectResult> ConnectAsync(string ssid, string? password, WifiSecurityType? security, bool hidden, CancellationToken ct = default)
    {
        var (ifExitCode, ifStdOut, _) = await _runNetsh("wlan show interfaces", ct);
        var interfaceName = ifExitCode == 0 ? ParseInterfaceName(ifStdOut) : null;
        if (interfaceName is null)
        {
            LogNoAdapter(_logger);
            return WifiConnectResult.NoAdapter;
        }

        var profileXml = BuildProfileXml(ssid, password, security, hidden);
        var tempProfilePath = Path.Combine(Path.GetTempPath(), $"ci-wifi-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempProfilePath, profileXml, ct);

            var (addExitCode, addStdOut, addStdErr) = await _runNetsh(
                $"wlan add profile filename=\"{tempProfilePath}\" interface=\"{interfaceName}\"", ct);
            if (addExitCode != 0)
            {
                LogConnectFailed(_logger, addStdOut + addStdErr);
                return WifiConnectResult.Failed;
            }

            var (connExitCode, connStdOut, connStdErr) = await _runNetsh(
                $"wlan connect name=\"{ssid}\" ssid=\"{ssid}\" interface=\"{interfaceName}\"", ct);
            if (connExitCode != 0)
            {
                LogConnectFailed(_logger, connStdOut + connStdErr);
                return WifiConnectResult.Failed;
            }

            var deadline = DateTime.UtcNow + ConnectPollTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(ConnectPollInterval, ct);
                var (stateExitCode, stateStdOut, _) = await _runNetsh("wlan show interfaces", ct);
                if (stateExitCode == 0 && IsConnected(stateStdOut))
                {
                    LogConnected(_logger, ssid);
                    return WifiConnectResult.Success;
                }
            }

            return WifiConnectResult.Timeout;
        }
        finally
        {
            // Security hygiene: never leave the PSK-bearing profile XML behind on disk, even on
            // a failed/thrown attempt.
            try { File.Delete(tempProfilePath); } catch { /* best effort */ }
        }
    }

    /// <summary>Parses <c>netsh wlan show networks mode=bssid</c> output into a flat network list (max signal seen per SSID).</summary>
    public static IReadOnlyList<VisibleWifiNetwork> ParseNetworks(string output)
    {
        var networks = new List<VisibleWifiNetwork>();
        string? currentSsid = null;
        var currentAuth = WifiAuthKind.Unknown;
        var currentSignal = 0;

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentSsid))
            {
                networks.Add(new VisibleWifiNetwork(
                    currentSsid!,
                    currentSignal,
                    currentAuth,
                    IsSupported: currentAuth is WifiAuthKind.Open or WifiAuthKind.PersonalPsk));
            }
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();

            var ssidMatch = SsidLineRegex().Match(line);
            if (ssidMatch.Success)
            {
                FlushCurrent();
                currentSsid = ssidMatch.Groups[1].Value.Trim();
                currentAuth = WifiAuthKind.Unknown;
                currentSignal = 0;
                continue;
            }

            var authMatch = AuthLineRegex().Match(line);
            if (authMatch.Success)
            {
                currentAuth = ClassifyAuth(authMatch.Groups[1].Value.Trim());
                continue;
            }

            var signalMatch = SignalLineRegex().Match(line);
            if (signalMatch.Success)
            {
                var percent = int.Parse(signalMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (percent > currentSignal)
                {
                    currentSignal = percent;
                }
            }
        }

        FlushCurrent();
        return networks;
    }

    private static WifiAuthKind ClassifyAuth(string raw)
    {
        if (raw.Contains("Open", StringComparison.OrdinalIgnoreCase))
        {
            return WifiAuthKind.Open;
        }

        if (raw.Contains("WPA2-Personal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("WPA3-Personal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("WPA-Personal", StringComparison.OrdinalIgnoreCase))
        {
            return WifiAuthKind.PersonalPsk;
        }

        if (raw.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("802.1x", StringComparison.OrdinalIgnoreCase))
        {
            return WifiAuthKind.Enterprise;
        }

        return WifiAuthKind.Unknown;
    }

    /// <summary>Parses the first wireless interface's Name from <c>netsh wlan show interfaces</c> output. Null when no adapter is present.</summary>
    public static string? ParseInterfaceName(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = NameLineRegex().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    /// <summary>True when <c>netsh wlan show interfaces</c> output reports <c>State : connected</c> for any interface.</summary>
    public static bool IsConnected(string output) =>
        output.Split('\n').Select(l => l.Trim()).Any(l => StateConnectedRegex().IsMatch(l));

    /// <summary>The network named in <c>netsh wlan show interfaces</c> output when it reports a connection; otherwise null.</summary>
    public static string? ParseConnectedSsid(string output) =>
        !IsConnected(output) ? null
        : output.Split('\n').Select(l => ConnectedSsidLineRegex().Match(l.Trim())).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();

    /// <summary>The Wi-Fi network this device is connected to, or null. Never throws.</summary>
    public async Task<string?> GetConnectedSsidAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, stdOut, _) = await _runNetsh("wlan show interfaces", ct);
            return exitCode == 0 ? ParseConnectedSsid(stdOut) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Leaves the connected Wi-Fi network, so Ethernet carries all traffic. Returns the network left, or null when none was connected. Never throws.</summary>
    public async Task<string?> DisconnectAsync(CancellationToken ct = default)
    {
        var ssid = await GetConnectedSsidAsync(ct);
        if (ssid is null)
        {
            return null;
        }

        try
        {
            var (exitCode, stdOut, stdErr) = await _runNetsh("wlan disconnect", ct);
            if (exitCode != 0)
            {
                LogDisconnectFailed(_logger, (stdOut + stdErr).Trim());
                return null;
            }

            LogDisconnected(_logger, ssid);
            return ssid;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDisconnectFailed(_logger, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds a WLAN profile XML for <paramref name="ssid"/>. Without a <paramref name="security"/>
    /// (a network picked from the scan list) WPA3-Personal is requested with the same <c>WPA2PSK</c>
    /// keyword as WPA2-Personal, and Windows negotiates the strongest handshake. A managed network
    /// states its type: WPA3 uses <c>WPA3SAE</c>, and WPA2/WPA3 adds the v4 transition-mode flag.
    /// </summary>
    public static string BuildProfileXml(string ssid, string? password, WifiSecurityType? security = null, bool hidden = false)
    {
        var escapedSsid = SecurityElement.Escape(ssid);
        var nonBroadcast = hidden ? "<nonBroadcast>true</nonBroadcast>" : string.Empty;

        if (string.IsNullOrEmpty(password))
        {
            return $"""
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                    <name>{escapedSsid}</name>
                    <SSIDConfig><SSID><name>{escapedSsid}</name></SSID>{nonBroadcast}</SSIDConfig>
                    <connectionType>ESS</connectionType>
                    <connectionMode>manual</connectionMode>
                    <MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM>
                </WLANProfile>
                """;
        }

        var authentication = security is WifiSecurityType.Wpa3Personal or WifiSecurityType.Wpa2Wpa3Personal ? "WPA3SAE" : "WPA2PSK";
        var transition = security == WifiSecurityType.Wpa2Wpa3Personal
            ? "<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>"
            : string.Empty;
        var escapedPassword = SecurityElement.Escape(password);

        // A 64-character hex string is the raw key, which the profile schema types differently from a passphrase.
        var keyType = password.Length == 64 && password.All(char.IsAsciiHexDigit) ? "networkKey" : "passPhrase";
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                <name>{escapedSsid}</name>
                <SSIDConfig><SSID><name>{escapedSsid}</name></SSID>{nonBroadcast}</SSIDConfig>
                <connectionType>ESS</connectionType>
                <connectionMode>manual</connectionMode>
                <MSM><security><authEncryption><authentication>{authentication}</authentication><encryption>AES</encryption><useOneX>false</useOneX>{transition}</authEncryption><sharedKey><keyType>{keyType}</keyType><protected>false</protected><keyMaterial>{escapedPassword}</keyMaterial></sharedKey></security></MSM>
            </WLANProfile>
            """;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunNetshAsync(string arguments, CancellationToken ct) =>
        await RunProcessAsync("netsh.exe", arguments, ct);

    /// <summary>True when the WLAN AutoConfig service runs; false when it is not installed or would not start.</summary>
    private async Task<bool> StartWlanServiceAsync(CancellationToken ct)
    {
        var error = await Task.Run(() => WindowsServiceStarter.Start("wlansvc", WlanServiceStartTimeout), ct);
        if (error == 0)
        {
            return true;
        }

        LogWlanServiceStartFailed(_logger, error, new System.ComponentModel.Win32Exception(error).Message, WindowsServiceStarter.DescribeDependencies("wlansvc"));
        return false;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdOut, stdErr);
    }

    [GeneratedRegex(@"^SSID\s+\d+\s*:\s*(.*)$")]
    private static partial Regex SsidLineRegex();

    [GeneratedRegex(@"^Authentication\s*:\s*(.*)$")]
    private static partial Regex AuthLineRegex();

    [GeneratedRegex(@"^Signal\s*:\s*(\d+)%$")]
    private static partial Regex SignalLineRegex();

    [GeneratedRegex(@"^Name\s*:\s*(.+)$")]
    private static partial Regex NameLineRegex();

    [GeneratedRegex(@"^State\s*:\s*connected$", RegexOptions.IgnoreCase)]
    private static partial Regex StateConnectedRegex();

    [GeneratedRegex(@"^SSID\s*:\s*(.+)$")]
    private static partial Regex ConnectedSsidLineRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Left Wi-Fi network {Ssid}: Ethernet is in use.")]
    private static partial void LogDisconnected(ILogger logger, string ssid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not leave the Wi-Fi network: {Output}")]
    private static partial void LogDisconnectFailed(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi scan failed (exit {ExitCode}): {Output}")]
    private static partial void LogScanFailed(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLAN AutoConfig service is not running; starting it.")]
    private static partial void LogStartingWlanService(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the WLAN AutoConfig service (error {ErrorCode}): {Reason} Services it depends on: {Dependencies}")]
    private static partial void LogWlanServiceStartFailed(ILogger logger, int errorCode, string reason, string dependencies);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No wireless adapter found.")]
    private static partial void LogNoAdapter(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi connect failed: {Output}")]
    private static partial void LogConnectFailed(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi connected to {Ssid}.")]
    private static partial void LogConnected(ILogger logger, string ssid);
}

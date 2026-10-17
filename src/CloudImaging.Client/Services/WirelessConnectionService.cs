using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
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
public sealed record WifiNetwork(string Ssid, int SignalPercent, WifiAuthKind AuthKind, bool IsSupported)
{
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
        _     => Wpf.Ui.Controls.SymbolRegular.Wifi124,
    };
}

/// <summary>Outcome of <see cref="WirelessConnectionService.ConnectAsync"/>.</summary>
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
public sealed record WifiScanResult(WifiScanStatus Status, IReadOnlyList<WifiNetwork> Networks, string? Detail = null);

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

    private readonly ILogger<WirelessConnectionService> _logger;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _runNetsh;
    private readonly Func<CancellationToken, Task<bool>> _startWlanService;
    private readonly TimeSpan _serviceStartSettleTime;

    /// <param name="logger">Defaults to a no-op logger when not supplied.</param>
    /// <param name="runNetshOverride">Test seam. Defaults to a real <c>netsh.exe</c> invocation.</param>
    /// <param name="startWlanServiceOverride">Test seam. Defaults to <c>sc.exe start wlansvc</c>.</param>
    /// <param name="serviceStartSettleTime">Test seam for the wait after starting the WLAN service.</param>
    public WirelessConnectionService(
        ILogger<WirelessConnectionService>? logger = null,
        Func<string, CancellationToken, Task<(int, string, string)>>? runNetshOverride = null,
        Func<CancellationToken, Task<bool>>? startWlanServiceOverride = null,
        TimeSpan? serviceStartSettleTime = null)
    {
        _logger   = logger ?? NullLogger<WirelessConnectionService>.Instance;
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
                return result;

            LogStartingWlanService(_logger);
            if (!await _startWlanService(ct))
                return result;

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
        IReadOnlyList<WifiNetwork> networks = exitCode == 0 ? ParseNetworks(stdOut) : [];
        if (networks.Count > 0)
            return new WifiScanResult(WifiScanStatus.Ok, networks);

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
            LogScanFailed(_logger, exitCode, output.Trim());

        return new WifiScanResult(status, [], status == WifiScanStatus.Failed ? FirstLine(output) : null);
    }

    /// <summary>Maps netsh's English error text to a scan status; <see cref="WifiScanStatus.Ok"/> when none matches.</summary>
    internal static WifiScanStatus ClassifyNetshOutput(string output)
    {
        if (output.Contains("command was not found", StringComparison.OrdinalIgnoreCase))
            return WifiScanStatus.NotSupported;
        if (output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase) && output.Contains("not running", StringComparison.OrdinalIgnoreCase))
            return WifiScanStatus.ServiceNotRunning;
        if (output.Contains("no wireless interface", StringComparison.OrdinalIgnoreCase))
            return WifiScanStatus.NoAdapter;
        if (output.Contains("location permission", StringComparison.OrdinalIgnoreCase))
            return WifiScanStatus.LocationPermissionRequired;
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
    public async Task<WifiConnectResult> ConnectAsync(string ssid, string? password, CancellationToken ct = default)
    {
        var (ifExitCode, ifStdOut, _) = await _runNetsh("wlan show interfaces", ct);
        var interfaceName = ifExitCode == 0 ? ParseInterfaceName(ifStdOut) : null;
        if (interfaceName is null)
        {
            LogNoAdapter(_logger);
            return WifiConnectResult.NoAdapter;
        }

        var profileXml = BuildProfileXml(ssid, password);
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
    public static IReadOnlyList<WifiNetwork> ParseNetworks(string output)
    {
        var networks = new List<WifiNetwork>();
        string? currentSsid = null;
        var currentAuth = WifiAuthKind.Unknown;
        var currentSignal = 0;

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentSsid))
            {
                networks.Add(new WifiNetwork(
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
                currentSsid   = ssidMatch.Groups[1].Value.Trim();
                currentAuth   = WifiAuthKind.Unknown;
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
                    currentSignal = percent;
            }
        }

        FlushCurrent();
        return networks;
    }

    private static WifiAuthKind ClassifyAuth(string raw)
    {
        if (raw.Contains("Open", StringComparison.OrdinalIgnoreCase))
            return WifiAuthKind.Open;
        if (raw.Contains("WPA2-Personal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("WPA3-Personal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("WPA-Personal", StringComparison.OrdinalIgnoreCase))
            return WifiAuthKind.PersonalPsk;
        if (raw.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("802.1x", StringComparison.OrdinalIgnoreCase))
            return WifiAuthKind.Enterprise;
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
                return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>True when <c>netsh wlan show interfaces</c> output reports <c>State : connected</c> for any interface.</summary>
    public static bool IsConnected(string output) =>
        output.Split('\n').Select(l => l.Trim()).Any(l => StateConnectedRegex().IsMatch(l));

    /// <summary>
    /// Builds a WLAN profile XML for <paramref name="ssid"/>. WPA3-Personal networks are
    /// requested with the same <c>WPA2PSK</c> authentication keyword as WPA2-Personal (Windows
    /// negotiates the strongest mutually-supported handshake) — a deliberate simplification since
    /// both are treated as the same simple-PSK bucket by this feature.
    /// </summary>
    public static string BuildProfileXml(string ssid, string? password)
    {
        var escapedSsid = SecurityElement.Escape(ssid);

        if (string.IsNullOrEmpty(password))
        {
            return $"""
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                    <name>{escapedSsid}</name>
                    <SSIDConfig><SSID><name>{escapedSsid}</name></SSID></SSIDConfig>
                    <connectionType>ESS</connectionType>
                    <connectionMode>manual</connectionMode>
                    <MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM>
                </WLANProfile>
                """;
        }

        var escapedPassword = SecurityElement.Escape(password);
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                <name>{escapedSsid}</name>
                <SSIDConfig><SSID><name>{escapedSsid}</name></SSID></SSIDConfig>
                <connectionType>ESS</connectionType>
                <connectionMode>manual</connectionMode>
                <MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{escapedPassword}</keyMaterial></sharedKey></security></MSM>
            </WLANProfile>
            """;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunNetshAsync(string arguments, CancellationToken ct) =>
        await RunProcessAsync("netsh.exe", arguments, ct);

    /// <summary>True when <c>sc.exe</c> started the WLAN AutoConfig service; false when it is not installed or refused.</summary>
    private async Task<bool> StartWlanServiceAsync(CancellationToken ct)
    {
        var (exitCode, stdOut, stdErr) = await RunProcessAsync("sc.exe", "start wlansvc", ct);
        if (exitCode == 0)
            return true;

        LogWlanServiceStartFailed(_logger, exitCode, (stdOut + stdErr).Trim());
        return false;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi scan failed (exit {ExitCode}): {Output}")]
    private static partial void LogScanFailed(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLAN AutoConfig service is not running; starting it.")]
    private static partial void LogStartingWlanService(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the WLAN AutoConfig service (exit {ExitCode}): {Output}")]
    private static partial void LogWlanServiceStartFailed(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No wireless adapter found.")]
    private static partial void LogNoAdapter(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi connect failed: {Output}")]
    private static partial void LogConnectFailed(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi connected to {Ssid}.")]
    private static partial void LogConnected(ILogger logger, string ssid);
}

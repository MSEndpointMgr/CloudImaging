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
    public string NotSupportedLabel => IsSupported ? string.Empty : " — Not supported (Enterprise/802.1X)";
}

/// <summary>Outcome of <see cref="WirelessConnectionService.ConnectAsync"/>.</summary>
public enum WifiConnectResult
{
    Success,
    Failed,
    Timeout,
    NoAdapter,
}

/// <summary>
/// Live, runtime-only Wi-Fi scan/connect for the WinPE Client's "Connect to Wi-Fi" support tool.
/// Uses the built-in <c>netsh wlan</c> CLI (present in WinPE with the WinPE-WiFi-Package
/// optional component) rather than a managed WLAN API, since none is referenced by this project.
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

    private readonly ILogger<WirelessConnectionService> _logger;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _runNetsh;

    /// <param name="logger">Defaults to a no-op logger when not supplied.</param>
    /// <param name="runNetshOverride">Test seam. Defaults to a real <c>netsh.exe</c> invocation.</param>
    public WirelessConnectionService(
        ILogger<WirelessConnectionService>? logger = null,
        Func<string, CancellationToken, Task<(int, string, string)>>? runNetshOverride = null)
    {
        _logger   = logger ?? NullLogger<WirelessConnectionService>.Instance;
        _runNetsh = runNetshOverride ?? RunNetshAsync;
    }

    /// <summary>Scans for visible Wi-Fi networks via <c>netsh wlan show networks mode=bssid</c>. Returns an empty list (never throws) on any failure.</summary>
    public async Task<IReadOnlyList<WifiNetwork>> ScanAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, stdOut, stdErr) = await _runNetsh("wlan show networks mode=bssid", ct);
            if (exitCode != 0)
            {
                LogScanFailed(_logger, exitCode, stdOut + stdErr);
                return [];
            }

            return ParseNetworks(stdOut);
        }
        catch (Exception ex)
        {
            LogScanFailed(_logger, -1, ex.Message);
            return [];
        }
    }

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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunNetshAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "netsh.exe",
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No wireless adapter found.")]
    private static partial void LogNoAdapter(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wi-Fi connect failed: {Output}")]
    private static partial void LogConnectFailed(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi connected to {Ssid}.")]
    private static partial void LogConnected(ILogger logger, string ssid);
}

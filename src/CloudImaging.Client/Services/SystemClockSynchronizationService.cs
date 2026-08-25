using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CloudImaging.Client.Services;

/// <summary>
/// Best-effort synchronization of the local system clock against external NTP time servers,
/// run once per session before the boot-media proof-of-possession challenge is signed (FR-069).
///
/// WinPE devices frequently boot with a wildly incorrect clock — no NTP has ever run, the
/// CMOS/RTC battery may be dead or unset, or the device has simply never been powered on before
/// — which otherwise causes an entirely valid signature's timestamp to fall outside the Device
/// Gateway API's clock-skew tolerance and get rejected as "Proof-of-possession verification
/// failed (Expired)".
///
/// Actually resetting the system clock (<see cref="TrySetSystemClock"/>) only happens when the
/// process is running inside WinPE (detected via the well-known <c>MiniNT</c> registry key) —
/// this service must never silently change a developer's or technician's full Windows desktop
/// clock just because the Cloud Imaging Client happens to be running there (e.g. Dev Mode).
///
/// Never throws: every failure (DNS, network, timeout, insufficient privilege) is logged and
/// swallowed. This is a best-effort correction only — if it fails, the normal proof-of-possession
/// failure path still surfaces a clear, actionable error to the user.
/// </summary>
public sealed partial class SystemClockSynchronizationService
{
    private static readonly string[] DefaultNtpServers = ["time.windows.com", "time.google.com", "pool.ntp.org"];

    /// <summary>Per-server network round-trip budget — keep this fast; it runs on every session start.</summary>
    private static readonly TimeSpan PerServerTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Below this drift, don't bother resetting the clock (avoid needless churn/log noise).</summary>
    private static readonly TimeSpan MinimumCorrectionThreshold = TimeSpan.FromSeconds(2);

    private readonly ILogger<SystemClockSynchronizationService> _logger;
    private readonly IReadOnlyList<string> _ntpServers;

    /// <param name="logger">
    /// Optional so existing tests/tools can keep constructing this service without a logging
    /// pipeline wired up; defaults to a no-op logger.
    /// </param>
    /// <param name="ntpServers">Override for tests; defaults to well-known public NTP servers.</param>
    public SystemClockSynchronizationService(
        ILogger<SystemClockSynchronizationService>? logger = null,
        IReadOnlyList<string>? ntpServers = null)
    {
        _logger = logger ?? NullLogger<SystemClockSynchronizationService>.Instance;
        _ntpServers = ntpServers ?? DefaultNtpServers;
    }

    /// <summary>
    /// Queries each configured NTP server in turn until one responds, and — only when running
    /// under WinPE — resets the system clock to the measured network time. Returns true if the
    /// clock was already accurate or was successfully corrected; false if no server responded or
    /// the correction could not be applied. Never throws.
    /// </summary>
    public async Task<bool> TrySynchronizeAsync(CancellationToken ct = default)
    {
        var isWinPe = IsRunningInWinPe();

        foreach (var server in _ntpServers)
        {
            if (ct.IsCancellationRequested)
                return false;

            DateTime networkTimeUtc;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerServerTimeout);
                var result = await QueryNetworkTimeAsync(server, timeoutCts.Token).ConfigureAwait(false);
                if (result is null)
                    continue;
                networkTimeUtc = result.Value;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogNtpServerTimedOut(_logger, server);
                continue;
            }
            catch (Exception ex)
            {
                LogNtpServerFailed(_logger, server, ex);
                continue;
            }

            var drift = networkTimeUtc - DateTime.UtcNow;
            LogClockDriftMeasured(_logger, server, drift);

            if (drift.Duration() < MinimumCorrectionThreshold)
                return true;

            if (!isWinPe)
            {
                LogCorrectionSkippedNotWinPe(_logger, server, drift);
                return false;
            }

            if (TrySetSystemClock(networkTimeUtc))
            {
                LogClockCorrected(_logger, server, drift);
                return true;
            }

            LogClockCorrectionDenied(_logger, server);
            return false;
        }

        LogAllServersFailed(_logger);
        return false;
    }

    /// <summary>Sends a minimal SNTP client request (RFC 4330) and returns the corrected server UTC time.</summary>
    private static async Task<DateTime?> QueryNetworkTimeAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            return null;

        var request = new byte[48];
        request[0] = 0x1B; // LI=0 (no warning), VN=3 (NTPv3), Mode=3 (client)

        using var udp = new UdpClient();
        udp.Connect(new IPEndPoint(addresses[0], 123));

        var sentAt = DateTime.UtcNow;
        await udp.SendAsync(request, ct).ConfigureAwait(false);
        var response = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        var receivedAt = DateTime.UtcNow;

        var buffer = response.Buffer;
        if (buffer.Length < 48)
            return null;

        // Transmit Timestamp: bytes 40-43 = seconds since 1900-01-01 (big-endian),
        // bytes 44-47 = fractional seconds.
        uint seconds = ReadUInt32BigEndian(buffer, 40);
        uint fraction = ReadUInt32BigEndian(buffer, 44);
        var milliseconds = (seconds * 1000UL) + ((fraction * 1000UL) / 0x100000000UL);

        var ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var serverTransmitTime = ntpEpoch.AddMilliseconds(milliseconds);

        // Correct for network round-trip, assuming symmetric latency.
        var roundTrip = receivedAt - sentAt;
        return serverTransmitTime + TimeSpan.FromTicks(roundTrip.Ticks / 2);
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    /// <summary>
    /// WinPE always sets the <c>MiniNT</c> registry key — the standard, documented way to detect
    /// a WinPE environment at runtime. Absence means this is a full Windows install (e.g. a
    /// developer's desktop running Dev Mode), where this service must not touch the system clock.
    /// </summary>
    private static bool IsRunningInWinPe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetSystemClock(DateTime utc)
    {
        if (!TryEnableSystemTimePrivilege())
            return false;

        var systemTime = new SYSTEMTIME
        {
            Year = (ushort)utc.Year,
            Month = (ushort)utc.Month,
            DayOfWeek = (ushort)utc.DayOfWeek,
            Day = (ushort)utc.Day,
            Hour = (ushort)utc.Hour,
            Minute = (ushort)utc.Minute,
            Second = (ushort)utc.Second,
            Milliseconds = (ushort)utc.Millisecond,
        };

        return SetSystemTime(ref systemTime);
    }

    /// <summary>
    /// Changing the system clock requires <c>SeSystemtimePrivilege</c>, which is granted to the
    /// process token but disabled by default — it must be explicitly enabled before
    /// <see cref="SetSystemTime"/> will succeed.
    /// </summary>
    private static bool TryEnableSystemTimePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tokenHandle))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, SE_SYSTEMTIME_NAME, out var luid))
                return false;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            return AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Measured clock drift of {Drift} against NTP server {Server}.")]
    private static partial void LogClockDriftMeasured(ILogger logger, string server, TimeSpan drift);

    [LoggerMessage(Level = LogLevel.Information, Message = "System clock corrected by {Drift} using NTP server {Server}.")]
    private static partial void LogClockCorrected(ILogger logger, string server, TimeSpan drift);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Clock drift of {Drift} detected against {Server} but not running under WinPE — leaving system clock untouched.")]
    private static partial void LogCorrectionSkippedNotWinPe(ILogger logger, string server, TimeSpan drift);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not obtain SeSystemtimePrivilege to correct the system clock (server {Server}).")]
    private static partial void LogClockCorrectionDenied(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NTP server {Server} timed out.")]
    private static partial void LogNtpServerTimedOut(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NTP server {Server} query failed.")]
    private static partial void LogNtpServerFailed(ILogger logger, string server, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "All configured NTP servers failed — proceeding with the current (uncorrected) system clock.")]
    private static partial void LogAllServersFailed(ILogger logger);

    // ── Win32 interop for SetSystemTime + SeSystemtimePrivilege ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const string SE_SYSTEMTIME_NAME = "SeSystemtimePrivilege";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLengthInBytes,
        IntPtr previousState,
        IntPtr returnLengthInBytes);
}

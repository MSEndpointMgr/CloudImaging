using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CloudImaging.Client.Services;

/// <summary>
/// Reads firmware mode, Secure Boot state and TPM presence in WinPE using plain Win32 calls, so it
/// works on any hardware without extra WinPE optional components. Never throws: a value it cannot
/// read is reported as Unknown and the reason is logged.
/// </summary>
public sealed partial class DevicePostureDetector
{
    // EFI_GLOBAL_VARIABLE namespace defined by the UEFI specification.
    private const string EfiGlobalVariableGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
    private const int ErrorInvalidFunction = 1;
    private const int ErrorEnvVarNotFound = 203;

    private readonly ILogger<DevicePostureDetector> _logger;

    /// <summary>Builds the detector. Defaults to a no-op logger.</summary>
    public DevicePostureDetector(ILogger<DevicePostureDetector>? logger = null) =>
        _logger = logger ?? NullLogger<DevicePostureDetector>.Instance;

    /// <summary>Reads the device's firmware mode, Secure Boot state and TPM presence.</summary>
    public DeviceSecurityPosture Detect()
    {
        var firmwareMode = DetectFirmwareMode();
        var posture = new DeviceSecurityPosture
        {
            FirmwareMode = firmwareMode,
            SecureBoot = DetectSecureBoot(firmwareMode),
            Tpm = DetectTpm(),
        };
        LogPosture(_logger, posture.FirmwareMode, posture.SecureBoot, posture.Tpm);
        return posture;
    }

    /// <summary>Reads whether the device booted UEFI or legacy BIOS.</summary>
    public FirmwareMode DetectFirmwareMode()
    {
        try
        {
            if (GetFirmwareType(out var firmwareType) && MapFirmwareType(firmwareType) is var mode && mode != FirmwareMode.Unknown)
            {
                return mode;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            LogDetectionFailed(_logger, "GetFirmwareType", ex.Message);
        }

        // Documented WinPE fallback: HKLM\System\CurrentControlSet\Control\PEFirmwareType.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return MapFirmwareType(key?.GetValue("PEFirmwareType") is int value ? (uint)value : 0);
        }
        catch (Exception ex)
        {
            LogDetectionFailed(_logger, "PEFirmwareType", ex.Message);
            return FirmwareMode.Unknown;
        }
    }

    private SecureBootState DetectSecureBoot(FirmwareMode firmwareMode)
    {
        if (firmwareMode == FirmwareMode.LegacyBios)
        {
            return SecureBootState.Unsupported;
        }

        try
        {
            // Reading UEFI variables needs SeSystemEnvironmentPrivilege, held but disabled by default.
            if (!TryEnablePrivilege("SeSystemEnvironmentPrivilege"))
            {
                LogDetectionFailed(_logger, "SeSystemEnvironmentPrivilege", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            var buffer = new byte[1];
            var read = GetFirmwareEnvironmentVariableEx("SecureBoot", EfiGlobalVariableGuid, buffer, (uint)buffer.Length, out _);
            var error = read == 0 ? Marshal.GetLastWin32Error() : 0;
            var state = MapSecureBoot(read == 0 ? null : buffer[0], error);
            if (state == SecureBootState.Unknown)
            {
                LogDetectionFailed(_logger, "SecureBoot variable", new Win32Exception(error).Message);
            }

            return state;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            LogDetectionFailed(_logger, "GetFirmwareEnvironmentVariableEx", ex.Message);
            return SecureBootState.Unknown;
        }
    }

    private TpmPresence DetectTpm()
    {
        try
        {
            var signatures = EnumerateAcpiTables();
            if (signatures is null)
            {
                LogDetectionFailed(_logger, "ACPI tables", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            return MapTpm(signatures);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            LogDetectionFailed(_logger, "EnumSystemFirmwareTables", ex.Message);
            return TpmPresence.Unknown;
        }
    }

    // ── Pure mappings (unit-tested) ──────────────────────────────────────────

    /// <summary>Maps FIRMWARE_TYPE / PEFirmwareType: 1 = BIOS, 2 = UEFI.</summary>
    internal static FirmwareMode MapFirmwareType(uint firmwareType) => firmwareType switch
    {
        1 => FirmwareMode.LegacyBios,
        2 => FirmwareMode.Uefi,
        _ => FirmwareMode.Unknown,
    };

    /// <summary>
    /// Maps the SecureBoot variable. It is 1 only while Secure Boot is enforcing; setup mode (no
    /// platform key) reads 0. A missing variable means the firmware has no Secure Boot support.
    /// </summary>
    internal static SecureBootState MapSecureBoot(byte? value, int lastError) => value switch
    {
        1 => SecureBootState.Enabled,
        not null => SecureBootState.Disabled,
        null when lastError is ErrorEnvVarNotFound or ErrorInvalidFunction => SecureBootState.Unsupported,
        _ => SecureBootState.Unknown,
    };

    /// <summary>TPM 2.0 publishes an ACPI TPM2 table, TPM 1.2 a TCPA table. Null means the tables could not be listed.</summary>
    internal static TpmPresence MapTpm(IReadOnlyCollection<string>? acpiSignatures) => acpiSignatures switch
    {
        null => TpmPresence.Unknown,
        _ when acpiSignatures.Contains("TPM2") => TpmPresence.Tpm20,
        _ when acpiSignatures.Contains("TCPA") => TpmPresence.Tpm12,
        _ => TpmPresence.NotDetected,
    };

    /// <summary>Lists the signatures of the ACPI tables the firmware exposes, or null on failure.</summary>
    private static List<string>? EnumerateAcpiTables()
    {
        const uint acpiProvider = 0x41435049; // 'ACPI'
        var size = EnumSystemFirmwareTables(acpiProvider, null, 0);
        if (size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        if (EnumSystemFirmwareTables(acpiProvider, buffer, size) == 0)
        {
            return null;
        }

        // Each table ID is its 4-character signature in memory order.
        var signatures = new List<string>();
        for (var i = 0; i + 4 <= buffer.Length; i += 4)
        {
            signatures.Add(Encoding.ASCII.GetString(buffer, i, 4));
        }

        return signatures;
    }

    private static bool TryEnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device posture: firmware mode {FirmwareMode}, Secure Boot {SecureBoot}, TPM {Tpm}.")]
    private static partial void LogPosture(ILogger logger, FirmwareMode firmwareMode, SecureBootState secureBoot, TpmPresence tpm);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Source} for the pre-flight checks: {Reason}")]
    private static partial void LogDetectionFailed(ILogger logger, string source, string reason);

    // ── Win32 interop ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFirmwareEnvironmentVariableExW")]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, [Out] byte[] buffer, uint size, out uint attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint EnumSystemFirmwareTables(uint firmwareTableProviderSignature, [Out] byte[]? buffer, uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}

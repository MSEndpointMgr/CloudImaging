using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Firmware mode the device booted WinPE in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FirmwareMode
{
    Unknown,
    Uefi,
    LegacyBios,
}

/// <summary>
/// Secure Boot state read from the UEFI <c>SecureBoot</c> variable. A firmware in setup mode (no
/// platform key) reports the variable as 0 even when its menu says Enabled, so it reads as Disabled.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecureBootState
{
    Unknown,
    Enabled,
    Disabled,

    /// <summary>The firmware has no Secure Boot support, or the device booted in Legacy BIOS mode.</summary>
    Unsupported,
}

/// <summary>TPM the firmware exposes, read from its ACPI tables.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TpmPresence
{
    Unknown,
    NotDetected,
    Tpm12,
    Tpm20,
}

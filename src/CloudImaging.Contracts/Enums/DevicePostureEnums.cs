using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Firmware mode the device booted WinPE in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FirmwareMode
{
    /// <summary>Not yet determined.</summary>
    Unknown,

    /// <summary>The device booted in UEFI mode.</summary>
    Uefi,

    /// <summary>The device booted in Legacy BIOS (CSM) mode.</summary>
    LegacyBios,
}

/// <summary>
/// Secure Boot state read from the UEFI <c>SecureBoot</c> variable. A firmware in setup mode (no
/// platform key) reports the variable as 0 even when its menu says Enabled, so it reads as Disabled.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecureBootState
{
    /// <summary>Not yet determined.</summary>
    Unknown,

    /// <summary>Secure Boot is enabled.</summary>
    Enabled,

    /// <summary>Secure Boot is disabled, or the firmware is in setup mode.</summary>
    Disabled,

    /// <summary>The firmware has no Secure Boot support, or the device booted in Legacy BIOS mode.</summary>
    Unsupported,
}

/// <summary>TPM the firmware exposes, read from its ACPI tables.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TpmPresence
{
    /// <summary>Not yet determined.</summary>
    Unknown,

    /// <summary>No TPM was detected.</summary>
    NotDetected,

    /// <summary>A TPM 1.2 was detected.</summary>
    Tpm12,

    /// <summary>A TPM 2.0 was detected.</summary>
    Tpm20,
}

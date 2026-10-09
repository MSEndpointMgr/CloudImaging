using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Firmware security state the Client reads in WinPE and reports at registration. Self-reported
/// by the device, so it guards against misconfiguration; it is not hardware attestation.
/// </summary>
public sealed class DeviceSecurityPosture
{
    /// <summary>Whether the device booted in UEFI or Legacy BIOS mode.</summary>
    public FirmwareMode FirmwareMode { get; init; }

    /// <summary>Whether Secure Boot is enabled.</summary>
    public SecureBootState SecureBoot { get; init; }

    /// <summary>TPM version detected, if any.</summary>
    public TpmPresence Tpm { get; init; }
}

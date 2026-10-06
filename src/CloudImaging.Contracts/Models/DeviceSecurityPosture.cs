using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Firmware security state the Client reads in WinPE and reports at registration. Self-reported
/// by the device, so it guards against misconfiguration; it is not hardware attestation.
/// </summary>
public sealed class DeviceSecurityPosture
{
    public FirmwareMode FirmwareMode { get; init; }
    public SecureBootState SecureBoot { get; init; }
    public TpmPresence Tpm { get; init; }
}

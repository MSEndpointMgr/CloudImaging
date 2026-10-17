using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Display text for pre-flight checks shown on the device and written to its log. The portal keeps
/// the same wording in <c>lib/preflight.ts</c>; change both together.
/// </summary>
public static class PreFlightCheckText
{
    /// <summary>Display name for a check.</summary>
    public static string Name(PreFlightCheck check) => check switch
    {
        PreFlightCheck.AutopilotPresence => "Autopilot presence",
        PreFlightCheck.FirmwareMode => "Firmware mode",
        PreFlightCheck.SecureBoot => "Secure Boot",
        PreFlightCheck.TpmVersion => "TPM version",
        _ => check.ToString(),
    };

    /// <summary>Display text for what was observed for a check.</summary>
    public static string Value(PreFlightCheck check, string observed) => observed switch
    {
        PreFlightObserved.NotReported => "Not reported",
        PreFlightObserved.NotChecked => "Not checked",
        PreFlightObserved.Autopilot => "Present in Autopilot",
        PreFlightObserved.CorporateIdentifier => "Imported as Corporate Identifier",
        PreFlightObserved.NotFound => "Not present",
        nameof(FirmwareMode.Uefi) => "UEFI",
        nameof(FirmwareMode.LegacyBios) => "Legacy BIOS (CSM)",
        nameof(SecureBootState.Enabled) => "Enabled",
        nameof(SecureBootState.Disabled) or nameof(SecureBootState.Unsupported) => "Not enabled",
        nameof(TpmPresence.Tpm20) => "Version 2.0 or above",
        nameof(TpmPresence.Tpm12) => "Version 1.2",
        nameof(TpmPresence.NotDetected) => "Not detected",
        nameof(FirmwareMode.Unknown) => "Could not be detected",
        _ => observed,
    };

    /// <summary>One-line action for a failed check. Never refers to another check.</summary>
    public static string FixHint(PreFlightCheck check, string observed) => observed switch
    {
        PreFlightObserved.NotReported => "Update the boot media.",
        nameof(FirmwareMode.Unknown) => "See the device log for why it could not be read.",
        _ => check switch
        {
            PreFlightCheck.AutopilotPresence => "Register the device in Windows Autopilot, or import it as a Corporate Identifier.",
            PreFlightCheck.FirmwareMode => "Switch to UEFI in firmware settings.",
            PreFlightCheck.SecureBoot => "Enable Secure Boot in firmware settings.",
            PreFlightCheck.TpmVersion => "Enable the TPM in firmware settings.",
            _ => string.Empty,
        },
    };
}

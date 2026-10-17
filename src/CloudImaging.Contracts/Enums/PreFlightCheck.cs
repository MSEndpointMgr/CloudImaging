using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>The pre-flight requirements an administrator can switch on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightCheck
{
    /// <summary>The device must match an Autopilot (v1) or corporate identifier registration.</summary>
    AutopilotPresence,

    /// <summary>The device must have booted in UEFI mode.</summary>
    FirmwareMode,

    /// <summary>The device must have Secure Boot enabled.</summary>
    SecureBoot,

    /// <summary>The device must have a TPM 2.0.</summary>
    TpmVersion,
}

/// <summary>Result of one pre-flight check for one session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightCheckOutcome
{
    /// <summary>The device met the requirement.</summary>
    Passed,

    /// <summary>The device did not meet the requirement.</summary>
    Failed,

    /// <summary>The requirement was switched off when the session started; the value is still recorded.</summary>
    NotRequired,

    /// <summary>The check failed but an administrator override let the session through.</summary>
    Approved,
}

using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>The pre-flight requirements an administrator can switch on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightCheck
{
    AutopilotPresence,
    FirmwareMode,
    SecureBoot,
    TpmVersion,
}

/// <summary>Result of one pre-flight check for one session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightCheckOutcome
{
    Passed,
    Failed,

    /// <summary>The requirement was switched off when the session started; the value is still recorded.</summary>
    NotRequired,

    /// <summary>The check failed but an administrator override let the session through.</summary>
    Approved,
}

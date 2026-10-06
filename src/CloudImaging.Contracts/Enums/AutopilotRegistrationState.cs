using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Lifecycle of a Windows Autopilot (v1) hardware hash registration request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutopilotRegistrationState
{
    /// <summary>Submitted by a device and waiting for an approver.</summary>
    PendingApproval,

    /// <summary>Approved; the Graph import was submitted and is being processed by Intune.</summary>
    Importing,

    /// <summary>Intune reported the import as complete. Terminal.</summary>
    Imported,

    /// <summary>Intune rejected the import, or it did not finish in time. Can be retried.</summary>
    ImportFailed,

    /// <summary>An approver declined the request. Terminal.</summary>
    Rejected,

    /// <summary>Nobody decided before the configured expiry. Terminal.</summary>
    Expired,

    /// <summary>The serial number was already registered in Autopilot at submission time. Terminal.</summary>
    AlreadyRegistered,
}

/// <summary>How an Autopilot group tag definition produces its value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutopilotGroupTagKind
{
    /// <summary>The value is used verbatim.</summary>
    Static,

    /// <summary>The value is a template with tokens resolved from the device's Location.</summary>
    Template,
}

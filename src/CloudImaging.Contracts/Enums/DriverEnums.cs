using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Driver vendor. Lenovo is listed but not supported: never onboardable, sync switch disabled.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverVendor
{
    /// <summary>HP, matched by baseboard system ID.</summary>
    Hp,

    /// <summary>Dell, matched by system SKU.</summary>
    Dell,

    /// <summary>Lenovo, listed as not supported yet.</summary>
    Lenovo,
}

/// <summary>What happens to a driver component during a deployment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverAction
{
    /// <summary>Injected into the applied Windows volume in the boot environment.</summary>
    Offline,

    /// <summary>Injected offline, and the vendor package also runs during the specialize pass.</summary>
    OfflineAndSpecialize,

    /// <summary>The vendor package runs during the specialize pass only.</summary>
    Specialize,

    /// <summary>Not part of the driver set.</summary>
    Skip,
}

/// <summary>Where a component's action comes from, in precedence order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverActionSource
{
    /// <summary>Only one action is allowed for the component.</summary>
    Locked,

    /// <summary>A custom component replaces this vendor component.</summary>
    ReplacedByCustom,

    /// <summary>The component is a custom component.</summary>
    Custom,

    /// <summary>The component is held at an earlier version.</summary>
    RolledBack,

    /// <summary>An action chosen for this model.</summary>
    Override,

    /// <summary>A matching rule.</summary>
    Rule,

    /// <summary>The vendor default.</summary>
    Default,
}

/// <summary>What kind of component a device got.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverComponentOrigin
{
    /// <summary>The vendor's current version.</summary>
    Vendor,

    /// <summary>A custom component uploaded by an Operator.</summary>
    Custom,

    /// <summary>A vendor component held at an earlier version.</summary>
    RolledBack,
}

/// <summary>How long a component roll back lasts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverRollBackUntil
{
    /// <summary>Until a sync finds a version newer than the one current when the roll back started.</summary>
    NewerVersion,

    /// <summary>Until someone undoes the roll back.</summary>
    Undone,
}

/// <summary>Vendors a rule applies to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverRuleScope
{
    /// <summary>Every vendor.</summary>
    AllVendors,

    /// <summary>HP only.</summary>
    Hp,

    /// <summary>Dell only.</summary>
    Dell,
}

/// <summary>What a rule matches on. Specificity: ComponentId, then NamePattern, then Category.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverRuleMatch
{
    /// <summary>The vendor category, compared case-insensitively.</summary>
    Category,

    /// <summary>A name pattern where <c>*</c> matches any text.</summary>
    NamePattern,

    /// <summary>The vendor component ID.</summary>
    ComponentId,
}

/// <summary>State of a component on a model and release.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverComponentState
{
    /// <summary>Part of policy resolution.</summary>
    Active,

    /// <summary>New and held until an administrator approves it.</summary>
    WaitingForApproval,

    /// <summary>The vendor no longer lists it; kept at its last verified version.</summary>
    Withdrawn,
}

/// <summary>
/// Windows release an OS image installs and a model is onboarded for. Extended in code when
/// Microsoft ships a new release. Wire values keep the <c>Windows11_24H2</c> form.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowsRelease
{
    /// <summary>Windows 10, version 22H2.</summary>
    [JsonStringEnumMemberName("Windows10_22H2")]
    Windows10Version22H2,

    /// <summary>Windows 11, version 23H2.</summary>
    [JsonStringEnumMemberName("Windows11_23H2")]
    Windows11Version23H2,

    /// <summary>Windows 11, version 24H2.</summary>
    [JsonStringEnumMemberName("Windows11_24H2")]
    Windows11Version24H2,

    /// <summary>Windows 11, version 25H2.</summary>
    [JsonStringEnumMemberName("Windows11_25H2")]
    Windows11Version25H2,
}

/// <summary>When the driver sync runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSyncSchedule
{
    /// <summary>Every day at the chosen time.</summary>
    Daily,

    /// <summary>On selected weekdays, every 1 to 4 weeks.</summary>
    SelectedDays,

    /// <summary>Only when someone selects Sync now.</summary>
    Manual,
}

/// <summary>What started a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSyncTrigger
{
    /// <summary>The schedule: refreshes the model lists and checks every onboarded model.</summary>
    Scheduled,

    /// <summary>Sync now: the same work as the schedule.</summary>
    Manual,

    /// <summary>Check for updates on the Models tab: every onboarded model, components only.</summary>
    Check,

    /// <summary>Check for updates on one model's page.</summary>
    ModelCheck,
}

/// <summary>Result of a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSyncResult
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>Every item succeeded.</summary>
    Completed,

    /// <summary>Some items failed; the rest applied.</summary>
    PartlyFailed,

    /// <summary>Nothing applied.</summary>
    Failed,
}

/// <summary>Outcome of one item of a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSyncItemOutcome
{
    /// <summary>A newer version was found and applied.</summary>
    Updated,

    /// <summary>A new component entered the set.</summary>
    New,

    /// <summary>A new component waits for approval.</summary>
    NewWaitingForApproval,

    /// <summary>The vendor no longer lists the component.</summary>
    Withdrawn,

    /// <summary>The component was removed.</summary>
    Removed,

    /// <summary>The item failed; the reason says why.</summary>
    Failed,
}

/// <summary>Why no driver set applies to a session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSkipReason
{
    /// <summary>No onboarded model matches the device.</summary>
    NoMatchingModel,

    /// <summary>The OS image has no Windows release.</summary>
    NoWindowsRelease,

    /// <summary>The matched model is not onboarded for the image's release.</summary>
    ReleaseNotOnboarded,

    /// <summary>The model and release have no current driver set yet.</summary>
    NoDriverSet,

    /// <summary>The OS image's architecture does not match the model's.</summary>
    ArchitectureMismatch,

    /// <summary>No drivers was chosen when imaging started.</summary>
    NoneChosen,
}

/// <summary>A manual driver set choice made when imaging starts. Request only; absent means use the match.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverChoiceKind
{
    /// <summary>Use the chosen model's current set.</summary>
    Model,

    /// <summary>Install no drivers.</summary>
    None,
}

using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>A time zone offered for the sync schedule, the way Windows presents it.</summary>
public sealed record DriverTimeZone
{
    /// <summary>IANA name stored in the schedule.</summary>
    public required string IanaId { get; init; }

    /// <summary>Windows display name, for example "(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna".</summary>
    public required string DisplayName { get; init; }

    /// <summary>IANA names this entry covers, used to find the browser's entry.</summary>
    public IReadOnlyList<string> Covers { get; init; } = [];
}

/// <summary>Sync settings.</summary>
public sealed record DriverSyncSettings
{
    /// <summary>Schedule mode.</summary>
    public DriverSyncSchedule Schedule { get; init; } = DriverSyncSchedule.Daily;

    /// <summary>Selected weekdays as a bit mask, Monday = 1 up to Sunday = 64.</summary>
    public int Weekdays { get; init; }

    /// <summary>Selected days repeat every 1 to 4 weeks.</summary>
    public int RepeatWeeks { get; init; } = 1;

    /// <summary>Local time, <c>HH:mm</c>.</summary>
    public string LocalTime { get; init; } = "02:00";

    /// <summary>IANA time zone.</summary>
    public string TimeZone { get; init; } = "Etc/UTC";

    /// <summary>New components wait for approval.</summary>
    public bool WaitForApprovalOfNewComponents { get; init; }

    /// <summary>Next scheduled sync in UTC, null for manual only.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>When Sync now and Check for updates are available again, null when available now.</summary>
    public DateTimeOffset? SyncNowAvailableAt { get; init; }

    /// <summary>Start of the last scheduled sync, Sync now or Check for updates.</summary>
    public DateTimeOffset? LastRunStartedAt { get; init; }

    /// <summary>When the settings were last saved; null until the first save, when the portal applies the browser's time zone.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Time zones offered; returned by the get only.</summary>
    public IReadOnlyList<DriverTimeZone> TimeZones { get; init; } = [];
}

/// <summary>Request to change the sync settings.</summary>
public sealed record SetDriverSyncSettingsRequest
{
    /// <summary>Schedule mode.</summary>
    public DriverSyncSchedule Schedule { get; init; }

    /// <summary>Selected weekdays as a bit mask, Monday = 1.</summary>
    public int Weekdays { get; init; }

    /// <summary>Selected days repeat every 1 to 4 weeks.</summary>
    public int RepeatWeeks { get; init; } = 1;

    /// <summary>Local time, <c>HH:mm</c>.</summary>
    public string? LocalTime { get; init; }

    /// <summary>IANA time zone from the offered list.</summary>
    public string? TimeZone { get; init; }

    /// <summary>New components wait for approval.</summary>
    public bool WaitForApprovalOfNewComponents { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>Per-vendor result of a run.</summary>
public sealed record DriverSyncRunVendor
{
    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>True when the vendor's catalogs were read.</summary>
    public bool Processed { get; init; }

    /// <summary>Why the vendor failed.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Models added to the vendor's list by this run.</summary>
    public int NewModels { get; init; }
}

/// <summary>A run in the History card.</summary>
public sealed record DriverSyncRun
{
    /// <summary>Run id.</summary>
    public Guid RunId { get; init; }

    /// <summary>What started the run.</summary>
    public DriverSyncTrigger Trigger { get; init; }

    /// <summary>Model of a model check.</summary>
    public Guid? ModelId { get; init; }

    /// <summary>Model name of a model check.</summary>
    public string? ModelName { get; init; }

    /// <summary>Start.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>End.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Result.</summary>
    public DriverSyncResult Result { get; init; }

    /// <summary>Who started a manual run or check.</summary>
    public string? StartedBy { get; init; }

    /// <summary>Item counts per outcome.</summary>
    public IReadOnlyDictionary<DriverSyncItemOutcome, int> Counts { get; init; } = new Dictionary<DriverSyncItemOutcome, int>();

    /// <summary>Per-vendor results.</summary>
    public IReadOnlyList<DriverSyncRunVendor> Vendors { get; init; } = [];

    /// <summary>Vendors whose catalogs the run set out to read; one missing from <see cref="Vendors"/> was never read.</summary>
    public IReadOnlyList<DriverVendor> PlannedVendors { get; init; } = [];

    /// <summary>Onboarded models the run checks; grows as vendor catalogs are read.</summary>
    public int ModelsTotal { get; init; }

    /// <summary>Models whose every Windows release was checked.</summary>
    public int ModelsChecked { get; init; }

    /// <summary>Components downloaded and verified.</summary>
    public int DownloadedComponents { get; init; }

    /// <summary>Bytes of the downloaded components.</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>New driver sets that became current.</summary>
    public int NewSets { get; init; }
}

/// <summary>Done and total work of one stage of a running sync. The total grows while the sync finds work.</summary>
public sealed record DriverSyncStage
{
    /// <summary>Done.</summary>
    public int Done { get; init; }

    /// <summary>Total so far.</summary>
    public int Total { get; init; }
}

/// <summary>How far a run got, for the Sync running card.</summary>
public sealed record DriverSyncRunProgress
{
    /// <summary>The run.</summary>
    public required DriverSyncRun Run { get; init; }

    /// <summary>Vendor catalogs read.</summary>
    public required DriverSyncStage Catalogs { get; init; }

    /// <summary>Models checked.</summary>
    public required DriverSyncStage Models { get; init; }

    /// <summary>Component downloads finished, verified or failed; <see cref="DriverSyncRun.DownloadedComponents"/> counts the verified ones.</summary>
    public required DriverSyncStage Components { get; init; }

    /// <summary>Driver sets checked.</summary>
    public required DriverSyncStage DriverSets { get; init; }

    /// <summary>Items found so far, per outcome.</summary>
    public IReadOnlyDictionary<DriverSyncItemOutcome, int> Found { get; init; } = new Dictionary<DriverSyncItemOutcome, int>();

    /// <summary>The latest items, newest first.</summary>
    public IReadOnlyList<DriverSyncRunItem> Latest { get; init; } = [];

    /// <summary>Onboardings that start when this run finishes, in order.</summary>
    public IReadOnlyList<DriverQueuedRun> Queued { get; init; } = [];
}

/// <summary>An onboarding waiting for the running sync to finish.</summary>
public sealed record DriverQueuedRun
{
    /// <summary>Model.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Model name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Who completed the onboarding.</summary>
    public required string QueuedBy { get; init; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset QueuedAt { get; init; }
}

/// <summary>A run and its items, for the Driver Synchronization report.</summary>
public sealed record DriverSyncReportRun
{
    /// <summary>The run.</summary>
    public required DriverSyncRun Run { get; init; }

    /// <summary>Its items in order.</summary>
    public IReadOnlyList<DriverSyncRunItem> Items { get; init; } = [];
}

/// <summary>One item of a run.</summary>
public sealed record DriverSyncRunItem
{
    /// <summary>Model id.</summary>
    public Guid? ModelId { get; init; }

    /// <summary>Model name.</summary>
    public string? ModelName { get; init; }

    /// <summary>Release.</summary>
    public WindowsRelease? Release { get; init; }

    /// <summary>Lineage key, to link to the component row.</summary>
    public string? LineageKey { get; init; }

    /// <summary>Component name.</summary>
    public string? ComponentName { get; init; }

    /// <summary>Version before.</summary>
    public string? OldVersion { get; init; }

    /// <summary>Version after.</summary>
    public string? NewVersion { get; init; }

    /// <summary>Outcome.</summary>
    public DriverSyncItemOutcome Outcome { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Vendor of the model or download.</summary>
    public DriverVendor? Vendor { get; init; }

    /// <summary>Vendor category of the component.</summary>
    public string? Category { get; init; }
}

/// <summary>Response of a request that starts a run.</summary>
public sealed record DriverRunStarted
{
    /// <summary>Run id.</summary>
    public Guid RunId { get; init; }
}

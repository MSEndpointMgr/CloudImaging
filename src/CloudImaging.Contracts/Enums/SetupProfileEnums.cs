using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Registry hive of the applied Windows image that a setup profile value is written to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupRegistryHive
{
    /// <summary>HKLM\SOFTWARE of the applied image.</summary>
    Software,

    /// <summary>HKLM\SYSTEM of the applied image.</summary>
    System,

    /// <summary>The default user profile hive (Users\Default\NTUSER.DAT), copied to every new user.</summary>
    DefaultUser,
}

/// <summary>Registry value types a setup profile can write.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupRegistryValueType
{
    /// <summary>REG_SZ. Travels as "String" in JSON.</summary>
    [JsonStringEnumMemberName("String")]
    Text,

    /// <summary>REG_EXPAND_SZ.</summary>
    ExpandString,

    /// <summary>REG_MULTI_SZ, lines separated by a line feed.</summary>
    MultiString,

    /// <summary>REG_DWORD.</summary>
    DWord,

    /// <summary>REG_QWORD.</summary>
    QWord,
}

/// <summary>When a setup profile script runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupScriptPhase
{
    /// <summary>In WinPE, during the Prepare setup step, with the applied Windows drive available.</summary>
    WinPE,

    /// <summary>During the specialize pass of Windows setup, through the setup runner.</summary>
    WindowsSetup,
}

/// <summary>What a failed registry value, In WinPE script or answer file does to the deployment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupFailureHandling
{
    /// <summary>The deployment continues and completes with warnings.</summary>
    CompleteWithWarnings,

    /// <summary>The deployment stops before Configure boot and fails.</summary>
    FailDeployment,
}

/// <summary>Kind of a per-item outcome reported for an imaging step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepItemKind
{
    /// <summary>A registry value written to the applied image.</summary>
    RegistryValue,

    /// <summary>A script run in WinPE.</summary>
    WinPEScript,

    /// <summary>A script staged to run during Windows setup.</summary>
    WindowsSetupScript,

    /// <summary>A driver installer staged to run during Windows setup.</summary>
    DriverInstaller,

    /// <summary>The device's answer file.</summary>
    AnswerFile,

    /// <summary>A driver component of the pinned driver set. Appended so stored values stay stable.</summary>
    DriverComponent,
}

/// <summary>Outcome of a per-item step entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepItemOutcome
{
    /// <summary>The item was applied or ran successfully.</summary>
    Succeeded,

    /// <summary>The item failed; the reason says why.</summary>
    Failed,

    /// <summary>The item was staged and runs during Windows setup.</summary>
    Queued,

    /// <summary>The item was skipped because an earlier item failed and the profile fails the deployment.</summary>
    NotRun,

    /// <summary>The component was not processed because the device lacks its hardware. Appended so stored values stay stable.</summary>
    Skipped,
}

/// <summary>Kind of a setup profile history entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupProfileHistoryKind
{
    /// <summary>The profile was created.</summary>
    Created,

    /// <summary>The profile was renamed.</summary>
    Renamed,

    /// <summary>Locations were added or removed, or All devices was switched on or off.</summary>
    TargetingChanged,

    /// <summary>A targeted location was removed because the location was deleted.</summary>
    LocationDeleted,

    /// <summary>A new version was saved.</summary>
    VersionSaved,

    /// <summary>An earlier version was restored as a new version.</summary>
    VersionRestored,

    /// <summary>The profile was created as a duplicate of another profile.</summary>
    Duplicated,

    /// <summary>The profile was deleted.</summary>
    Deleted,
}

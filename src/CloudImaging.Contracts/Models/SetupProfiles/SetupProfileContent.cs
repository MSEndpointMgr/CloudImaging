using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.SetupProfiles;

/// <summary>
/// The immutable content of one setup profile version: registry values, scripts, an optional
/// answer file and the failure handling setting. Stored as one JSON blob per version, and sent by
/// the portal as the <c>content</c> of a create or save request (ids, hashes and modified fields
/// are then assigned by Imaging Core).
/// </summary>
public sealed record SetupProfileContent
{
    /// <summary>Version of this document's format.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Maximum number of registry values per profile.</summary>
    public const int MaxRegistryValues = 200;

    /// <summary>Maximum number of scripts per profile.</summary>
    public const int MaxScripts = 20;

    /// <summary>Maximum script size in bytes of UTF-8.</summary>
    public const int MaxScriptBytes = 65_536;

    /// <summary>Maximum answer file size in bytes of UTF-8.</summary>
    public const int MaxAnswerFileBytes = 262_144;

    /// <summary>Format version of this document.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Profile the version belongs to.</summary>
    public Guid ProfileId { get; init; }

    /// <summary>Version number, starting at 1.</summary>
    public int Version { get; init; }

    /// <summary>What failed registry values, In WinPE scripts or answer file writes do to the deployment.</summary>
    public SetupFailureHandling FailureHandling { get; init; } = SetupFailureHandling.CompleteWithWarnings;

    /// <summary>Registry values written to the applied image.</summary>
    public IReadOnlyList<SetupRegistryValue> RegistryValues { get; init; } = [];

    /// <summary>Scripts, ordered within each phase by <see cref="SetupScript.Order"/>.</summary>
    public IReadOnlyList<SetupScript> Scripts { get; init; } = [];

    /// <summary>The profile's own answer file, or null when it has none.</summary>
    public SetupAnswerFile? AnswerFile { get; init; }
}

/// <summary>A registry value written to the applied image during Prepare setup.</summary>
public sealed record SetupRegistryValue
{
    /// <summary>Stable id across versions; null in a request for a new value.</summary>
    public Guid? Id { get; init; }

    /// <summary>Why the value is set, 1 to 100 characters.</summary>
    public required string Name { get; init; }

    /// <summary>Hive of the applied image.</summary>
    public SetupRegistryHive Hive { get; init; }

    /// <summary>Key path relative to the hive root.</summary>
    public required string Key { get; init; }

    /// <summary>Value name; empty for the key's default value.</summary>
    public string ValueName { get; init; } = string.Empty;

    /// <summary>Registry value type.</summary>
    public SetupRegistryValueType Type { get; init; }

    /// <summary>Value data as text; numbers in decimal or 0x hex, multi-string lines separated by a line feed.</summary>
    public string Data { get; init; } = string.Empty;
}

/// <summary>A PowerShell script that runs in WinPE or during Windows setup.</summary>
public sealed record SetupScript
{
    /// <summary>Default timeout in minutes.</summary>
    public const int DefaultTimeoutMinutes = 5;

    /// <summary>Stable id across versions; null in a request for a new script.</summary>
    public Guid? Id { get; init; }

    /// <summary>Script name, unique within the profile.</summary>
    public required string Name { get; init; }

    /// <summary>When the script runs.</summary>
    public SetupScriptPhase Runs { get; init; }

    /// <summary>1-based position within its phase.</summary>
    public int Order { get; init; }

    /// <summary>Timeout from 1 to 30 minutes.</summary>
    public int TimeoutMinutes { get; init; } = DefaultTimeoutMinutes;

    /// <summary>The PowerShell script text.</summary>
    public required string Content { get; init; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 content without BOM, computed by Imaging Core.</summary>
    public string? Sha256 { get; init; }

    /// <summary>When the script last changed.</summary>
    public DateTimeOffset? LastModifiedAt { get; init; }

    /// <summary>Who last changed the script.</summary>
    public string? LastModifiedBy { get; init; }
}

/// <summary>The profile's own answer file (unattend.xml).</summary>
public sealed record SetupAnswerFile
{
    /// <summary>The XML text.</summary>
    public required string Content { get; init; }

    /// <summary>Distinct configuration passes in document order, computed by Imaging Core.</summary>
    public IReadOnlyList<string> Passes { get; init; } = [];

    /// <summary>Size in bytes of UTF-8.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 content, computed by Imaging Core.</summary>
    public string? Sha256 { get; init; }

    /// <summary>When the answer file last changed.</summary>
    public DateTimeOffset? LastModifiedAt { get; init; }

    /// <summary>Who last changed the answer file.</summary>
    public string? LastModifiedBy { get; init; }
}

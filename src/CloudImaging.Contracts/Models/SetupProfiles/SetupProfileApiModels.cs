using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.SetupProfiles;

/// <summary>Which devices a setup profile applies to.</summary>
public sealed record SetupProfileTargeting
{
    /// <summary>True when the profile applies to every device whose location no other profile targets.</summary>
    public bool AllDevices { get; init; }

    /// <summary>Targeted locations; empty when <see cref="AllDevices"/> is true.</summary>
    public IReadOnlyList<Guid> LocationIds { get; init; } = [];
}

/// <summary>The signed-in portal user who made a change, taken from the portal token.</summary>
public sealed record SetupProfileActor
{
    /// <summary>User principal name.</summary>
    public required string Upn { get; init; }

    /// <summary>Entra object id.</summary>
    public string? ObjectId { get; init; }
}

/// <summary>A setup profile as shown in the profile list.</summary>
public record SetupProfileSummary
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Targeted devices.</summary>
    public required SetupProfileTargeting Targeting { get; init; }

    /// <summary>Number of registry values in the current version.</summary>
    public int RegistryValueCount { get; init; }

    /// <summary>Number of scripts in the current version.</summary>
    public int ScriptCount { get; init; }

    /// <summary>True when the current version has an answer file.</summary>
    public bool HasAnswerFile { get; init; }

    /// <summary>Failure handling of the current version.</summary>
    public SetupFailureHandling FailureHandling { get; init; }

    /// <summary>Current version number.</summary>
    public int CurrentVersion { get; init; }

    /// <summary>Last change of content, name or targeting.</summary>
    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>Who made the last change.</summary>
    public string? LastModifiedBy { get; init; }
}

/// <summary>A setup profile with the content of its current version.</summary>
public sealed record SetupProfileDetail : SetupProfileSummary
{
    /// <summary>When the profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who created the profile.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Content of the current version.</summary>
    public required SetupProfileContent Content { get; init; }
}

/// <summary>Source of a duplicated profile, as sent with a create request.</summary>
public sealed record SetupProfileSourceReference
{
    /// <summary>Profile that was duplicated.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Version that was duplicated.</summary>
    public required int Version { get; init; }
}

/// <summary>Source of a duplicated profile, as recorded on its first version.</summary>
public sealed record SetupProfileDuplicatedFrom
{
    /// <summary>Profile that was duplicated.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Its name when it was duplicated.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Version that was duplicated.</summary>
    public required int Version { get; init; }
}

/// <summary>Request to create a setup profile.</summary>
public sealed record CreateSetupProfileRequest
{
    /// <summary>Profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Targeted devices.</summary>
    public required SetupProfileTargeting Targeting { get; init; }

    /// <summary>Set when the profile is a duplicate of another profile.</summary>
    public SetupProfileSourceReference? DuplicatedFrom { get; init; }

    /// <summary>Content of version 1.</summary>
    public required SetupProfileContent Content { get; init; }

    /// <summary>Acting user, set by the portal backend from the token.</summary>
    public SetupProfileActor? Actor { get; init; }
}

/// <summary>Request to save a setup profile.</summary>
public sealed record SaveSetupProfileRequest
{
    /// <summary>Version the editor started from; must still be the current version.</summary>
    public required int BaseVersion { get; init; }

    /// <summary>Profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Targeted devices.</summary>
    public required SetupProfileTargeting Targeting { get; init; }

    /// <summary>New content; null when only the name or targeting changed.</summary>
    public SetupProfileContent? Content { get; init; }

    /// <summary>Acting user, set by the portal backend from the token.</summary>
    public SetupProfileActor? Actor { get; init; }
}

/// <summary>Request to restore an earlier version as a new current version.</summary>
public sealed record RestoreSetupProfileVersionRequest
{
    /// <summary>Version whose content is restored.</summary>
    public required int Version { get; init; }

    /// <summary>Version the user saw as current; must still be the current version.</summary>
    public required int BaseVersion { get; init; }

    /// <summary>Acting user, set by the portal backend from the token.</summary>
    public SetupProfileActor? Actor { get; init; }
}

/// <summary>How the answer file changed between two versions.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SetupAnswerFileChange
{
    /// <summary>Unchanged.</summary>
    NoChange,

    /// <summary>Added.</summary>
    Added,

    /// <summary>Removed.</summary>
    Removed,

    /// <summary>Content changed.</summary>
    Changed,
}

/// <summary>Counts of changed items in one section of a version.</summary>
public sealed record SetupProfileSectionChange
{
    /// <summary>Items added.</summary>
    public int Added { get; init; }

    /// <summary>Items removed.</summary>
    public int Removed { get; init; }

    /// <summary>Items changed.</summary>
    public int Changed { get; init; }

    /// <summary>True when the order changed (scripts only).</summary>
    public bool Reordered { get; init; }
}

/// <summary>Failure handling change between two versions.</summary>
public sealed record SetupFailureHandlingChange
{
    /// <summary>Previous value.</summary>
    public required SetupFailureHandling From { get; init; }

    /// <summary>New value.</summary>
    public required SetupFailureHandling To { get; init; }
}

/// <summary>Names of the items that were added, removed or changed, at most 10 per section.</summary>
public sealed record SetupProfileChangeNames
{
    /// <summary>Most names kept per section.</summary>
    public const int MaxNamesPerSection = 10;

    /// <summary>Registry value names.</summary>
    public IReadOnlyList<string> RegistryValues { get; init; } = [];

    /// <summary>Script names.</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];
}

/// <summary>What changed in a version compared with the version before it.</summary>
public sealed record SetupProfileChangeSummary
{
    /// <summary>Registry value changes.</summary>
    public SetupProfileSectionChange RegistryValues { get; init; } = new();

    /// <summary>Script changes.</summary>
    public SetupProfileSectionChange Scripts { get; init; } = new();

    /// <summary>Answer file change.</summary>
    public SetupAnswerFileChange AnswerFile { get; init; }

    /// <summary>Failure handling change, or null when unchanged.</summary>
    public SetupFailureHandlingChange? FailureHandling { get; init; }

    /// <summary>Names of changed items.</summary>
    public SetupProfileChangeNames Names { get; init; } = new();

    /// <summary>Total number of changes shown in the Changes column.</summary>
    public int Total { get; init; }
}

/// <summary>A retained version of a setup profile.</summary>
public record SetupProfileVersionSummary
{
    /// <summary>Version number.</summary>
    public required int Version { get; init; }

    /// <summary>When the version was saved.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who saved the version.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>What changed compared with the version before it.</summary>
    public SetupProfileChangeSummary ChangeSummary { get; init; } = new();

    /// <summary>Set when the version was created by restoring an earlier version.</summary>
    public int? RestoredFrom { get; init; }

    /// <summary>Set on version 1 of a duplicated profile.</summary>
    public SetupProfileDuplicatedFrom? DuplicatedFrom { get; init; }

    /// <summary>True for the current version.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Number of running deployments pinned to this version.</summary>
    public int InUseCount { get; init; }
}

/// <summary>A retained version with its content.</summary>
public sealed record SetupProfileVersionDetail : SetupProfileVersionSummary
{
    /// <summary>The version's content.</summary>
    public required SetupProfileContent Content { get; init; }
}

/// <summary>A profile-level history entry.</summary>
public sealed record SetupProfileHistoryEntry
{
    /// <summary>What happened.</summary>
    public required SetupProfileHistoryKind Kind { get; init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Who did it.</summary>
    public string? By { get; init; }

    /// <summary>Ready-to-show sentence describing the change.</summary>
    public string Change { get; init; } = string.Empty;
}

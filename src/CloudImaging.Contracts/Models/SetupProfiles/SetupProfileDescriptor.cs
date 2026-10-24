using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.SetupProfiles;

/// <summary>
/// The setup profile version pinned on a session, as returned to the device. The content itself is
/// downloaded from <see cref="ContentUrl"/> and checked against <see cref="ContentSha256"/>.
/// </summary>
public sealed record SetupProfileDescriptor
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name when the session was assigned.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Pinned version.</summary>
    public required int Version { get; init; }

    /// <summary>Failure handling of the pinned version.</summary>
    public SetupFailureHandling FailureHandling { get; init; }

    /// <summary>Short-lived read-only URL of the version document. Never logged.</summary>
    public required string ContentUrl { get; init; }

    /// <summary>When <see cref="ContentUrl"/> expires.</summary>
    public DateTimeOffset ContentUrlExpiresAt { get; init; }

    /// <summary>Lowercase hex SHA-256 of the version document.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Size of the version document in bytes.</summary>
    public long ContentSizeBytes { get; init; }

    /// <summary>The scripts this device runs: those for all locations and those for the session's location.</summary>
    public IReadOnlyList<SetupProfileDescriptorText> Scripts { get; init; } = [];

    /// <summary>The answer file text, or null when the version has none.</summary>
    public SetupProfileDescriptorText? AnswerFile { get; init; }
}

/// <summary>One script or answer file text the device downloads and checks.</summary>
public sealed record SetupProfileDescriptorText
{
    /// <summary>Script id; null for the answer file.</summary>
    public Guid? Id { get; init; }

    /// <summary>Lowercase hex SHA-256 of the text.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Size of the text in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Short-lived read-only URL of the text. Never logged.</summary>
    public required string ContentUrl { get; init; }

    /// <summary>When <see cref="ContentUrl"/> expires.</summary>
    public DateTimeOffset ContentUrlExpiresAt { get; init; }
}

/// <summary>Result of uploading a script or answer file text.</summary>
public sealed record SetupProfileContentUploadResult
{
    /// <summary>Lowercase hex SHA-256 of the text without BOM.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size of the text in bytes, without BOM.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Configuration passes, for an answer file that passes every rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Passes { get; init; }

    /// <summary>Answer file problems; empty when it passes. Null for a script.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SetupProfileContentProblem>? Problems { get; init; }
}

/// <summary>One answer file problem returned with an upload.</summary>
public sealed record SetupProfileContentProblem
{
    /// <summary>Stable rule code.</summary>
    public required string Code { get; init; }

    /// <summary>1-based line; 0 for the whole file.</summary>
    public int Line { get; init; }

    /// <summary>Ready-to-show sentence.</summary>
    public required string Message { get; init; }
}

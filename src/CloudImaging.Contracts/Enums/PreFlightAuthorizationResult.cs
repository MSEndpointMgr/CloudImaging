using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Pre-flight authorization outcomes (FR-026).</summary>
/// <remarks>Serialized as its name for the same reason as <see cref="SessionState"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightAuthorizationResult
{
    /// <summary>The pre-flight check is disabled or was not performed for this session.</summary>
    Skipped,

    /// <summary>The device matched a Windows Autopilot v1 enrollment record.</summary>
    MatchedAutopilotV1,

    /// <summary>The device matched an authorized corporate identifier record.</summary>
    MatchedCorporateIdentifier,

    /// <summary>The device did not match any authorization record and was not allowed to proceed.</summary>
    NotAuthorized
}

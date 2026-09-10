using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Pre-flight authorization outcomes (FR-026).</summary>
/// <remarks>Serialized as its name for the same reason as <see cref="SessionState"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightAuthorizationResult
{
    Skipped,
    MatchedAutopilotV1,
    MatchedCorporateIdentifier,
    NotAuthorized
}

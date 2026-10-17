using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Pre-flight authorization outcomes (FR-026).</summary>
/// <remarks>Serialized as its name for the same reason as <see cref="SessionState"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreFlightAuthorizationResult
{
    /// <summary>The Autopilot presence requirement is turned off, so Graph was never called.</summary>
    Skipped,

    /// <summary>The device's hardware hash matched an existing Autopilot (v1) registration.</summary>
    MatchedAutopilotV1,

    /// <summary>The device matched a configured corporate identifier instead of Autopilot.</summary>
    MatchedCorporateIdentifier,

    /// <summary>Neither Autopilot nor a corporate identifier matched; the device is not authorized.</summary>
    NotAuthorized
}

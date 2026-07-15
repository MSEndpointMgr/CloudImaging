namespace CloudImaging.Contracts.Enums;

/// <summary>Pre-flight authorization outcomes (FR-026).</summary>
public enum PreFlightAuthorizationResult
{
    Skipped,
    MatchedAutopilotV1,
    MatchedCorporateIdentifier,
    NotAuthorized
}

using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>One pre-flight check as evaluated when a session was created.</summary>
public sealed record PreFlightCheckResult
{
    public required PreFlightCheck Check { get; init; }
    public required PreFlightCheckOutcome Outcome { get; init; }

    /// <summary>What the device reported, as one of the <see cref="PreFlightObserved"/> values or a posture enum name.</summary>
    public required string Observed { get; init; }

    /// <summary>Administrator who approved this check, when <see cref="Outcome"/> is <see cref="PreFlightCheckOutcome.Approved"/>.</summary>
    public string? ApprovedBy { get; init; }
}

/// <summary>Observed values that are not a posture enum name.</summary>
public static class PreFlightObserved
{
    /// <summary>The Client predates posture reporting, so it sent nothing (outdated boot media).</summary>
    public const string NotReported = "NotReported";

    /// <summary>Autopilot presence was not looked up because the requirement was off.</summary>
    public const string NotChecked = "NotChecked";

    public const string Autopilot = "Autopilot";
    public const string CorporateIdentifier = "CorporateIdentifier";
    public const string NotFound = "NotFound";
}

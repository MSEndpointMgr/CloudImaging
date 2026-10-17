using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// One-time administrator approval for a blocked device's next session. Covers only the checks
/// that failed on <see cref="SourceSessionId"/>; any other failure still blocks. Used up by the
/// first session from <see cref="SerialNumber"/> that it fully covers, or dropped at <see cref="ExpiresAt"/>.
/// </summary>
public sealed record PreFlightOverride
{
    /// <summary>Device serial number the override applies to.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>The checks this override covers.</summary>
    public required IReadOnlyList<PreFlightCheck> CoveredChecks { get; init; }

    /// <summary>The blocked session the approval was made from.</summary>
    public required Guid SourceSessionId { get; init; }

    /// <summary>Device manufacturer, as reported by firmware.</summary>
    public required string DeviceManufacturer { get; init; }

    /// <summary>Device model, as reported by firmware.</summary>
    public required string DeviceModel { get; init; }

    /// <summary>Display name of the device's location, captured at approval time.</summary>
    public string? LocationName { get; init; }

    /// <summary>Check results of the source session, so the portal can show what was approved without it.</summary>
    public IReadOnlyList<PreFlightCheckResult> SourceChecks { get; init; } = [];

    /// <summary>UPN of the administrator who approved the override.</summary>
    public required string ApprovedBy { get; init; }

    /// <summary>Entra object id of the administrator who approved the override.</summary>
    public string? ApprovedByObjectId { get; init; }

    /// <summary>When the override was approved.</summary>
    public required DateTimeOffset ApprovedAt { get; init; }

    /// <summary>When the override can no longer be used.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

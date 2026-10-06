using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// One-time administrator approval for a blocked device's next session. Covers only the checks
/// that failed on <see cref="SourceSessionId"/>; any other failure still blocks. Used up by the
/// first session from <see cref="SerialNumber"/> that it fully covers, or dropped at <see cref="ExpiresAt"/>.
/// </summary>
public sealed record PreFlightOverride
{
    public required string SerialNumber { get; init; }
    public required IReadOnlyList<PreFlightCheck> CoveredChecks { get; init; }

    /// <summary>The blocked session the approval was made from.</summary>
    public required Guid SourceSessionId { get; init; }

    public required string DeviceManufacturer { get; init; }
    public required string DeviceModel { get; init; }
    public string? LocationName { get; init; }

    /// <summary>Check results of the source session, so the portal can show what was approved without it.</summary>
    public IReadOnlyList<PreFlightCheckResult> SourceChecks { get; init; } = [];

    public required string ApprovedBy { get; init; }
    public string? ApprovedByObjectId { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

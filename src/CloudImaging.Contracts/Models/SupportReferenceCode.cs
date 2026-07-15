namespace CloudImaging.Contracts.Models;

/// <summary>
/// Structured support reference code value object (FR-009, FR-058).
/// Format: {ComponentCode}-{SessionRef}-{StageCode}-{EpochSeconds}
/// Example: CIC-A1B2C3D4-DWN-1750000000
/// </summary>
public sealed class SupportReferenceCode
{
    /// <summary>3-letter component prefix: CIC = Cloud Imaging Client, CMB = Media Builder.</summary>
    public required string ComponentCode { get; init; }

    /// <summary>
    /// First 8 characters of the session ID for Client operations,
    /// or a short operation-stage identifier for Media Builder standalone operations.
    /// </summary>
    public required string SessionRef { get; init; }

    /// <summary>
    /// Abbreviated step identifier.
    /// Client: REG, FMT, DWN, APL.
    /// Media Builder: DVI, PRT, BID, BCF.
    /// </summary>
    public required string StageCode { get; init; }

    /// <summary>Unix epoch at time of error (seconds since 1970-01-01 UTC).</summary>
    public required long EpochSeconds { get; init; }

    public override string ToString() =>
        $"{ComponentCode}-{SessionRef}-{StageCode}-{EpochSeconds}";

    /// <summary>Generate a support reference code for the Cloud Imaging Client.</summary>
    public static SupportReferenceCode ForClient(Guid sessionId, string stageCode) =>
        new()
        {
            ComponentCode = "CIC",
            SessionRef = sessionId.ToString("N")[..8].ToUpperInvariant(),
            StageCode = stageCode,
            EpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

    /// <summary>Generate a support reference code for the Cloud Imaging Media Builder.</summary>
    public static SupportReferenceCode ForMediaBuilder(string operationStageRef, string stageCode) =>
        new()
        {
            ComponentCode = "CMB",
            SessionRef = operationStageRef,
            StageCode = stageCode,
            EpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
}

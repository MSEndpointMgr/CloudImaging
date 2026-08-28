namespace CloudImaging.Contracts.Models;

/// <summary>
/// Deployment-wide portal configuration persisted in Table Storage (Key Entities, FR-026a, FR-022).
/// </summary>
public sealed class PortalConfiguration
{
    /// <summary>
    /// Enables or disables the device pre-flight authorization check globally (FR-026a).
    /// Default: false (disabled) — bare-metal imaging works without device pre-enrollment;
    /// administrators opt in explicitly once device authorization records are in place.
    /// </summary>
    public bool DevicePreFlightAuthorizationEnabled { get; init; }

    /// <summary>
    /// OS image SAS token URL expiry in minutes (FR-022).
    /// Default: 240 (4 hours). Administrator-settable at runtime.
    /// </summary>
    public int SasTokenUrlExpiryMinutes { get; init; } = 240;

    /// <summary>
    /// Boot image SAS token URL expiry in minutes.
    /// Default: 120 (2 hours) — shorter than OS image SAS.
    /// </summary>
    public int BootImageSasExpiryMinutes { get; init; } = 120;

    /// <summary>
    /// Certificate validity period in days (FR-068).
    /// Default: 365 days (1 year).
    /// </summary>
    public int CertValidityPeriodDays { get; init; } = 365;

    /// <summary>
    /// Clock skew tolerance in seconds for token expiry boundary checks (FR-005).
    /// Default: 30 seconds.
    /// </summary>
    public int ClockSkewToleranceSeconds { get; init; } = 30;

    /// <summary>
    /// How long completed session outcomes are retained in the SessionHistory audit table
    /// (Reports feature) before being purged. Default: 90 days. Applied once, at the time a
    /// history record is written — changing this value is not retroactive; already-written
    /// records keep the retention that was in effect when they were created.
    /// </summary>
    public int SessionHistoryRetentionDays { get; init; } = 90;

    public DateTimeOffset LastModifiedAt { get; init; }
}

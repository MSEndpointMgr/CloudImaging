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

    // Requirements evaluated only while DevicePreFlightAuthorizationEnabled is on; saved values are kept while it is off.

    /// <summary>Device must be in Windows Autopilot or imported as a Corporate Identifier.</summary>
    public bool PreFlightRequireAutopilotPresence { get; init; }

    /// <summary>Blocks devices booted in Legacy BIOS (CSM) mode.</summary>
    public bool PreFlightRequireUefiFirmware { get; init; }

    /// <summary>Blocks devices where Secure Boot is disabled, in setup mode, or unavailable.</summary>
    public bool PreFlightRequireSecureBoot { get; init; }

    /// <summary>Blocks devices whose firmware does not expose a TPM 2.0.</summary>
    public bool PreFlightRequireTpm20 { get; init; }

    /// <summary>False when pre-flight is on but no requirement is selected, which would block nothing.</summary>
    public bool IsPreFlightConfigurationValid() =>
        !DevicePreFlightAuthorizationEnabled
        || PreFlightRequireAutopilotPresence || PreFlightRequireUefiFirmware || PreFlightRequireSecureBoot || PreFlightRequireTpm20;

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

    /// <summary>
    /// Opt-in: allows the portal backend to check GitHub for a newer Cloud Imaging release.
    /// Default: false. Off by default because it is the only outbound call the portal makes to
    /// an endpoint outside the customer's tenant, which some deployments prohibit and
    /// air-gapped ones cannot satisfy at all. Enforced server-side, so while this is false no
    /// request to github.com is issued.
    /// </summary>
    public bool UpdateCheckEnabled { get; init; }

    /// <summary>
    /// Allows devices to submit Windows Autopilot hardware hashes from the boot media for approval.
    /// Default: false. Importing needs the Graph DeviceManagementServiceConfig.ReadWrite.All
    /// permission on Imaging Core's identity, which an administrator grants deliberately.
    /// </summary>
    public bool AutopilotRegistrationEnabled { get; init; }

    /// <summary>When true, approvers cannot approve an Autopilot request without choosing a group tag.</summary>
    public bool AutopilotGroupTagRequired { get; init; }

    /// <summary>Days an undecided Autopilot request stays pending before it expires and its hash is purged. Default: 7.</summary>
    public int AutopilotPendingExpiryDays { get; init; } = 7;

    /// <summary>Days a handled Autopilot request (imported, already registered, rejected or expired) is kept for audit before it is deleted. Default: 365.</summary>
    public int AutopilotRetentionDays { get; init; } = 365;

    /// <summary>When the configuration was last modified.</summary>
    public DateTimeOffset LastModifiedAt { get; init; }
}

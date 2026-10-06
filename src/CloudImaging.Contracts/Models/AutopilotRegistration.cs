using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// A Windows Autopilot (v1) registration request raised by a device from the boot media. It lives
/// outside the imaging session lifecycle so submitting one never blocks imaging, and it outlives
/// the short session retention so the approval decision stays auditable.
/// </summary>
public sealed record AutopilotRegistrationRequest
{
    public required Guid RequestId { get; init; }

    /// <summary>Short code shown on the device so a technician can find the request in the portal.</summary>
    public required string ReferenceCode { get; init; }

    public required string SerialNumber { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;
    public Guid? LocationId { get; init; }
    public string? LocationName { get; init; }

    /// <summary>
    /// Base64 4K hardware hash. Sensitive device data, so it never leaves Imaging Core in JSON and
    /// is purged from storage as soon as the request reaches a terminal state.
    /// </summary>
    [JsonIgnore]
    public string? HardwareHash { get; init; }

    /// <summary>SHA-256 of the device-held status token. Never serialized.</summary>
    [JsonIgnore]
    public string? StatusTokenHash { get; init; }

    public required AutopilotRegistrationState State { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When a <see cref="AutopilotRegistrationState.PendingApproval"/> request expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public string? ClientVersion { get; init; }

    /// <summary>TPM version recorded in the hardware hash, or null when the hash has none.</summary>
    public string? TpmVersion { get; init; }

    /// <summary>True when the hash carries TPM 2.0 data for pre-provisioning and self-deploying; null when it could not be read.</summary>
    public bool? PreProvisioningReady { get; init; }

    public string? GroupTag { get; init; }
    public Guid? GroupTagDefinitionId { get; init; }
    public string? DecidedByUpn { get; init; }
    public string? DecidedByObjectId { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? RejectionReason { get; init; }

    /// <summary>Id of the Graph importedWindowsAutopilotDeviceIdentity while an import is in flight.</summary>
    public string? ImportedIdentityId { get; init; }
    public DateTimeOffset? ImportStartedAt { get; init; }
    public DateTimeOffset? ImportCompletedAt { get; init; }
    public int ImportAttempts { get; init; }
    public string? ImportErrorCode { get; init; }
    public string? ImportErrorName { get; init; }

    public static bool IsTerminal(AutopilotRegistrationState state) => state is
        AutopilotRegistrationState.Imported
        or AutopilotRegistrationState.Rejected
        or AutopilotRegistrationState.Expired
        or AutopilotRegistrationState.AlreadyRegistered;
}

/// <summary>Hardware hash submission sent by the Cloud Imaging Client through the Device Gateway.</summary>
public sealed class AutopilotHashSubmission
{
    public required string SerialNumber { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string HardwareHash { get; init; }
    public MachineArchitecture? Architecture { get; init; }
    public Guid? LocationId { get; init; }
    public string? LocationName { get; init; }
    public string? ClientVersion { get; init; }

    /// <summary>
    /// Signature over <see cref="DevicePayloadSignature.BuildAutopilotChallenge"/>, which binds the
    /// hardware hash itself so a captured request cannot be replayed with a different hash.
    /// </summary>
    public DeviceProofOfPossession? ProofOfPossession { get; init; }
}

/// <summary>Returned to the device once a submission is accepted.</summary>
public sealed class AutopilotSubmissionResponse
{
    public required Guid RequestId { get; init; }
    public required string ReferenceCode { get; init; }
    public required AutopilotRegistrationState State { get; init; }

    /// <summary>Bearer token the device presents to poll this request's status. Returned once.</summary>
    public required string StatusToken { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Device-facing status projection. Deliberately omits approver identity and import internals.</summary>
public sealed class AutopilotRegistrationStatus
{
    public required Guid RequestId { get; init; }
    public required string ReferenceCode { get; init; }
    public required AutopilotRegistrationState State { get; init; }
    public string? GroupTag { get; init; }
    public string? RejectionReason { get; init; }
    public string? ImportErrorName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Whether the device should offer Autopilot registration at all.</summary>
public sealed class AutopilotAvailability
{
    public bool Enabled { get; init; }
}

/// <summary>Admin-defined group tag an approver can choose from.</summary>
public sealed class AutopilotGroupTagDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required AutopilotGroupTagKind Kind { get; init; }

    /// <summary>The literal tag for <see cref="AutopilotGroupTagKind.Static"/>, or the template text.</summary>
    public required string Value { get; init; }

    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A group tag definition evaluated against one request, as offered to the approver.</summary>
public sealed class AutopilotGroupTagOption
{
    public required Guid DefinitionId { get; init; }
    public required string Name { get; init; }
    public required AutopilotGroupTagKind Kind { get; init; }
    public required string Value { get; init; }

    /// <summary>The tag that would be imported, or null when the option is unavailable for this device.</summary>
    public string? ResolvedValue { get; init; }

    public string? UnavailableReason { get; init; }
}

/// <summary>A request plus everything an approver needs to decide on it.</summary>
public sealed class AutopilotRegistrationDetail
{
    public required AutopilotRegistrationRequest Request { get; init; }
    public required IReadOnlyList<AutopilotGroupTagOption> GroupTagOptions { get; init; }
    public bool GroupTagRequired { get; init; }
    public string? LocationRegion { get; init; }
    public string? LocationCountryCode { get; init; }
}

/// <summary>Approve, reject or retry decision forwarded by the portal backend.</summary>
public sealed class AutopilotDecision
{
    /// <summary>UPN of the signed-in portal user, taken from their validated token by the portal backend.</summary>
    public required string DecidedByUpn { get; init; }
    public string? DecidedByObjectId { get; init; }
    public Guid? GroupTagDefinitionId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Token resolution and validation for Autopilot group tag templates, shared by Imaging Core and
/// tests so the rules cannot drift between the place that stores a definition and the place that
/// resolves it.
/// </summary>
public static partial class AutopilotGroupTagTemplate
{
    public const string LocationNameToken = "{LocationName}";
    public const string RegionToken = "{Region}";
    public const string CountryCodeToken = "{CountryCode}";
    public const int MaxLength = 128;

    public static readonly IReadOnlyList<string> SupportedTokens = [LocationNameToken, RegionToken, CountryCodeToken];

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TokenPattern();

    // Conservative character set so a resolved tag is safe in Intune dynamic group rules.
    [GeneratedRegex(@"^[A-Za-z0-9 _.\-]+$")]
    private static partial Regex AllowedCharacters();

    /// <summary>Returns a validation error for a definition, or null when it is valid.</summary>
    public static string? ValidateDefinition(AutopilotGroupTagKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A value is required.";
        }

        if (kind == AutopilotGroupTagKind.Static)
        {
            return ValidateResolvedValue(value.Trim());
        }

        var tokens = TokenPattern().Matches(value).Select(m => m.Value).ToList();
        if (tokens.Count == 0)
        {
            return "A template must contain at least one token. Use a static tag for fixed text.";
        }

        var unknown = tokens.FirstOrDefault(t => !SupportedTokens.Contains(t, StringComparer.Ordinal));
        if (unknown is not null)
        {
            return $"Unknown token {unknown}. Supported tokens: {string.Join(", ", SupportedTokens)}.";
        }

        var literal = TokenPattern().Replace(value, string.Empty);
        return literal.Length > 0 && !AllowedCharacters().IsMatch(literal)
            ? "Literal text may only contain letters, digits, spaces, hyphens, underscores and periods."
            : null;
    }

    /// <summary>Returns a validation error for a final tag value, or null when it can be imported.</summary>
    public static string? ValidateResolvedValue(string value)
    {
        if (value.Length > MaxLength)
        {
            return $"Group tags are limited to {MaxLength} characters.";
        }

        return AllowedCharacters().IsMatch(value)
            ? null
            : "Group tags may only contain letters, digits, spaces, hyphens, underscores and periods.";
    }

    /// <summary>
    /// Resolves a template against a device's Location. Returns the tag, or null plus the reason
    /// when a token has no value for this device.
    /// </summary>
    public static (string? Value, string? UnavailableReason) Resolve(string template, string? locationName, string? region, string? countryCode)
    {
        var missing = new List<string>();
        var resolved = TokenPattern().Replace(template, match =>
        {
            var tokenValue = match.Value switch
            {
                LocationNameToken => locationName,
                RegionToken => region,
                CountryCodeToken => countryCode,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(tokenValue))
            {
                missing.Add(match.Value);
                return string.Empty;
            }
            return tokenValue.Trim();
        });

        if (missing.Count > 0)
        {
            var reason = string.IsNullOrWhiteSpace(locationName)
                ? "The device has no location."
                : $"The location has no value for {string.Join(", ", missing.Distinct())}.";
            return (null, reason);
        }

        var error = ValidateResolvedValue(resolved);
        return error is null ? (resolved, null) : (null, error);
    }
}

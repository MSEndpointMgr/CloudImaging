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
    /// <summary>Identifier for this registration request.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>Short code shown on the device so a technician can find the request in the portal.</summary>
    public required string ReferenceCode { get; init; }

    /// <summary>Device serial number.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Device manufacturer, as reported by firmware.</summary>
    public required string Manufacturer { get; init; }

    /// <summary>Device model, as reported by firmware.</summary>
    public required string Model { get; init; }

    /// <summary>Processor architecture of the device.</summary>
    public MachineArchitecture Architecture { get; init; } = MachineArchitecture.X64;

    /// <summary>Location the device is registered at, if any.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Display name of <see cref="LocationId"/>, captured at submission time.</summary>
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

    /// <summary>Current lifecycle state of the request.</summary>
    public required AutopilotRegistrationState State { get; init; }

    /// <summary>When the device submitted the request.</summary>
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>When the request was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When a <see cref="AutopilotRegistrationState.PendingApproval"/> request expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Version of the Cloud Imaging Client that submitted the request.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>TPM version recorded in the hardware hash, or null when the hash has none.</summary>
    public string? TpmVersion { get; init; }

    /// <summary>True when the hash carries TPM 2.0 data for pre-provisioning and self-deploying; null when it could not be read.</summary>
    public bool? PreProvisioningReady { get; init; }

    /// <summary>Group tag to import the device with, resolved or chosen at decision time.</summary>
    public string? GroupTag { get; init; }

    /// <summary>Group tag definition the approver selected, if any.</summary>
    public Guid? GroupTagDefinitionId { get; init; }

    /// <summary>UPN of the approver who decided this request.</summary>
    public string? DecidedByUpn { get; init; }

    /// <summary>Entra object id of the approver who decided this request.</summary>
    public string? DecidedByObjectId { get; init; }

    /// <summary>When the request was decided.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Reason the approver rejected the request, if rejected.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>Id of the Graph importedWindowsAutopilotDeviceIdentity while an import is in flight.</summary>
    public string? ImportedIdentityId { get; init; }

    /// <summary>When the Graph import was submitted.</summary>
    public DateTimeOffset? ImportStartedAt { get; init; }

    /// <summary>When Intune reported the import as complete.</summary>
    public DateTimeOffset? ImportCompletedAt { get; init; }

    /// <summary>Number of times an import has been attempted for this request.</summary>
    public int ImportAttempts { get; init; }

    /// <summary>Error code Intune returned for a failed import, if any.</summary>
    public string? ImportErrorCode { get; init; }

    /// <summary>Error name Intune returned for a failed import, if any.</summary>
    public string? ImportErrorName { get; init; }

    /// <summary>True when <paramref name="state"/> is one from which the request cannot transition further.</summary>
    public static bool IsTerminal(AutopilotRegistrationState state) => state is
        AutopilotRegistrationState.Imported
        or AutopilotRegistrationState.Rejected
        or AutopilotRegistrationState.Expired
        or AutopilotRegistrationState.AlreadyRegistered;
}

/// <summary>Hardware hash submission sent by the Cloud Imaging Client through the Device Gateway.</summary>
public sealed class AutopilotHashSubmission
{
    /// <summary>Device serial number.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Device manufacturer, as reported by firmware.</summary>
    public required string Manufacturer { get; init; }

    /// <summary>Device model, as reported by firmware.</summary>
    public required string Model { get; init; }

    /// <summary>Base64 4K hardware hash captured by the device.</summary>
    public required string HardwareHash { get; init; }

    /// <summary>Processor architecture of the device.</summary>
    public MachineArchitecture? Architecture { get; init; }

    /// <summary>Location the device is registered at, if any.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Display name of <see cref="LocationId"/>, captured at submission time.</summary>
    public string? LocationName { get; init; }

    /// <summary>Version of the Cloud Imaging Client that submitted the request.</summary>
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
    /// <summary>Identifier for this registration request.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>Short code shown on the device so a technician can find the request in the portal.</summary>
    public required string ReferenceCode { get; init; }

    /// <summary>Current lifecycle state of the request.</summary>
    public required AutopilotRegistrationState State { get; init; }

    /// <summary>Bearer token the device presents to poll this request's status. Returned once.</summary>
    public required string StatusToken { get; init; }

    /// <summary>When a <see cref="AutopilotRegistrationState.PendingApproval"/> request expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Device-facing status projection. Deliberately omits approver identity and import internals.</summary>
public sealed class AutopilotRegistrationStatus
{
    /// <summary>Identifier for this registration request.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>Short code shown on the device so a technician can find the request in the portal.</summary>
    public required string ReferenceCode { get; init; }

    /// <summary>Current lifecycle state of the request.</summary>
    public required AutopilotRegistrationState State { get; init; }

    /// <summary>Group tag the device was, or will be, imported with.</summary>
    public string? GroupTag { get; init; }

    /// <summary>Reason the approver rejected the request, if rejected.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>Error name Intune returned for a failed import, if any.</summary>
    public string? ImportErrorName { get; init; }

    /// <summary>When the request was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Whether the device should offer Autopilot registration at all.</summary>
public sealed class AutopilotAvailability
{
    /// <summary>True when Autopilot registration is enabled for this tenant.</summary>
    public bool Enabled { get; init; }
}

/// <summary>Admin-defined group tag an approver can choose from.</summary>
public sealed class AutopilotGroupTagDefinition
{
    /// <summary>Identifier for this group tag definition.</summary>
    public required Guid Id { get; init; }

    /// <summary>Display name shown to approvers.</summary>
    public required string Name { get; init; }

    /// <summary>Whether <see cref="Value"/> is a literal tag or a template.</summary>
    public required AutopilotGroupTagKind Kind { get; init; }

    /// <summary>The literal tag for <see cref="AutopilotGroupTagKind.Static"/>, or the template text.</summary>
    public required string Value { get; init; }

    /// <summary>Optional free-text note shown to approvers.</summary>
    public string? Description { get; init; }

    /// <summary>When the definition was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A group tag definition evaluated against one request, as offered to the approver.</summary>
public sealed class AutopilotGroupTagOption
{
    /// <summary>Identifier of the source <see cref="AutopilotGroupTagDefinition"/>.</summary>
    public required Guid DefinitionId { get; init; }

    /// <summary>Display name shown to approvers.</summary>
    public required string Name { get; init; }

    /// <summary>Whether <see cref="Value"/> is a literal tag or a template.</summary>
    public required AutopilotGroupTagKind Kind { get; init; }

    /// <summary>The definition's literal tag or template text, unresolved.</summary>
    public required string Value { get; init; }

    /// <summary>The tag that would be imported, or null when the option is unavailable for this device.</summary>
    public string? ResolvedValue { get; init; }

    /// <summary>Why the option is unavailable, when <see cref="ResolvedValue"/> is null.</summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>A request plus everything an approver needs to decide on it.</summary>
public sealed class AutopilotRegistrationDetail
{
    /// <summary>The request being decided.</summary>
    public required AutopilotRegistrationRequest Request { get; init; }

    /// <summary>Group tags available for this device.</summary>
    public required IReadOnlyList<AutopilotGroupTagOption> GroupTagOptions { get; init; }

    /// <summary>True when a group tag must be selected before approving.</summary>
    public bool GroupTagRequired { get; init; }

    /// <summary>Region of the device's location, for template resolution context.</summary>
    public string? LocationRegion { get; init; }

    /// <summary>Country code of the device's location, for template resolution context.</summary>
    public string? LocationCountryCode { get; init; }
}

/// <summary>Approve, reject or retry decision forwarded by the portal backend.</summary>
public sealed class AutopilotDecision
{
    /// <summary>UPN of the signed-in portal user, taken from their validated token by the portal backend.</summary>
    public required string DecidedByUpn { get; init; }

    /// <summary>Entra object id of the signed-in portal user.</summary>
    public string? DecidedByObjectId { get; init; }

    /// <summary>Group tag definition the approver selected, if any.</summary>
    public Guid? GroupTagDefinitionId { get; init; }

    /// <summary>Reason for a reject decision.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Token resolution and validation for Autopilot group tag templates, shared by Imaging Core and
/// tests so the rules cannot drift between the place that stores a definition and the place that
/// resolves it.
/// </summary>
public static partial class AutopilotGroupTagTemplate
{
    /// <summary>Template token resolved to the device's location name.</summary>
    public const string LocationNameToken = "{LocationName}";

    /// <summary>Template token resolved to the device's location region.</summary>
    public const string RegionToken = "{Region}";

    /// <summary>Template token resolved to the device's location country code.</summary>
    public const string CountryCodeToken = "{CountryCode}";

    /// <summary>Maximum length of a resolved group tag.</summary>
    public const int MaxLength = 128;

    /// <summary>All tokens a template may reference.</summary>
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

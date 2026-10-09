using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Turns a registration's reported values into per-check results against the portal configuration.
/// Pure so the blocking rules can be tested without storage or Graph.
/// </summary>
public static class PreFlightCheckEvaluator
{
    /// <summary>Unused overrides drop off after this long.</summary>
    public static readonly TimeSpan OverrideLifetime = TimeSpan.FromDays(7);

    /// <summary>Evaluates each configured pre-flight check against the reported security posture and enrollment result.</summary>
    public static IReadOnlyList<PreFlightCheckResult> Evaluate(
        PortalConfiguration config,
        DeviceSecurityPosture? posture,
        PreFlightAuthorizationResult enrollment)
    {
        var on = config.DevicePreFlightAuthorizationEnabled;

        // A missing posture (Client older than this feature) or an undetectable value fails a required
        // check: either way the device cannot show it meets the requirement.
        return
        [
            Result(PreFlightCheck.AutopilotPresence, on && config.PreFlightRequireAutopilotPresence, ObservedEnrollment(enrollment),
                enrollment is PreFlightAuthorizationResult.MatchedAutopilotV1 or PreFlightAuthorizationResult.MatchedCorporateIdentifier),
            Result(PreFlightCheck.FirmwareMode, on && config.PreFlightRequireUefiFirmware, Observed(posture, p => p.FirmwareMode),
                posture?.FirmwareMode == FirmwareMode.Uefi),
            Result(PreFlightCheck.SecureBoot, on && config.PreFlightRequireSecureBoot, Observed(posture, p => p.SecureBoot),
                posture?.SecureBoot == SecureBootState.Enabled),
            Result(PreFlightCheck.TpmVersion, on && config.PreFlightRequireTpm20, Observed(posture, p => p.Tpm),
                posture?.Tpm == TpmPresence.Tpm20),
        ];
    }

    /// <summary>Returns the checks that failed.</summary>
    public static IReadOnlyList<PreFlightCheck> FailedChecks(IEnumerable<PreFlightCheckResult> checks) =>
        checks.Where(c => c.Outcome == PreFlightCheckOutcome.Failed).Select(c => c.Check).ToList();

    /// <summary>
    /// Returns the checks with every failure marked approved when <paramref name="preFlightOverride"/>
    /// covers all of them and has not expired; otherwise null, and the session stays blocked.
    /// </summary>
    public static IReadOnlyList<PreFlightCheckResult>? ApplyOverride(
        IReadOnlyList<PreFlightCheckResult> checks,
        PreFlightOverride preFlightOverride,
        DateTimeOffset now)
    {
        var failed = FailedChecks(checks);
        if (failed.Count == 0 || preFlightOverride.ExpiresAt <= now || failed.Except(preFlightOverride.CoveredChecks).Any())
        {
            return null;
        }

        return checks
            .Select(c => c.Outcome == PreFlightCheckOutcome.Failed
                ? c with { Outcome = PreFlightCheckOutcome.Approved, ApprovedBy = preFlightOverride.ApprovedBy }
                : c)
            .ToList();
    }

    private static PreFlightCheckResult Result(PreFlightCheck check, bool required, string observed, bool passes) => new()
    {
        Check = check,
        Outcome = !required ? PreFlightCheckOutcome.NotRequired : passes ? PreFlightCheckOutcome.Passed : PreFlightCheckOutcome.Failed,
        Observed = observed,
    };

    private static string Observed<T>(DeviceSecurityPosture? posture, Func<DeviceSecurityPosture, T> value)
        where T : struct, Enum =>
        posture is null ? PreFlightObserved.NotReported : value(posture).ToString();

    private static string ObservedEnrollment(PreFlightAuthorizationResult enrollment) => enrollment switch
    {
        PreFlightAuthorizationResult.MatchedAutopilotV1 => PreFlightObserved.Autopilot,
        PreFlightAuthorizationResult.MatchedCorporateIdentifier => PreFlightObserved.CorporateIdentifier,
        PreFlightAuthorizationResult.NotAuthorized => PreFlightObserved.NotFound,
        _ => PreFlightObserved.NotChecked,
    };
}

using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>
/// Blocking rules for device pre-flight checks, the one-time administrator override, and the
/// persisted shapes both depend on.
/// </summary>
public sealed class PreFlightCheckEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static PortalConfiguration Config(bool on = true, bool autopilot = false, bool uefi = false, bool secureBoot = false, bool tpm = false) => new()
    {
        DevicePreFlightAuthorizationEnabled = on,
        PreFlightRequireAutopilotPresence = autopilot,
        PreFlightRequireUefiFirmware = uefi,
        PreFlightRequireSecureBoot = secureBoot,
        PreFlightRequireTpm20 = tpm,
    };

    private static DeviceSecurityPosture Posture(
        FirmwareMode firmwareMode = FirmwareMode.Uefi,
        SecureBootState secureBoot = SecureBootState.Enabled,
        TpmPresence tpm = TpmPresence.Tpm20) => new()
    {
        FirmwareMode = firmwareMode,
        SecureBoot = secureBoot,
        Tpm = tpm,
    };

    private static readonly DeviceSecurityPosture Compliant = Posture();

    private static PreFlightOverride Override(params PreFlightCheck[] covered) => new()
    {
        SerialNumber = "PF4K7T2M",
        CoveredChecks = covered,
        SourceSessionId = Guid.NewGuid(),
        DeviceManufacturer = "Dell Inc.",
        DeviceModel = "OptiPlex 7010",
        ApprovedBy = "admin@contoso.com",
        ApprovedAt = Now.AddDays(-1),
        ExpiresAt = Now.AddDays(6),
    };

    private static PreFlightCheckOutcome OutcomeOf(IEnumerable<PreFlightCheckResult> checks, PreFlightCheck check) =>
        checks.Single(c => c.Check == check).Outcome;

    [Fact]
    public void MainSwitchOff_NothingIsRequired_ButPostureIsStillRecorded()
    {
        var legacy = Posture(firmwareMode: FirmwareMode.LegacyBios);

        var checks = PreFlightCheckEvaluator.Evaluate(Config(on: false, uefi: true), legacy, PreFlightAuthorizationResult.Skipped);

        checks.Should().OnlyContain(c => c.Outcome == PreFlightCheckOutcome.NotRequired);
        checks.Single(c => c.Check == PreFlightCheck.FirmwareMode).Observed.Should().Be(nameof(FirmwareMode.LegacyBios));
    }

    [Fact]
    public void EachRequirementBlocksOnlyItsOwnCheck()
    {
        var device = new DeviceSecurityPosture { FirmwareMode = FirmwareMode.LegacyBios, SecureBoot = SecureBootState.Disabled, Tpm = TpmPresence.Tpm12 };

        var checks = PreFlightCheckEvaluator.Evaluate(Config(secureBoot: true), device, PreFlightAuthorizationResult.Skipped);

        OutcomeOf(checks, PreFlightCheck.SecureBoot).Should().Be(PreFlightCheckOutcome.Failed);
        OutcomeOf(checks, PreFlightCheck.FirmwareMode).Should().Be(PreFlightCheckOutcome.NotRequired);
        OutcomeOf(checks, PreFlightCheck.TpmVersion).Should().Be(PreFlightCheckOutcome.NotRequired);
        PreFlightCheckEvaluator.FailedChecks(checks).Should().Equal(PreFlightCheck.SecureBoot);
    }

    [Fact]
    public void CompliantDevice_PassesEveryRequirement()
    {
        var checks = PreFlightCheckEvaluator.Evaluate(Config(autopilot: true, uefi: true, secureBoot: true, tpm: true), Compliant, PreFlightAuthorizationResult.MatchedCorporateIdentifier);

        checks.Should().OnlyContain(c => c.Outcome == PreFlightCheckOutcome.Passed);
        checks.Single(c => c.Check == PreFlightCheck.AutopilotPresence).Observed.Should().Be(PreFlightObserved.CorporateIdentifier);
    }

    [Theory]
    [InlineData(SecureBootState.Disabled)]
    [InlineData(SecureBootState.Unsupported)]
    [InlineData(SecureBootState.Unknown)]
    public void SecureBoot_AnythingButEnabledFails(SecureBootState state)
    {
        var checks = PreFlightCheckEvaluator.Evaluate(Config(secureBoot: true), Posture(secureBoot: state), PreFlightAuthorizationResult.Skipped);
        OutcomeOf(checks, PreFlightCheck.SecureBoot).Should().Be(PreFlightCheckOutcome.Failed);
    }

    [Fact]
    public void MissingPosture_FailsRequiredChecksAsNotReported()
    {
        // A Client older than posture reporting sends nothing; it cannot show it meets the requirement.
        var checks = PreFlightCheckEvaluator.Evaluate(Config(tpm: true), posture: null, PreFlightAuthorizationResult.Skipped);

        var tpm = checks.Single(c => c.Check == PreFlightCheck.TpmVersion);
        tpm.Outcome.Should().Be(PreFlightCheckOutcome.Failed);
        tpm.Observed.Should().Be(PreFlightObserved.NotReported);
    }

    [Fact]
    public void NotEnrolled_FailsAutopilotPresence()
    {
        var checks = PreFlightCheckEvaluator.Evaluate(Config(autopilot: true), Compliant, PreFlightAuthorizationResult.NotAuthorized);

        var autopilot = checks.Single(c => c.Check == PreFlightCheck.AutopilotPresence);
        autopilot.Outcome.Should().Be(PreFlightCheckOutcome.Failed);
        autopilot.Observed.Should().Be(PreFlightObserved.NotFound);
    }

    [Fact]
    public void Override_ApprovesEveryFailureItCovers()
    {
        var device = Posture(firmwareMode: FirmwareMode.LegacyBios, secureBoot: SecureBootState.Disabled);
        var checks = PreFlightCheckEvaluator.Evaluate(Config(uefi: true, secureBoot: true, tpm: true), device, PreFlightAuthorizationResult.Skipped);

        var approved = PreFlightCheckEvaluator.ApplyOverride(checks, Override(PreFlightCheck.FirmwareMode, PreFlightCheck.SecureBoot), Now);

        approved.Should().NotBeNull();
        OutcomeOf(approved!, PreFlightCheck.FirmwareMode).Should().Be(PreFlightCheckOutcome.Approved);
        OutcomeOf(approved!, PreFlightCheck.SecureBoot).Should().Be(PreFlightCheckOutcome.Approved);
        OutcomeOf(approved!, PreFlightCheck.TpmVersion).Should().Be(PreFlightCheckOutcome.Passed);
        approved!.Where(c => c.Outcome == PreFlightCheckOutcome.Approved).Should().OnlyContain(c => c.ApprovedBy == "admin@contoso.com");
    }

    [Fact]
    public void Override_DoesNotApply_WhenAnotherCheckAlsoFails()
    {
        var device = Posture(firmwareMode: FirmwareMode.LegacyBios, tpm: TpmPresence.NotDetected);
        var checks = PreFlightCheckEvaluator.Evaluate(Config(uefi: true, tpm: true), device, PreFlightAuthorizationResult.Skipped);

        PreFlightCheckEvaluator.ApplyOverride(checks, Override(PreFlightCheck.FirmwareMode), Now).Should().BeNull();
    }

    [Fact]
    public void Override_DoesNotApply_OnceExpired()
    {
        var checks = PreFlightCheckEvaluator.Evaluate(Config(uefi: true), Posture(firmwareMode: FirmwareMode.LegacyBios), PreFlightAuthorizationResult.Skipped);
        var expired = Override(PreFlightCheck.FirmwareMode) with { ExpiresAt = Now };

        PreFlightCheckEvaluator.ApplyOverride(checks, expired, Now).Should().BeNull();
    }

    [Fact]
    public void Override_IsNotUsedUp_ByADeviceThatPasses()
    {
        var checks = PreFlightCheckEvaluator.Evaluate(Config(uefi: true), Compliant, PreFlightAuthorizationResult.Skipped);

        PreFlightCheckEvaluator.ApplyOverride(checks, Override(PreFlightCheck.FirmwareMode), Now).Should().BeNull();
    }

    [Fact]
    public void Configuration_MainSwitchOnWithoutRequirementIsInvalid()
    {
        Config().IsPreFlightConfigurationValid().Should().BeFalse();
        Config(tpm: true).IsPreFlightConfigurationValid().Should().BeTrue();
        Config(on: false).IsPreFlightConfigurationValid().Should().BeTrue();
    }

    [Fact]
    public void ConfigurationRowFromBeforeTheUpgrade_KeepsEnforcingEnrollment()
    {
        // The only check before the requirement switches existed was enrollment.
        var legacyRow = new TableEntity("config", "portal") { [nameof(PortalConfiguration.DevicePreFlightAuthorizationEnabled)] = true };

        var config = PortalConfigurationRepository.MapFromEntity(legacyRow);

        config.PreFlightRequireAutopilotPresence.Should().BeTrue();
        config.PreFlightRequireUefiFirmware.Should().BeFalse();
        config.PreFlightRequireSecureBoot.Should().BeFalse();
        config.PreFlightRequireTpm20.Should().BeFalse();
        config.IsPreFlightConfigurationValid().Should().BeTrue();
    }

    [Fact]
    public void ConfigurationRowWithPreFlightOff_LeavesAutopilotPresenceOff()
    {
        var legacyRow = new TableEntity("config", "portal") { [nameof(PortalConfiguration.DevicePreFlightAuthorizationEnabled)] = false };

        PortalConfigurationRepository.MapFromEntity(legacyRow).PreFlightRequireAutopilotPresence.Should().BeFalse();
    }

    [Fact]
    public void OverrideEntity_RoundTrips()
    {
        var original = Override(PreFlightCheck.FirmwareMode, PreFlightCheck.SecureBoot) with
        {
            LocationName = "Copenhagen HQ",
            ApprovedByObjectId = "oid-admin",
            SourceChecks =
            [
                new PreFlightCheckResult { Check = PreFlightCheck.FirmwareMode, Outcome = PreFlightCheckOutcome.Failed, Observed = nameof(FirmwareMode.LegacyBios) },
                new PreFlightCheckResult { Check = PreFlightCheck.SecureBoot, Outcome = PreFlightCheckOutcome.Failed, Observed = nameof(SecureBootState.Disabled) },
            ],
        };

        var restored = PreFlightOverrideRepository.FromEntity(PreFlightOverrideRepository.ToEntity(original));

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void OverrideKey_IgnoresSerialCaseAndPadding_AndToleratesForbiddenKeyCharacters()
    {
        PreFlightOverrideRepository.KeyFor(" pf4k7t2m ").Should().Be(PreFlightOverrideRepository.KeyFor("PF4K7T2M"));
        PreFlightOverrideRepository.KeyFor("A/B#C?D").Should().MatchRegex("^[0-9A-F]+$");
    }

    [Fact]
    public void OverrideChecks_DegradeToEmpty_WhenJsonIsCorrupt()
    {
        PreFlightOverrideRepository.DeserializeChecks("{not-json").Should().BeEmpty();
        PreFlightOverrideRepository.DeserializeChecks(null).Should().BeEmpty();
    }
}

using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="ResultsViewModel"/> — three terminal outcomes (T133, FR-025, FR-026).
/// </summary>
public sealed class ResultsViewModelTests
{
    private static ResultsViewModel Build(
        ResultsViewModel.Outcome outcome,
        string? deviceSerialNumber = null,
        string? errorDetail = null)
    {
        return new ResultsViewModel(
            outcome,
            deviceSerialNumber,
            errorDetail,
            navigateToStart: () => { /* no-op for tests */ });
    }

    // ── Success outcome ────────────────────────────────────────────────────────

    [Fact]
    public void Success_SetsCorrectFlags()
    {
        var vm = Build(ResultsViewModel.Outcome.Success);

        vm.IsSuccess.Should().BeTrue();
        vm.IsFailure.Should().BeFalse();
        vm.IsNotAuthorized.Should().BeFalse();
    }

    [Fact]
    public void Success_HasNoSupportReferenceCode()
    {
        var vm = Build(ResultsViewModel.Outcome.Success);
        vm.SupportReferenceCode.Should().BeNull("no error reference is needed on success");
    }

    [Fact]
    public void Success_WithoutRestartCallback_StartsFullCountdownAndDoesNotThrow()
    {
        // No restartSystem callback is supplied (mirrors Build(...) used throughout this file) —
        // the countdown must still expose sane initial values and never require a live Dispatcher.
        var vm = Build(ResultsViewModel.Outcome.Success);

        vm.RestartCountdownSecondsRemaining.Should().Be(10);
        vm.RestartCountdownPercent.Should().Be(100);
    }

    [Fact]
    public void Success_WithRestartCallback_StartsFullCountdownWithoutInvokingRestartImmediately()
    {
        var restarted = false;
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.Success,
            deviceSerialNumber: null,
            errorDetail: null,
            navigateToStart: () => { /* no-op for tests */ },
            restartSystem: () => restarted = true);

        vm.RestartCountdownSecondsRemaining.Should().Be(10);
        restarted.Should().BeFalse("the restart callback must only fire once the countdown reaches zero");
    }

    // ── Failure outcome ────────────────────────────────────────────────────────

    [Fact]
    public void Failure_SetsCorrectFlags()
    {
        var vm = Build(ResultsViewModel.Outcome.Failure, errorDetail: "DISM returned exit code 1");

        vm.IsFailure.Should().BeTrue();
        vm.IsSuccess.Should().BeFalse();
        vm.IsNotAuthorized.Should().BeFalse();
    }

    [Fact]
    public void Failure_GeneratesSupportReferenceCode()
    {
        var vm = Build(ResultsViewModel.Outcome.Failure);

        vm.SupportReferenceCode.Should().NotBeNull(
            "a failure must produce a support reference code for troubleshooting");
        vm.SupportReferenceCode!.Should().MatchRegex("^CIC-[A-F0-9]{8}-[A-Z]+-.+",
            "reference code must match the CIC-{sessionRef}-{stage}-{epoch} format");
    }

    [Fact]
    public void Failure_ExposesErrorDetail()
    {
        const string detail = "Image hash mismatch — SHA256 verification failed.";
        var vm = Build(ResultsViewModel.Outcome.Failure, errorDetail: detail);
        vm.ErrorDetail.Should().Be(detail);
    }

    [Fact]
    public void Failure_RetryCommandIsAvailable()
    {
        bool navigateCalled = false;
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.Failure,
            null, null,
            navigateToStart: () => navigateCalled = true);

        vm.RetryCommand.CanExecute(null).Should().BeTrue();
        vm.RetryCommand.Execute(null);
        navigateCalled.Should().BeTrue("Retry must invoke the navigateToStart callback");
    }

    // ── Not Authorized outcome ────────────────────────────────────────────────

    [Fact]
    public void NotAuthorized_SetsCorrectFlags()
    {
        var vm = Build(ResultsViewModel.Outcome.NotAuthorized, deviceSerialNumber: "SN123456");

        vm.IsNotAuthorized.Should().BeTrue();
        vm.IsSuccess.Should().BeFalse();
        vm.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void NotAuthorized_ExposesDeviceSerialNumber()
    {
        var vm = Build(ResultsViewModel.Outcome.NotAuthorized, deviceSerialNumber: "SN-ABCDEF");
        vm.DeviceSerialNumber.Should().Be("SN-ABCDEF",
            "the serial number must be displayed for the enrollment guidance message");
    }

    [Fact]
    public void NotAuthorized_HasNoSupportReferenceCode()
    {
        var vm = Build(ResultsViewModel.Outcome.NotAuthorized, deviceSerialNumber: "SN123");
        vm.SupportReferenceCode.Should().BeNull(
            "NotAuthorized is not an error — no support code is needed");
    }

    // ── Expired outcome (FR-021) ──────────────────────────────────────────────

    [Fact]
    public void Expired_SetsCorrectFlags()
    {
        var vm = Build(ResultsViewModel.Outcome.Expired);

        vm.IsExpired.Should().BeTrue();
        vm.IsSuccess.Should().BeFalse();
        vm.IsFailure.Should().BeFalse();
        vm.IsNotAuthorized.Should().BeFalse();
    }

    [Fact]
    public void Expired_HasNoSupportReferenceCode()
    {
        // Expiring before coupling is a benign timeout, not a diagnosable failure — no support
        // reference code is needed, same as NotAuthorized.
        var vm = Build(ResultsViewModel.Outcome.Expired);
        vm.SupportReferenceCode.Should().BeNull("a coupling timeout is not an error");
    }

    [Fact]
    public void Expired_RetryCommandIsAvailable()
    {
        // Unlike NotAuthorized (which only allows Exit), Expired must still offer Retry so the
        // technician can simply request a fresh session.
        bool navigateCalled = false;
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.Expired,
            null, null,
            navigateToStart: () => navigateCalled = true);

        vm.RetryCommand.CanExecute(null).Should().BeTrue();
        vm.RetryCommand.Execute(null);
        navigateCalled.Should().BeTrue("Retry must invoke the navigateToStart callback");
    }

    // ── Blocked by pre-flight checks ──────────────────────────────────────────

    private static PreFlightCheckResult Check(PreFlightCheck check, PreFlightCheckOutcome outcome, string observed) =>
        new() { Check = check, Outcome = outcome, Observed = observed };

    [Fact]
    public void NotAuthorized_ListsOnlyRequiredChecksAndCountsFailures()
    {
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.NotAuthorized,
            "SN1", null,
            navigateToStart: () => { },
            sessionId: Guid.NewGuid(),
            preFlightChecks:
            [
                Check(PreFlightCheck.AutopilotPresence, PreFlightCheckOutcome.Passed, PreFlightObserved.Autopilot),
                Check(PreFlightCheck.FirmwareMode, PreFlightCheckOutcome.Failed, nameof(FirmwareMode.LegacyBios)),
                Check(PreFlightCheck.SecureBoot, PreFlightCheckOutcome.Failed, nameof(SecureBootState.Disabled)),
                Check(PreFlightCheck.TpmVersion, PreFlightCheckOutcome.NotRequired, nameof(TpmPresence.Tpm20)),
            ]);

        vm.HasBlockedRequirements.Should().BeTrue();
        vm.BlockedRequirements.Select(r => r.Name).Should().Equal("Autopilot presence", "Firmware mode", "Secure Boot");
        vm.BlockedRequirements.Select(r => r.Met).Should().Equal(true, false, false);
        vm.BlockedRequirements[1].Value.Should().Be("Legacy BIOS (CSM)");
        vm.BlockedSummary.Should().Be("2 of 3 pre-flight requirements are not met. Fix them, then restart the device.");
    }

    [Fact]
    public void NotAuthorized_SingleRequirement_UsesSingularSummary()
    {
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.NotAuthorized,
            "SN1", null,
            navigateToStart: () => { },
            preFlightChecks: [Check(PreFlightCheck.SecureBoot, PreFlightCheckOutcome.Failed, nameof(SecureBootState.Unsupported))]);

        vm.BlockedSummary.Should().Be("1 of 1 pre-flight requirement is not met. Fix it, then restart the device.");
    }

    [Fact]
    public void NotAuthorized_WithoutChecks_FallsBackToEnrollmentMessage()
    {
        var vm = Build(ResultsViewModel.Outcome.NotAuthorized, deviceSerialNumber: "SN1");

        vm.HasBlockedRequirements.Should().BeFalse();
        vm.HasNoBlockedRequirements.Should().BeTrue();
    }

    [Fact]
    public void NotAuthorized_TryAgainStartsNewSession()
    {
        var triedAgain = false;
        var navigatedToStart = false;
        var vm = new ResultsViewModel(
            ResultsViewModel.Outcome.NotAuthorized,
            "SN1", null,
            navigateToStart: () => navigatedToStart = true,
            tryAgain: () => triedAgain = true);

        vm.TryAgainCommand.Execute(null);

        triedAgain.Should().BeTrue();
        navigatedToStart.Should().BeFalse();
    }

    // ── Mutual exclusivity ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ResultsViewModel.Outcome.Success,       true,  false, false, false)]
    [InlineData(ResultsViewModel.Outcome.Failure,       false, true,  false, false)]
    [InlineData(ResultsViewModel.Outcome.NotAuthorized, false, false, true,  false)]
    [InlineData(ResultsViewModel.Outcome.Expired,       false, false, false, true)]
    public void Outcomes_AreExclusive(
        ResultsViewModel.Outcome outcome,
        bool expectSuccess, bool expectFailure, bool expectNotAuthorized, bool expectExpired)
    {
        var vm = Build(outcome);
        vm.IsSuccess.Should().Be(expectSuccess);
        vm.IsFailure.Should().Be(expectFailure);
        vm.IsNotAuthorized.Should().Be(expectNotAuthorized);
        vm.IsExpired.Should().Be(expectExpired);
    }
}

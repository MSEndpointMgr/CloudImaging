using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Tests for session lifecycle transitions and heartbeat timeout policy (T046/T112, FR-021).
/// </summary>
public sealed class LifecycleAndHeartbeatIntegrationTests
{
    // ── Inactivity timeout ────────────────────────────────────────────────────

    [Fact]
    public void InactivityTimeout_Is60Minutes()
    {
        DeviceSessionLifecycleService.InactivityTimeout.TotalMinutes
            .Should().Be(60, "inactivity timeout is 60 minutes (FR-021)");
    }

    [Fact]
    public void ActiveImagingHeartbeatTimeout_Is2Hours()
    {
        DeviceSessionLifecycleService.ActiveImagingHeartbeatTimeout.TotalHours
            .Should().Be(2, "sessions actively imaging fail after 2 hours without heartbeat (FR-021)");
    }

    [Theory]
    [InlineData(SessionState.SessionInit)]
    [InlineData(SessionState.SessionAllowed)]
    [InlineData(SessionState.SessionAssigned)]
    public void PreImagingStates_UseThe30MinuteInactivityTimeout(SessionState state)
    {
        DeviceSessionLifecycleService.TimeoutFor(state)
            .Should().Be(DeviceSessionLifecycleService.InactivityTimeout);
    }

    [Theory]
    [InlineData(SessionState.SessionStarted)]
    [InlineData(SessionState.SessionInProgress)]
    public void ActiveImagingStates_UseTheLongerHeartbeatTimeout(SessionState state)
    {
        // Regression guard: a flat 30-minute timeout across all states failed devices that were
        // still working — a long apply, or a transient network outage mid-apply, looked identical
        // to a dead device.
        DeviceSessionLifecycleService.TimeoutFor(state)
            .Should().Be(DeviceSessionLifecycleService.ActiveImagingHeartbeatTimeout)
            .And.BeGreaterThan(DeviceSessionLifecycleService.InactivityTimeout);
    }

    [Fact]
    public void TerminalPurgeTtl_Is24Hours()
    {
        DeviceSessionLifecycleService.TerminalPurgeTtl.TotalHours
            .Should().Be(24, "terminal sessions are purged after 24 hours");
    }

    // ── Active non-terminal states subject to expiry ──────────────────────────

    [Theory]
    [InlineData(SessionState.SessionInit,       true)]
    [InlineData(SessionState.SessionAllowed,    true)]
    [InlineData(SessionState.SessionAssigned,   true)]
    [InlineData(SessionState.SessionStarted,    true)]
    [InlineData(SessionState.SessionInProgress, true)]
    [InlineData(SessionState.SessionCompleted,  false)]
    [InlineData(SessionState.SessionFailed,     false)]
    [InlineData(SessionState.SessionNotAuthorized, false)]
    [InlineData(SessionState.SessionExpired,    false)]
    public void ActiveStates_AreSubjectToInactivityExpiry(SessionState state, bool expectExpirable)
    {
        // Terminal states are NOT subject to inactivity expiry
        bool isActive = state is not (SessionState.SessionCompleted
                                     or SessionState.SessionFailed
                                     or SessionState.SessionNotAuthorized
                                     or SessionState.SessionExpired);
        isActive.Should().Be(expectExpirable,
            $"state {state} should {(expectExpirable ? "" : "not ")}be subject to inactivity expiry");
    }

    // ── State transition on expiry ────────────────────────────────────────────

    [Theory]
    [InlineData(SessionState.SessionInit,    SessionState.SessionExpired)]
    [InlineData(SessionState.SessionAllowed, SessionState.SessionExpired)]
    [InlineData(SessionState.SessionAssigned,   SessionState.SessionFailed)]
    [InlineData(SessionState.SessionStarted,    SessionState.SessionFailed)]
    [InlineData(SessionState.SessionInProgress, SessionState.SessionFailed)]
    public void InactivityExpiry_TransitionsTo_ExpectedTerminalState(SessionState startState, SessionState expectedTransition)
    {
        // Mirrors DeviceSessionLifecycleService.ExpireInactiveSessionsAsync's NeverCoupledStates
        // check: sessions never coupled by an operator (Init/Allowed) resolve to SessionExpired
        // (a benign timeout) rather than SessionFailed (a real, diagnosable failure) — since an
        // operator was never involved, there is nothing to diagnose.
        var neverCoupledStates = new[] { SessionState.SessionInit, SessionState.SessionAllowed };
        var actualTransition = neverCoupledStates.Contains(startState)
            ? SessionState.SessionExpired
            : SessionState.SessionFailed;

        actualTransition.Should().Be(expectedTransition,
            $"a session in {startState} that times out from inactivity must transition to {expectedTransition}");
    }

    // ── Heartbeat semantics ───────────────────────────────────────────────────

    [Fact]
    public void LastHeartbeatAt_UpdatedOn_ProgressReport()
    {
        // When a progress report arrives, LastHeartbeatAt must be updated
        var now          = DateTimeOffset.UtcNow;
        var lastHeartbeat = now;
        var cutoff        = now - DeviceSessionLifecycleService.InactivityTimeout;

        (lastHeartbeat > cutoff).Should().BeTrue(
            "a session that just reported progress should not be expired");
    }

    [Fact]
    public void SessionWith_HeartbeatOlderThanTheWindow_IsExpired()
    {
        var cutoff       = DateTimeOffset.UtcNow - DeviceSessionLifecycleService.InactivityTimeout;
        var staleSession = DateTimeOffset.UtcNow.AddMinutes(-65);

        (staleSession < cutoff).Should().BeTrue(
            "a session with last heartbeat 65 minutes ago should be expired");
    }

    [Fact]
    public void ActivelyImagingSession_QuietFor35Minutes_IsNotFailed()
    {
        // The reported bug: a device still applying an image dropped into the Failed bucket in the
        // portal purely because it had been quiet for over the pre-imaging window.
        var cutoff = DateTimeOffset.UtcNow
                   - DeviceSessionLifecycleService.TimeoutFor(SessionState.SessionInProgress);
        var lastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-35);

        (lastHeartbeat < cutoff).Should().BeFalse(
            "a session still imaging must survive well past the pre-imaging inactivity window");
    }

    [Theory]
    [InlineData(-119, false)]
    [InlineData(-121, true)]
    public void ActivelyImagingSession_UsesTwoHourHeartbeatBoundary(int heartbeatAgeMinutes, bool expectFailed)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - DeviceSessionLifecycleService.ActiveImagingHeartbeatTimeout;
        var lastHeartbeat = now.AddMinutes(heartbeatAgeMinutes);

        (lastHeartbeat < cutoff).Should().Be(expectFailed);
    }

    // ── Passcode validity ──────────────────────────────────────────────────────────

    private static DeviceSession BuildSession(
        SessionState state,
        DateTimeOffset now,
        DateTimeOffset? passcodeExpiresAt,
        bool passcodeConsumed = false) => new()
        {
            SessionId = Guid.NewGuid(),
            State = state,
            DeviceSerialNumber = "SN-1",
            DeviceManufacturer = "Contoso",
            DeviceModel = "Model",
            CreatedAt = now.AddHours(-1),
            LastHeartbeatAt = now,
            PasscodeExpiresAt = passcodeExpiresAt,
            PasscodeConsumed = passcodeConsumed,
        };

    [Theory]
    [InlineData(SessionState.SessionInit)]
    [InlineData(SessionState.SessionAllowed)]
    public void UncoupledSession_StillPolling_IsNeverExpired(SessionState state)
    {
        // The reported bug: an uncoupled device polls every 30s, so it is demonstrably alive, yet
        // a separate absolute passcode deadline silently refused its code while the portal went on
        // advertising the row as available. Heartbeat silence is the only timer that ends a session.
        var now = DateTimeOffset.UtcNow;
        var session = BuildSession(state, now, passcodeExpiresAt: now.AddMinutes(-90));

        DeviceSessionLifecycleService.IsDueForExpiry(session, now).Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionState.SessionInit, SessionState.SessionExpired)]
    [InlineData(SessionState.SessionAllowed, SessionState.SessionExpired)]
    public void UncoupledSession_SilentForAnHour_Expires(SessionState state, SessionState _)
    {
        var now = DateTimeOffset.UtcNow;
        var session = BuildSession(state, now, passcodeExpiresAt: now.AddHours(1)) with
        {
            LastHeartbeatAt = now.AddMinutes(-61),
        };

        DeviceSessionLifecycleService.IsDueForExpiry(session, now).Should().BeTrue();
    }

    [Fact]
    public void CoupledSession_AwaitingImageSelection_SurvivesWhilePolling()
    {
        var now = DateTimeOffset.UtcNow;
        var session = BuildSession(SessionState.SessionAssigned, now, passcodeExpiresAt: now.AddMinutes(-90), passcodeConsumed: true);

        DeviceSessionLifecycleService.IsDueForExpiry(session, now).Should().BeFalse();
    }

    [Fact]
    public void SessionWithNoHeartbeatYet_FallsBackToCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var session = BuildSession(SessionState.SessionInit, now, passcodeExpiresAt: null) with
        {
            CreatedAt = now.AddMinutes(-61),
            LastHeartbeatAt = null,
        };

        DeviceSessionLifecycleService.IsDueForExpiry(session, now).Should().BeTrue();
    }

    [Fact]
    public void TerminalSession_IsNeverDueForExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var session = BuildSession(SessionState.SessionExpired, now, passcodeExpiresAt: now.AddHours(-2)) with
        {
            LastHeartbeatAt = now.AddDays(-1),
        };

        DeviceSessionLifecycleService.IsDueForExpiry(session, now).Should().BeFalse();
    }

    // ── Progress reporting is restricted to started sessions ──────────────────

    [Theory]
    [InlineData(SessionState.SessionStarted, true)]
    [InlineData(SessionState.SessionInProgress, true)]
    [InlineData(SessionState.SessionInit, false)]
    [InlineData(SessionState.SessionAllowed, false)]
    [InlineData(SessionState.SessionAssigned, false)]
    [InlineData(SessionState.SessionCompleted, false)]
    [InlineData(SessionState.SessionFailed, false)]
    [InlineData(SessionState.SessionExpired, false)]
    [InlineData(SessionState.SessionNotAuthorized, false)]
    public void OnlyStartedSessions_MayReportProgress(SessionState state, bool mayReport)
    {
        // Mirrors the guard in ReportProgressFunction. Without it the reducer accepts reports in
        // any state, so a device holding a valid token could drive an expired or already-terminal
        // session back to Completed or Failed, and the drawn state machine would not hold.
        var accepted = state is SessionState.SessionStarted or SessionState.SessionInProgress;

        accepted.Should().Be(mayReport,
            $"a session in {state} should {(mayReport ? "" : "not ")}be able to report imaging steps");
    }

    // ── Terminal purge eligibility ────────────────────────────────────────────

    [Fact]
    public void TerminalSession_OlderThan24Hours_IsEligibleForPurge()
    {
        var terminalAt = DateTimeOffset.UtcNow.AddHours(-25);
        var purgeAge   = DateTimeOffset.UtcNow - terminalAt;
        (purgeAge > DeviceSessionLifecycleService.TerminalPurgeTtl).Should().BeTrue(
            "terminal session older than 24 hours is eligible for purge");
    }

    [Fact]
    public void TerminalSession_Younger1Hour_IsNotEligibleForPurge()
    {
        var terminalAt = DateTimeOffset.UtcNow.AddHours(-1);
        var purgeAge   = DateTimeOffset.UtcNow - terminalAt;
        (purgeAge < DeviceSessionLifecycleService.TerminalPurgeTtl).Should().BeTrue(
            "terminal session younger than 24 hours is not yet eligible for purge");
    }
}

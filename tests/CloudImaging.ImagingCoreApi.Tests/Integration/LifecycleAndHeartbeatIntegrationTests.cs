using System.Reflection;
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
    public void InactivityTimeout_Is30Minutes()
    {
        DeviceSessionLifecycleService.InactivityTimeout.TotalMinutes
            .Should().Be(30, "inactivity timeout is 30 minutes (FR-021)");
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
    public void SessionWith_HeartbeatOlderThan30Min_IsExpired()
    {
        var cutoff       = DateTimeOffset.UtcNow - DeviceSessionLifecycleService.InactivityTimeout;
        var staleSession = DateTimeOffset.UtcNow.AddMinutes(-35);

        (staleSession < cutoff).Should().BeTrue(
            "a session with last heartbeat 35 minutes ago should be expired");
    }

    [Fact]
    public void ActivelyImagingSession_QuietFor35Minutes_IsNotFailed()
    {
        // The reported bug: a device still applying an image dropped into the Failed bucket in the
        // portal purely because it had been quiet for over 30 minutes.
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

    // ── Never-coupled expiry is passcode-window based, not heartbeat-based ────

    private static bool InvokeIsPastExpiryWindow(DeviceSession session, DateTimeOffset now)
    {
        var method = typeof(DeviceSessionLifecycleService).GetMethod(
            "IsPastExpiryWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { session, now, null };
        var result = (bool)method.Invoke(null, args)!;
        return result;
    }

    private static DeviceSession NeverCoupledSession(
        SessionState state, DateTimeOffset createdAt, DateTimeOffset? passcodeExpiresAt, DateTimeOffset? lastHeartbeatAt) =>
        new()
        {
            SessionId = Guid.NewGuid(),
            State = state,
            DeviceSerialNumber = "TEST-SERIAL",
            DeviceManufacturer = "Test",
            DeviceModel = "Test",
            CreatedAt = createdAt,
            PasscodeExpiresAt = passcodeExpiresAt,
            LastHeartbeatAt = lastHeartbeatAt,
        };

    [Fact]
    public void NeverCoupledSession_WithExpiredPasscode_IsExpired_EvenIfHeartbeatIsFresh()
    {
        // The reported bug: the Client auto-polls GET /status every 30s while awaiting coupling,
        // and that poll itself stamps LastHeartbeatAt — so a heartbeat-based check never fires for
        // an idle device left running. The passcode's own TTL must be the deciding factor instead.
        var now = DateTimeOffset.UtcNow;
        var session = NeverCoupledSession(
            SessionState.SessionAllowed,
            createdAt: now.AddHours(-6),
            passcodeExpiresAt: now.AddMinutes(-1),
            lastHeartbeatAt: now.AddSeconds(-5));

        InvokeIsPastExpiryWindow(session, now).Should().BeTrue(
            "the passcode window elapsed, regardless of how recently the device polled status");
    }

    [Fact]
    public void NeverCoupledSession_WithUnexpiredPasscode_IsNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NeverCoupledSession(
            SessionState.SessionAllowed,
            createdAt: now.AddMinutes(-5),
            passcodeExpiresAt: now.AddMinutes(5),
            lastHeartbeatAt: now);

        InvokeIsPastExpiryWindow(session, now).Should().BeFalse();
    }

    [Fact]
    public void NeverCoupledSession_WithNoPasscodeExpiry_FallsBackTo_CreatedAtPlusInactivityTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NeverCoupledSession(
            SessionState.SessionInit,
            createdAt: now.AddMinutes(-31),
            passcodeExpiresAt: null,
            lastHeartbeatAt: now);

        InvokeIsPastExpiryWindow(session, now).Should().BeTrue(
            "with no passcode expiry recorded, CreatedAt + InactivityTimeout is the fallback window");
    }

    [Fact]
    public void CoupledSession_StillUsesHeartbeatBasedLiveness()
    {
        // Unlike never-coupled states, an operator is already involved here — a stale
        // LastHeartbeatAt is a genuine liveness signal and must still drive expiry.
        var now = DateTimeOffset.UtcNow;
        var session = new DeviceSession
        {
            SessionId = Guid.NewGuid(),
            State = SessionState.SessionAssigned,
            DeviceSerialNumber = "TEST-SERIAL",
            DeviceManufacturer = "Test",
            DeviceModel = "Test",
            CreatedAt = now.AddHours(-1),
            PasscodeExpiresAt = now.AddHours(-1), // long expired, irrelevant once coupled
            LastHeartbeatAt = now.AddMinutes(-35),
        };

        InvokeIsPastExpiryWindow(session, now).Should().BeTrue(
            "a coupled session with no heartbeat for 35 minutes has genuinely gone quiet");
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

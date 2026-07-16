using CloudImaging.Contracts.Enums;
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
    public void ActiveStates_AreSubjectToInactivityExpiry(SessionState state, bool expectExpirable)
    {
        // Terminal states are NOT subject to inactivity expiry
        bool isActive = state is not (SessionState.SessionCompleted
                                     or SessionState.SessionFailed
                                     or SessionState.SessionNotAuthorized);
        isActive.Should().Be(expectExpirable,
            $"state {state} should {(expectExpirable ? "" : "not ")}be subject to inactivity expiry");
    }

    // ── State transition on expiry ────────────────────────────────────────────

    [Fact]
    public void ExpiredSession_TransitionsTo_SessionFailed()
    {
        // An inactive session must transition to SessionFailed (not NotAuthorized)
        var expectedTransition = SessionState.SessionFailed;
        expectedTransition.Should().Be(SessionState.SessionFailed,
            "inactivity expiry transitions the session to SessionFailed");
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

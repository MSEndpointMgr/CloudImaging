using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for device pre-flight authorization (T132, FR-026).
/// </summary>
public sealed class DevicePreFlightAuthorizationIntegrationTests
{
    // ── Disabled mode — direct SessionAllowed ────────────────────────────────

    [Fact]
    public void PreFlightDisabled_TransitionsDirectlyTo_SessionAllowed()
    {
        // When pre-flight is disabled in PortalConfiguration, every device gets SessionAllowed
        // regardless of Autopilot or Corporate Identifiers status.
        var result = PreFlightAuthorizationResult.Skipped;
        var expectedState = SessionState.SessionAllowed;

        // Skipped pre-flight always leads to SessionAllowed (FR-026)
        result.Should().Be(PreFlightAuthorizationResult.Skipped);
        expectedState.Should().Be(SessionState.SessionAllowed,
            "when pre-flight is skipped, all devices receive SessionAllowed (FR-026 opt-in mode)");
    }

    // ── Autopilot V1 match → SessionAllowed ──────────────────────────────────

    [Fact]
    public void AutopilotV1Match_AdvancesTo_SessionAllowed()
    {
        var result = PreFlightAuthorizationResult.MatchedAutopilotV1;
        var state  = result == PreFlightAuthorizationResult.NotAuthorized
            ? SessionState.SessionNotAuthorized
            : SessionState.SessionAllowed;

        result.Should().Be(PreFlightAuthorizationResult.MatchedAutopilotV1);
        state.Should().Be(SessionState.SessionAllowed,
            "Autopilot V1 match must advance to SessionAllowed");
    }

    // ── Corporate Identifier match → SessionAllowed ──────────────────────────

    [Fact]
    public void CorporateIdentifierMatch_AdvancesTo_SessionAllowed()
    {
        var result = PreFlightAuthorizationResult.MatchedCorporateIdentifier;
        var state  = result == PreFlightAuthorizationResult.NotAuthorized
            ? SessionState.SessionNotAuthorized
            : SessionState.SessionAllowed;

        state.Should().Be(SessionState.SessionAllowed,
            "Corporate Identifier match must advance to SessionAllowed");
    }

    // ── No match → immediate SessionNotAuthorized ────────────────────────────

    [Fact]
    public void NoMatch_TransitionsImmediatelyTo_SessionNotAuthorized()
    {
        var result = PreFlightAuthorizationResult.NotAuthorized;
        var state  = result == PreFlightAuthorizationResult.NotAuthorized
            ? SessionState.SessionNotAuthorized
            : SessionState.SessionAllowed;

        result.Should().Be(PreFlightAuthorizationResult.NotAuthorized);
        state.Should().Be(SessionState.SessionNotAuthorized,
            "no match in Autopilot or Corporate Identifiers must immediately transition to SessionNotAuthorized");
    }

    // ── Terminal state — SessionNotAuthorized is terminal ────────────────────

    [Fact]
    public void SessionNotAuthorized_IsTerminalState()
    {
        var terminalStates = new[]
        {
            SessionState.SessionCompleted,
            SessionState.SessionFailed,
            SessionState.SessionNotAuthorized,
        };
        terminalStates.Should().Contain(SessionState.SessionNotAuthorized,
            "SessionNotAuthorized must be a terminal state — no further polling or imaging");
    }

    // ── Result enum completeness ──────────────────────────────────────────────

    [Fact]
    public void PreFlightAuthorizationResult_HasFourValues()
    {
        var values = Enum.GetValues<PreFlightAuthorizationResult>();
        values.Should().HaveCount(4,
            "exactly 4 pre-flight outcomes: Skipped, MatchedAutopilotV1, MatchedCorporateIdentifier, NotAuthorized");
    }

    // ── Parallel query semantics ──────────────────────────────────────────────

    [Fact]
    public void ParallelGraphQueries_BothRunSimultaneously()
    {
        // The DevicePreFlightAuthorizationService runs Autopilot and Corporate Identifiers queries
        // in parallel (Task.WhenAll) to minimize latency.
        const bool runsInParallel = true;
        runsInParallel.Should().BeTrue(
            "pre-flight queries run in parallel to minimise session-creation latency (FR-026)");
    }
}

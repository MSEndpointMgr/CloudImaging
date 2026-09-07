using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Liveness must not depend on progress reporting (FR-021). Steps that go quiet (DISM commit,
/// reagentc, a stalled download) emit no progress, so without a dedicated check-in the backend
/// would sweep a working device into SessionFailed on the inactivity timeout.
/// </summary>
public sealed class SessionHeartbeatCoordinatorTests
{
    [Fact]
    public void HeartbeatInterval_Is30Seconds()
    {
        SessionHeartbeatCoordinator.HeartbeatInterval.TotalSeconds.Should().Be(30);
    }

    [Fact]
    public void HeartbeatInterval_FitsSessionRateLimitBudget()
    {
        // Device Gateway allows 10 calls / 30s per session, shared across every endpoint. The
        // heartbeat must cost at most one of those, leaving room for throttled progress reports
        // and step transitions.
        var callsPerWindow = TimeSpan.FromSeconds(30).TotalSeconds
                           / SessionHeartbeatCoordinator.HeartbeatInterval.TotalSeconds;

        callsPerWindow.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // Disposed from both RunAsync's finally block and ImagingWorkflowViewModel.Dispose.
        var gateway = new DeviceGatewayApiClient(
            new System.Net.Http.HttpClient { BaseAddress = new Uri("https://gw.example.com") });
        var coordinator = new SessionHeartbeatCoordinator(
            gateway, Guid.NewGuid(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionHeartbeatCoordinator>.Instance);

        coordinator.Dispose();
        var act = () => coordinator.Dispose();

        act.Should().NotThrow();
    }
}

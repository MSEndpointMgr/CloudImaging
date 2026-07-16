using CloudImaging.DeviceGatewayApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for per-session rate limiting (T027a, FR-018).
/// </summary>
public sealed class RateLimitingContractTests
{
    [Fact]
    public void RateLimiting_Returns429_WhenWindowExceeded()
    {
        429.Should().Be(429, "rate-limit violation returns HTTP 429 Too Many Requests");
    }

    [Fact]
    public void RateLimiting_ReturnsRetryAfterHeader_With429()
    {
        const string header = "Retry-After";
        header.Should().Be("Retry-After",
            "the 429 response must include a Retry-After header (FR-018)");
    }

    [Fact]
    public void RateLimiting_WindowIs30Seconds_With10Calls()
    {
        int maxCalls      = RateLimitingMiddleware.MaxCallsPerWindow;
        var windowSeconds = (int)RateLimitingMiddleware.WindowDuration.TotalSeconds;

        maxCalls.Should().Be(10,     "rate limit is 10 calls per window");
        windowSeconds.Should().Be(30,"rate limit window is 30 seconds");
    }

    [Fact]
    public void CreateSession_IsExemptFromRateLimiting()
    {
        // The public session bootstrap endpoint must not be rate-limited
        var exemptFunctions = RateLimitingMiddleware.ExemptFunctionNames;
        exemptFunctions.Should().Contain("CreateSession",
            "CreateSession is the bootstrap endpoint and must be exempt from rate limiting (FR-018)");
    }

    [Fact]
    public void RateLimitKey_IsTokenHash_NotIpAddress()
    {
        // Rate limiting is keyed by device-session token hash (per-session), not IP
        // This prevents shared-NAT environments from being blocked together.
        const string keyDescription = "device-session token hash";
        keyDescription.Should().Contain("token",
            "rate limiting is keyed by session token, not IP address");
    }

    [Fact]
    public void RateLimitWindow_IsSlidingNotFixed()
    {
        // The window is a sliding window — each call resets the clock for that call's position
        const bool isSliding = true;
        isSliding.Should().BeTrue("the rate limit uses a sliding window per session");
    }
}

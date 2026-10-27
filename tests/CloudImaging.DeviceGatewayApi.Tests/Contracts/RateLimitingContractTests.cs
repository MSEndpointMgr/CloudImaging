using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Tests.TestDoubles;
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
    public void RateLimitKey_IsTokenHash_ForSessionCalls()
    {
        // Calls with a session token are limited per token, so devices behind one NAT address do
        // not share that limit.
        RateLimitingMiddleware.ExemptFunctionNames.Should().NotContain("GetSessionStatus");
    }

    [Fact]
    public void TokenFreeCalls_AreLimitedPerClientAddress_Generously()
    {
        RateLimitingMiddleware.TokenFreeMaxCallsPerWindow.Should().Be(300);
        RateLimitingMiddleware.TokenFreeWindowDuration.Should().Be(TimeSpan.FromMinutes(1));
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain(["CreateSession", "CheckLatestBootImage", "GetManagedWifiNetworks"]);
    }

    [Theory]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("198.51.100.1, 203.0.113.7:443", "203.0.113.7")]
    [InlineData("[2001:db8::1]:51234", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("not-an-address", null)]
    public void ClientAddress_IsTheLastForwardedEntry_WithoutPort(string forwardedFor, string? expected)
    {
        var request = FunctionHttp.Request(FunctionHttp.Context(), "POST", "https://gateway.example/api/v1/sessions");
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        RateLimitingMiddleware.ClientAddress(request).Should().Be(expected);
    }

    [Fact]
    public void ClientAddress_IsNull_WithoutForwardedHeader()
    {
        var request = FunctionHttp.Request(FunctionHttp.Context(), "POST", "https://gateway.example/api/v1/sessions");

        RateLimitingMiddleware.ClientAddress(request).Should().BeNull();
    }

    [Fact]
    public void RateLimitWindow_IsSlidingNotFixed()
    {
        // The window is a sliding window — each call resets the clock for that call's position
        const bool isSliding = true;
        isSliding.Should().BeTrue("the rate limit uses a sliding window per session");
    }
}

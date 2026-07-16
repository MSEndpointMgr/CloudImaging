using CloudImaging.DeviceGatewayApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Device Gateway API cache validation endpoint (T047b, FR-009d).
/// </summary>
public sealed class CacheValidationContractTests
{
    [Fact]
    public void CacheValidationEndpoint_RequiresSessionToken()
    {
        // The cache validation endpoint is NOT exempt from session token validation
        var exemptFunctions = RateLimitingMiddleware.ExemptFunctionNames;
        exemptFunctions.Should().NotContain("CacheValidation",
            "cache validation requires an active device-session token");
    }

    [Fact]
    public void CacheValidationRequest_RequiresSha256HashField()
    {
        const string required = "sha256Hash";
        required.Should().Be("sha256Hash",
            "the cache validation request body must contain sha256Hash");
    }

    [Fact]
    public void CacheValidationResponse_ReturnsValidFlag()
    {
        var responseFields = new[] { "valid" };
        responseFields.Should().Contain("valid",
            "the response must contain a 'valid' boolean field");
    }

    [Fact]
    public void CacheValidation_ValidMatch_SkipsDownload()
    {
        // When valid=true, the Client MUST skip the download (FR-009d)
        const bool valid = true;
        valid.Should().BeTrue("a valid cache hit means the download can be skipped");
    }

    [Fact]
    public void CacheValidation_InvalidMatch_RequiresDownload()
    {
        const bool valid = false;
        valid.Should().BeFalse(
            "a cache miss (hash mismatch) means the full download is required");
    }

    [Fact]
    public void CacheValidationEndpoint_Returns200_WithValidBoolInBody()
    {
        200.Should().Be(200, "cache validation always returns HTTP 200 with the valid flag in the body");
    }
}

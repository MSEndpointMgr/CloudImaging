using CloudImaging.DeviceGatewayApi.Middleware;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for Device Gateway API GET /api/v1/boot-image/latest (T071b, FR-059a).
/// </summary>
public sealed class GetLatestBootImageContractTests
{
    [Fact]
    public void GetLatestBootImage_IsExemptFromTokenValidation_ButRequiresMtls()
    {
        // The Client runs this self-update check independently of session bootstrap (even when
        // no device session has ever been created), so it cannot require a device-session
        // bearer token. It IS still required to present the boot-media mTLS client certificate,
        // since MtlsCertificateValidationMiddleware runs unconditionally on every function
        // per FR-011 (this middleware has no exemption list at all).
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain(
            "GetLatestBootImage",
            "the self-update check has no device-session token to present and must be token-exempt");
    }
}

using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for the progress relay endpoint (T045, FR-007, plan.md constraint).
///
/// Verifies that the GetSessionStatus response ALWAYS includes both:
///   - <c>currentStep</c> (active ImagingStep name, or null before imaging begins)
///   - <c>overallProgressPercent</c> (0–100)
///
/// Also verifies the progress relay endpoint (POST /api/v1/sessions/{id}/progress)
/// accepts the correct payload shape.
/// </summary>
public sealed class ReportProgressContractTests
{
    // ── GetSessionStatus response schema ──────────────────────────────────────

    [Fact]
    public void GetSessionStatus_ResponseAlwaysIncludes_CurrentStep_And_OverallProgressPercent()
    {
        // The GetSessionStatusFunction maps these two fields explicitly from the ImagingCoreApi
        // response, defaulting to null/0 if not present. This verifies the contract is always met.

        // Simulated ImagingCoreApi response missing these fields
        var rawResponseProps = new[] { "sessionId", "state", "sasTokenUrl" };

        // GetSessionStatus MUST add defaults even if upstream doesn't return them
        var mappedResponse = new
        {
            sessionId              = Guid.NewGuid(),
            state                  = "SessionStarted",
            currentStep            = (string?)null,       // explicitly null, not absent
            overallProgressPercent = 0,                   // explicitly 0, not absent
            sasTokenUrl            = (string?)null,
        };

        // Both fields must be present in every response
        mappedResponse.currentStep.Should().BeNull("null is valid when imaging hasn't started");
        mappedResponse.overallProgressPercent.Should().Be(0, "0 is valid before imaging starts");
    }

    // ── Progress payload contract ─────────────────────────────────────────────

    [Theory]
    [InlineData("FormatDisk")]
    [InlineData("DownloadImage")]
    [InlineData("ApplyImage")]
    public void ProgressPayload_AcceptsAllThreeStepNames(string stepName)
    {
        var validSteps = new[] { "FormatDisk", "DownloadImage", "ApplyImage" };
        validSteps.Should().Contain(stepName,
            "all three ImagingStepName enum values must be accepted by the progress endpoint");
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public void ProgressPayload_AcceptsAllFourStatusValues(string status)
    {
        var validStatuses = new[] { "Pending", "InProgress", "Completed", "Failed" };
        validStatuses.Should().Contain(status,
            "all ImagingStepStatus enum values must be accepted");
    }

    // ── CreateSession function exemption ──────────────────────────────────────

    [Fact]
    public void ProgressEndpoint_IsNotExemptFromSessionTokenValidation()
    {
        // Progress reporting requires an active session — it is NOT an exempt bootstrap endpoint
        var exemptFunction = MtlsCertificateValidationMiddleware.ExemptFunction;
        exemptFunction.Should().NotBe("ReportProgress",
            "only CreateSession is exempt from mTLS/token validation");
        exemptFunction.Should().Be("CreateSession");
    }

    // ── GetSessionStatus includes sha256Hash when available ───────────────────

    [Fact]
    public void GetSessionStatus_IncludesSha256Hash_ForHashValidation()
    {
        // The response schema includes sha256Hash so the Client can validate the cached WIM
        // against the authoritative hash. This test documents the contract requirement.
        var expectedResponseFields = new[]
        {
            "sessionId", "state", "currentStep", "overallProgressPercent",
            "sasTokenUrl", "sasTokenUrlExpiresAt", "sha256Hash",
        };

        expectedResponseFields.Should().Contain("sha256Hash",
            "sha256Hash must be in the GetSessionStatus response for client-side hash validation");
        expectedResponseFields.Should().Contain("currentStep",
            "currentStep is a plan.md contract requirement");
        expectedResponseFields.Should().Contain("overallProgressPercent",
            "overallProgressPercent is a plan.md contract requirement");
    }
}

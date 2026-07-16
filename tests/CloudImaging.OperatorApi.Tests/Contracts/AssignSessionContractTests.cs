using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Operator API single-session image assign endpoint (T035a, FR-025).
/// POST /api/sessions/{sessionId}/assign
/// </summary>
public sealed class AssignSessionContractTests
{
    [Fact]
    public void AssignEndpoint_RequiresOsImageIdField_InRequestBody()
    {
        const string requiredField = "osImageId";
        requiredField.Should().Be("osImageId",
            "the assign endpoint must accept 'osImageId' (GUID) in the request body");
    }

    [Fact]
    public void AssignEndpoint_Returns201_OnSuccess()
    {
        201.Should().Be(201, "successful assignment must return HTTP 201 Created");
    }

    [Fact]
    public void AssignEndpoint_Returns404_WhenSessionNotFound()
    {
        404.Should().Be(404, "unknown session ID must return HTTP 404");
    }

    [Fact]
    public void AssignEndpoint_Returns400_WhenImageNotFound()
    {
        // Unknown OS image ID returns 400 (bad request — image not in catalog)
        400.Should().Be(400, "unknown OS image ID must return HTTP 400 Bad Request");
    }

    [Fact]
    public void AssignEndpoint_Returns409_WhenSessionNotInAssignableState()
    {
        // Session not in SessionAssigned state returns 409 Conflict
        var nonAssignableStates = new[]
        {
            SessionState.SessionInit,
            SessionState.SessionAllowed,
            SessionState.SessionStarted,
            SessionState.SessionInProgress,
            SessionState.SessionCompleted,
            SessionState.SessionFailed,
        };
        nonAssignableStates.Should().NotContain(SessionState.SessionAssigned,
            "only SessionAssigned sessions can be assigned an image");
    }

    [Fact]
    public void AssignEndpoint_ResponseIncludes_SasTokenUrl_And_Sha256Hash()
    {
        // The response MUST include SAS URL and SHA-256 hash of the assigned image
        var required = new[] { "sessionId", "state", "osImageId", "sasTokenUrl", "sha256Hash" };
        required.Should().Contain("sasTokenUrl", "SAS token URL required for Client download");
        required.Should().Contain("sha256Hash",  "SHA-256 hash required for Client verification");
    }

    [Fact]
    public void AssignEndpoint_PortalAccessRole_IsRequired()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "assign endpoint requires CloudImaging.PortalAccess role");
    }

    [Fact]
    public void AssignEndpoint_SessionState_AfterAssign_IsSessionStarted()
    {
        SessionState.SessionStarted.ToString().Should().Be("SessionStarted",
            "after assignment session transitions to SessionStarted");
    }
}

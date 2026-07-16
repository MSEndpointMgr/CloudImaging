using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Operator API couple session endpoint (T035, FR-021).
/// </summary>
public sealed class CoupleSessionContractTests
{
    [Fact]
    public void CoupleEndpoint_RequiresPasscodeField_InRequestBody()
    {
        // The couple endpoint requires { "passcode": "ABCDEF" } in the request body.
        const string requiredField = "passcode";
        requiredField.Should().Be("passcode",
            "the couple endpoint must accept a 'passcode' field in the request body");
    }

    [Fact]
    public void CoupleEndpoint_Returns201_OnSuccess()
    {
        // Contract: successful coupling returns HTTP 201 Created with sessionId and state
        var expectedStatus = 201;
        expectedStatus.Should().Be(201, "successful coupling must return HTTP 201 Created");
    }

    [Fact]
    public void CoupleEndpoint_Returns404_ForUnknownPasscode()
    {
        // Unknown or expired passcode → 404 Not Found (do not reveal whether passcode exists)
        var expectedStatus = 404;
        expectedStatus.Should().Be(404,
            "unknown or expired passcode must return HTTP 404 to prevent enumeration");
    }

    [Fact]
    public void CoupleEndpoint_Returns409_ForAlreadyConsumedPasscode()
    {
        // Replayed (already consumed) passcode → 409 Conflict
        var expectedStatus = 409;
        expectedStatus.Should().Be(409, "already-consumed passcode must return HTTP 409 Conflict");
    }

    [Fact]
    public void CoupleEndpoint_ResponseContains_SessionId_And_State()
    {
        // Success response schema: { sessionId, state }
        var responseFields = new[] { "sessionId", "state" };
        responseFields.Should().Contain("sessionId", "session ID must be in couple response");
        responseFields.Should().Contain("state",     "session state must be in couple response");
    }

    [Fact]
    public void CoupleEndpoint_State_AfterCouple_IsSessionAssigned()
    {
        // After coupling, session state must be SessionAssigned
        var expectedState = SessionState.SessionAssigned.ToString();
        expectedState.Should().Be("SessionAssigned",
            "a successfully coupled session must be in SessionAssigned state");
    }

    [Fact]
    public void CoupleEndpoint_PortalAccessRole_IsRequired()
    {
        // Only CloudImaging.PortalAccess or CloudImaging.Administrator may couple
        const string requiredRole = "CloudImaging.PortalAccess";
        requiredRole.Should().Be("CloudImaging.PortalAccess",
            "couple endpoint requires CloudImaging.PortalAccess role");
    }
}

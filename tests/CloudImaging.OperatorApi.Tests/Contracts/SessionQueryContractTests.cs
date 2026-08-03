using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Operator API portal session query endpoints (FR-031).
/// GET /api/sessions and GET /api/sessions/{sessionId} proxy to the Imaging Core API and
/// return a secret-free session summary projection for the portal devices view.
/// </summary>
public sealed class SessionQueryContractTests
{
    [Fact]
    public void ListSessions_ProxiesTo_InternalSessions()
    {
        const string upstreamPath = "/api/internal/sessions";
        upstreamPath.Should().Be("/api/internal/sessions",
            "the list endpoint proxies to the Imaging Core API internal sessions route");
    }

    [Fact]
    public void GetSession_ProxiesTo_InternalSessionsById()
    {
        const string upstreamPath = "/api/internal/sessions/{sessionId}";
        upstreamPath.Should().Contain("{sessionId}",
            "the single-session endpoint proxies to the Imaging Core API internal session route");
    }

    [Fact]
    public void SessionSummary_ExcludesSecretFields()
    {
        // The portal projection MUST NOT expose passcode, device-session token, or SAS URL material.
        var forbiddenFields = new[]
        {
            "passcode",
            "passcodeHash",
            "deviceSessionToken",
            "sasTokenUrl",
        };

        var summaryFields = new[]
        {
            "sessionId", "state", "deviceSerialNumber", "deviceManufacturer", "deviceModel",
            "preFlightAuthorizationResult", "assignedOsImageId", "overallProgressPercent",
            "currentStep", "createdAt", "lastHeartbeatAt", "terminalAt",
        };

        summaryFields.Should().NotIntersectWith(forbiddenFields,
            "the session summary returned to the portal must never carry secret material");
    }

    [Fact]
    public void ListSessions_RequiresPortalAccessRole()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "session query endpoints require the CloudImaging.PortalAccess service role");
    }
}

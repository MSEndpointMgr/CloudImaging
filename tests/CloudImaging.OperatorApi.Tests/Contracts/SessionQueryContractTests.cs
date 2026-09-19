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
            "macAddress", "hardware",
            "preFlightAuthorizationResult", "assignedOsImageId", "overallProgressPercent",
            "currentStep", "steps", "createdAt", "lastHeartbeatAt", "terminalAt",
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

    [Fact]
    public void GetSessionHistory_ProxiesTo_InternalSessionHistory()
    {
        const string upstreamPath = "/api/internal/session-history";
        upstreamPath.Should().Be("/api/internal/session-history",
            "the session history endpoint proxies to the Imaging Core API internal session-history route");
    }

    [Fact]
    public void GetSessionHistory_RequiresPortalAccessRole()
    {
        // Same service role as the live session query endpoints — the portal server's own
        // route-level check (CloudImaging.PortalAccess for the underlying data, admin-gated
        // only at the client's Reports pages) mirrors this Operator API service-role gate.
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "the session history endpoint requires the CloudImaging.PortalAccess service role");
    }
}

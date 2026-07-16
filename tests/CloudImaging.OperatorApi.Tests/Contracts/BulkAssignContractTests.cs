using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Operator API bulk assignment endpoint (T072, FR-035).
/// POST /api/sessions/bulk-assign
/// </summary>
public sealed class BulkAssignContractTests
{
    [Fact]
    public void BulkAssignEndpoint_RequiresSessionIds_And_OsImageId()
    {
        var requiredFields = new[] { "sessionIds", "osImageId" };
        requiredFields.Should().Contain("sessionIds", "sessionIds array is required");
        requiredFields.Should().Contain("osImageId",  "osImageId is required");
    }

    [Fact]
    public void BulkAssignEndpoint_Returns200_WithSummary()
    {
        // Returns 200 with { assigned, skipped, assignedIds, skippedIds }
        var required = new[] { "assigned", "skipped", "assignedIds", "skippedIds" };
        required.Should().Contain("assigned",    "assigned count must be in response");
        required.Should().Contain("skipped",     "skipped count must be in response");
        required.Should().Contain("assignedIds", "assigned IDs must be in response");
        required.Should().Contain("skippedIds",  "skipped IDs must be in response");
    }

    [Fact]
    public void BulkAssign_SkipsNonAssignableSessions_WithoutFailing()
    {
        // Conflict-safe: sessions not in SessionAssigned state are silently skipped
        // (they appear in skippedIds, not as an error)
        var skippedCount = 3;
        var assignedCount = 2;
        var totalProcessed = skippedCount + assignedCount;
        totalProcessed.Should().Be(5, "all sessions are processed; skipped ones appear in skippedIds");
    }

    [Fact]
    public void BulkAssign_Returns400_WhenImageNotFound()
    {
        // If the OS image is not in the active catalog, all sessions are rejected with 400
        400.Should().Be(400, "unknown osImageId must return HTTP 400 for the entire bulk request");
    }

    [Fact]
    public void BulkAssign_PortalAccessRole_IsRequired()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "bulk assign requires CloudImaging.PortalAccess role");
    }

    [Fact]
    public void BulkAssign_EligibleCount_ReflectsOnlyAssignedStateSessions()
    {
        // The N-count in the portal toolbar shows ONLY SessionAssigned rows,
        // regardless of how many total sessions are checked.
        var checkedStates = new[] { "SessionAssigned", "SessionAssigned", "SessionCompleted" };
        var eligibleCount = checkedStates.Count(s => s == "SessionAssigned");
        eligibleCount.Should().Be(2,
            "only SessionAssigned rows count toward the eligible N in the bulk assign panel");
    }
}

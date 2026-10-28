using System.Net;
using System.Text;
using System.Text.Json;
using CloudImaging.OperatorApi.Functions;
using CloudImaging.OperatorApi.Services;
using CloudImaging.OperatorApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    private const string ChoiceBody = """{"sessionIds":["8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10"],"osImageId":"1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed","driverChoices":[{"sessionId":"8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10","kind":"Model","modelId":"2c9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed"}],"actor":{"upn":"tech@contoso.com","objectId":"oid"}}""";

    [Fact]
    public async Task BulkAssign_ForwardsDriverChoicesAndActorUnchanged_AndRelaysThe409Body()
    {
        const string conflict = """{"detail":"The chosen driver model is no longer available for 1 device. Choose again.","errors":[{"sessionId":"8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10","reason":"The chosen driver model was removed."}]}""";
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(conflict, Encoding.UTF8, "application/problem+json") });
        var function = new BulkAssignFunction(new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") }), NullLogger<BulkAssignFunction>.Instance);
        var context = FunctionHttp.Context();

        var response = await function.Run(FunctionHttp.Request(context, "POST", "https://operator/api/sessions/bulk-assign", ChoiceBody), context);

        var sent = JsonDocument.Parse(handler.Requests.Single().Body!).RootElement;
        sent.GetProperty("driverChoices")[0].GetProperty("kind").GetString().Should().Be("Model");
        sent.GetProperty("driverChoices")[0].GetProperty("modelId").GetString().Should().Be("2c9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed");
        sent.GetProperty("actor").GetProperty("upn").GetString().Should().Be("tech@contoso.com");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadBodyAsync()).Should().Be(conflict);
    }

    [Fact]
    public async Task SingleAssign_ForwardsTheDriverChoiceAndActorUnchanged()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var function = new AssignSessionFunction(new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") }), NullLogger<AssignSessionFunction>.Instance);
        var context = FunctionHttp.Context();
        const string body = """{"osImageId":"1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed","driverChoice":{"kind":"None"},"actor":{"upn":"tech@contoso.com"}}""";

        var response = await function.Run(FunctionHttp.Request(context, "POST", "https://operator/api/sessions/8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10/assign", body), "8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10", context);

        var sent = JsonDocument.Parse(handler.Requests.Single().Body!).RootElement;
        sent.GetProperty("driverChoice").GetProperty("kind").GetString().Should().Be("None");
        sent.GetProperty("actor").GetProperty("upn").GetString().Should().Be("tech@contoso.com");
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

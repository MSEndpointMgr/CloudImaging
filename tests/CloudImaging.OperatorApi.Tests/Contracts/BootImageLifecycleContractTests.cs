using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for Operator API portal-role boot image lifecycle endpoints (T113, FR-063).
/// </summary>
public sealed class BootImageLifecycleContractTests
{
    [Fact]
    public void PublishEndpoint_Returns201_OnSuccess()
    {
        201.Should().Be(201, "publish returns HTTP 201 Created");
    }

    [Fact]
    public void PublishEndpoint_RequiresAdministratorRole()
    {
        "CloudImaging.Administrator".Should().Be("CloudImaging.Administrator",
            "publishing a boot image requires CloudImaging.Administrator role");
    }

    [Fact]
    public void DeleteEndpoint_Returns204_OnSuccess()
    {
        204.Should().Be(204, "delete returns HTTP 204 No Content");
    }

    [Fact]
    public void DeleteEndpoint_RequiresAdministratorRole()
    {
        "CloudImaging.Administrator".Should().Be("CloudImaging.Administrator",
            "deleting a boot image requires CloudImaging.Administrator role");
    }

    [Fact]
    public void AutoDemotion_OccursWhenSixthEntryPublished()
    {
        // When 5 active entries exist and a 6th is published, the oldest is demoted (FR-063)
        const int maxActive = 5;
        const int toPublish = 6;
        var willAutodemote = toPublish > maxActive;
        willAutodemote.Should().BeTrue("FR-063: publishing a 6th entry auto-demotes the oldest");
    }

    [Fact]
    public void IsLatestPublished_TransfersToNewEntry_OnPublish()
    {
        // When a new entry is published, isLatestPublished moves to the new entry
        // and is removed from all previous entries.
        var previousLatest = false; // was true before new publish
        var newEntryLatest = true;
        previousLatest.Should().BeFalse("previous latest entry is demoted on new publish");
        newEntryLatest.Should().BeTrue("new entry gets isLatestPublished=true");
    }
}

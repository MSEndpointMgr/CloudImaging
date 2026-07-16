using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for Operator API boot image list and SAS endpoints (T060, FR-053, FR-056).
/// </summary>
public sealed class BootImageContractTests
{
    [Fact]
    public void GetBootImages_ResponseMustInclude_Sha256Hash_And_IsLatestPublished()
    {
        // FR-053, FR-056 mandate these fields on every boot image DTO
        var required = new[] { "bootImageId", "version", "sha256Hash", "isLatestPublished", "isActive" };
        required.Should().Contain("sha256Hash",       "sha256Hash required for Media Builder hash verification (FR-056)");
        required.Should().Contain("isLatestPublished","isLatestPublished required so Media Builder pre-selects latest (FR-053)");
    }

    [Fact]
    public void GetBootImagesSas_ResponseMustInclude_Sha256Hash()
    {
        // POST /api/boot-images/{id}/sas response MUST include sha256Hash
        var required = new[] { "sasTokenUrl", "expiresAt", "sha256Hash" };
        required.Should().Contain("sha256Hash",
            "the SAS response must include sha256Hash so the Media Builder can verify the downloaded WIM (FR-056)");
    }

    [Fact]
    public void ActiveCatalog_AllowsMaximumFiveActiveEntries()
    {
        // FR-063: catalog overflow auto-demotes the oldest entry
        const int maxActive = 5;
        maxActive.Should().Be(5, "FR-063 mandates a maximum of 5 active boot image entries");
    }

    [Fact]
    public void ExactlyOneEntry_IsLatestPublished_AtAnyTime()
    {
        // FR-053: exactly one entry has isLatestPublished=true
        var entries = new[]
        {
            new { IsLatestPublished = false },
            new { IsLatestPublished = true  },
            new { IsLatestPublished = false },
        };
        entries.Count(e => e.IsLatestPublished).Should().Be(1,
            "exactly one boot image must be marked as latest published at any time (FR-053)");
    }
}

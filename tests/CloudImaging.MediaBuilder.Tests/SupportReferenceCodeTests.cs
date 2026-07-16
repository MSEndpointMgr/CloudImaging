using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Support reference code format compliance tests for Media Builder (T160, FR-058).
/// All six CMB stage codes must follow the CMB-{sessionRef}-{stageCode}-{epoch} format.
/// </summary>
public sealed class SupportReferenceCodeTests
{
    private static readonly string[] ValidStageCodes = ["DVI", "PRT", "BID", "BCF", "DWN", "APL"];

    // ── Format compliance ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("DVI")]  // Device identification
    [InlineData("PRT")]  // Partition
    [InlineData("BID")]  // Boot image download
    [InlineData("BCF")]  // Boot config
    [InlineData("DWN")]  // Download
    [InlineData("APL")]  // Apply
    public void AllSixStageCodes_AreThreeCharacters(string code)
    {
        code.Should().HaveLength(3, "CMB stage codes must be exactly 3 characters (FR-058)");
    }

    [Fact]
    public void AllSixStageCodes_ArePresent()
    {
        ValidStageCodes.Should().HaveCount(6,
            "Media Builder must have exactly 6 CMB stage codes (FR-058)");
    }

    // ── SupportReferenceCode.ForMediaBuilder factory ──────────────────────────

    [Theory]
    [InlineData("DVI")]
    [InlineData("BID")]
    public void ForMediaBuilder_GeneratesValidCode(string stageCode)
    {
        var code = SupportReferenceCode.ForMediaBuilder("ABCD1234", stageCode);

        code.ToString().Should().MatchRegex(@"^CMB-[A-Z0-9]+-[A-Z]+-\d+$",
            "support reference code must follow CMB-{sessionRef}-{stageCode}-{epoch} format");
        code.ToString().Should().Contain(stageCode, "stage code must appear in the reference");
        code.ToString().Should().StartWith("CMB-", "CMB prefix identifies Media Builder errors");
    }

    // ── Epoch seconds ─────────────────────────────────────────────────────────

    [Fact]
    public void SupportReferenceCode_EpochSeconds_IsCurrentTime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code   = SupportReferenceCode.ForMediaBuilder("TEST0001", "DVI");
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        code.EpochSeconds.Should().BeInRange(before, after,
            "EpochSeconds must reflect the time of error creation");
    }

    // ── ComponentCode ─────────────────────────────────────────────────────────

    [Fact]
    public void MediaBuilderCodes_UseComponentCode_CMB()
    {
        var code = SupportReferenceCode.ForMediaBuilder("ABCD1234", "DVI");
        code.ComponentCode.Should().Be("CMB",
            "Media Builder support codes must use the CMB component prefix (FR-058)");
    }
}

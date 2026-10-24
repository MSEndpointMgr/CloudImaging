using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>
/// The change summary on every version row: counts per section matched by stable id, reorders,
/// answer file and failure handling changes, the names behind the counts, and a total.
/// </summary>
public sealed class ChangeSummaryBuilderTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static SetupRegistryValue Value(Guid? id, string name, string data = "1") =>
        new() { Id = id, Name = name, Hive = SetupRegistryHive.Software, Key = "Policies\\Test", ValueName = name, Type = SetupRegistryValueType.DWord, Data = data };

    private static SetupScript Script(Guid? id, string name, int order, string sha = "aa", params Guid[] locations) =>
        new()
        {
            Id = id,
            Name = name,
            Runs = SetupScriptPhase.WinPE,
            Order = order,
            TimeoutMinutes = 5,
            ContentSha256 = sha,
            Locations = locations.Length == 0 ? new SetupScriptLocations() : new SetupScriptLocations { All = false, LocationIds = locations },
        };

    [Fact]
    public void VersionOne_CountsEverythingAsAdded()
    {
        var summary = ChangeSummaryBuilder.Build(null, new SetupProfileContent
        {
            RegistryValues = [Value(null, "Time zone"), Value(null, "Encryption")],
            Scripts = [Script(null, "Wake on LAN", 1)],
            AnswerFile = new SetupAnswerFile { ContentSha256 = "bb" },
        });

        summary.RegistryValues.Should().BeEquivalentTo(new SetupProfileSectionChange { Added = 2 });
        summary.Scripts.Added.Should().Be(1);
        summary.Scripts.Reordered.Should().BeFalse();
        summary.AnswerFile.Should().Be(SetupAnswerFileChange.Added);
        summary.FailureHandling.Should().BeNull();
        summary.Names.RegistryValues.Should().Equal("Time zone", "Encryption");
        summary.Total.Should().Be(4);
    }

    [Fact]
    public void Items_AreMatchedByStableId_ForAddedRemovedAndChanged()
    {
        var before = new SetupProfileContent { RegistryValues = [Value(A, "Kept"), Value(B, "Changed", "1"), Value(C, "Removed")] };
        var after = new SetupProfileContent { RegistryValues = [Value(A, "Kept"), Value(B, "Changed", "2"), Value(null, "New")] };

        var summary = ChangeSummaryBuilder.Build(before, after);

        summary.RegistryValues.Should().BeEquivalentTo(new SetupProfileSectionChange { Added = 1, Removed = 1, Changed = 1 });
        summary.Names.RegistryValues.Should().BeEquivalentTo(["Changed", "New", "Removed"]);
        summary.Total.Should().Be(3);
    }

    [Fact]
    public void AScriptReorder_CountsOnce()
    {
        var before = new SetupProfileContent { Scripts = [Script(A, "First", 1), Script(B, "Second", 2)] };
        var after = new SetupProfileContent { Scripts = [Script(A, "First", 2), Script(B, "Second", 1)] };

        var summary = ChangeSummaryBuilder.Build(before, after);

        summary.Scripts.Reordered.Should().BeTrue();
        summary.Scripts.Changed.Should().Be(0);
        summary.Total.Should().Be(1);
    }

    [Fact]
    public void ANewTextOrOtherLocations_ChangeAScript_TheSameReferenceDoesNot()
    {
        var before = new SetupProfileContent { Scripts = [Script(A, "Text", 1, "aa"), Script(B, "Where", 2, "bb", C), Script(C, "Same", 3, "cc", A, B)] };
        var after = new SetupProfileContent { Scripts = [Script(A, "Text", 1, "a2"), Script(B, "Where", 2, "bb"), Script(C, "Same", 3, "cc", B, A)] };

        var summary = ChangeSummaryBuilder.Build(before, after);

        summary.Scripts.Changed.Should().Be(2);
        summary.Names.Scripts.Should().Equal("Text", "Where");
    }

    [Theory]
    [InlineData(null, "a1", SetupAnswerFileChange.Added)]
    [InlineData("a1", null, SetupAnswerFileChange.Removed)]
    [InlineData("a1", "b2", SetupAnswerFileChange.Changed)]
    [InlineData("a1", "a1", SetupAnswerFileChange.NoChange)]
    [InlineData(null, null, SetupAnswerFileChange.NoChange)]
    public void TheAnswerFile_IsAddedRemovedChangedOrUnchanged(string? before, string? after, SetupAnswerFileChange expected)
    {
        var summary = ChangeSummaryBuilder.Build(
            new SetupProfileContent { AnswerFile = before is null ? null : new SetupAnswerFile { ContentSha256 = before } },
            new SetupProfileContent { AnswerFile = after is null ? null : new SetupAnswerFile { ContentSha256 = after } });

        summary.AnswerFile.Should().Be(expected);
        summary.Total.Should().Be(expected == SetupAnswerFileChange.NoChange ? 0 : 1);
    }

    [Fact]
    public void FailureHandling_IsRecordedOnlyWhenItChanges()
    {
        var before = new SetupProfileContent();

        ChangeSummaryBuilder.Build(before, new SetupProfileContent()).FailureHandling.Should().BeNull();
        var changed = ChangeSummaryBuilder.Build(before, new SetupProfileContent { FailureHandling = SetupFailureHandling.FailDeployment });
        changed.FailureHandling.Should().BeEquivalentTo(new SetupFailureHandlingChange { From = SetupFailureHandling.CompleteWithWarnings, To = SetupFailureHandling.FailDeployment });
        changed.Total.Should().Be(1);
    }

    [Fact]
    public void Names_AreCappedPerSection()
    {
        var after = new SetupProfileContent { RegistryValues = [.. Enumerable.Range(1, 15).Select(i => Value(null, $"Value {i}"))] };

        var summary = ChangeSummaryBuilder.Build(new SetupProfileContent(), after);

        summary.RegistryValues.Added.Should().Be(15);
        summary.Names.RegistryValues.Should().HaveCount(SetupProfileChangeNames.MaxNamesPerSection);
    }
}

using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverSetBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fingerprint_ItemOrder_DoesNotMatter()
    {
        var items = new[] { Item("a", "1"), Item("b", "2"), Item("c", "3") };

        DriverSetBuilder.Fingerprint(items).Should().Be(DriverSetBuilder.Fingerprint(items.Reverse()));
    }

    [Fact]
    public void Fingerprint_VersionActionOrOrigin_ChangesIt()
    {
        var baseline = DriverSetBuilder.Fingerprint([Item("a", "1")]);

        DriverSetBuilder.Fingerprint([Item("a", "2")]).Should().NotBe(baseline);
        DriverSetBuilder.Fingerprint([Item("a", "1") with { Action = DriverAction.OfflineAndSpecialize }]).Should().NotBe(baseline);
        DriverSetBuilder.Fingerprint([Item("a", "1") with { Origin = DriverComponentOrigin.RolledBack }]).Should().NotBe(baseline);
    }

    [Fact]
    public void Plan_SkippedComponent_IsNotInTheSetOrItsFingerprint()
    {
        var withSkip = DriverSetBuilder.Plan([Verified("a", "1"), Verified("b", "1", DriverAction.Skip)]);
        var withNewerSkip = DriverSetBuilder.Plan([Verified("a", "1"), Verified("b", "2", DriverAction.Skip)]);

        withSkip.Items.Select(i => i.LineageKey).Should().Equal("a");
        withSkip.Fingerprint.Should().Be(withNewerSkip.Fingerprint);
    }

    [Fact]
    public void Plan_RolledBackAndCustomOrigins_AreKept()
    {
        var plan = DriverSetBuilder.Plan([
            Verified("a", "1.0") with { Desired = new(Item("a", "1.0") with { Origin = DriverComponentOrigin.RolledBack }, DriverFileStatus.Verified) },
            Verified("custom-1", "7") with { Desired = new(Item("custom-1", "7") with { Origin = DriverComponentOrigin.Custom }, DriverFileStatus.Verified) },
        ]);

        plan.Items.Select(i => i.Origin).Should().BeEquivalentTo([DriverComponentOrigin.RolledBack, DriverComponentOrigin.Custom]);
    }

    [Fact]
    public void Plan_FileStillDownloading_IsNotReady()
    {
        var plan = DriverSetBuilder.Plan([Verified("a", "1"), new DriverSetCandidate("b", DriverAction.Offline, new(Item("b", "2"), DriverFileStatus.Downloading), null)]);

        plan.Ready.Should().BeFalse();
        plan.WaitingFileKeys.Should().Equal("b_2");
    }

    [Fact]
    public void Plan_FailedFile_KeepsTheLastVerifiedVersion()
    {
        var plan = DriverSetBuilder.Plan([new DriverSetCandidate("a", DriverAction.Offline, new(Item("a", "2"), DriverFileStatus.Failed), Item("a", "1"))]);

        plan.Items.Should().ContainSingle().Which.Version.Should().Be("1");
        plan.FellBack.Should().Equal("a");
        plan.Ready.Should().BeTrue();
    }

    [Fact]
    public void Plan_FailedFileWithoutPreviousVersion_LeavesTheComponentOut()
    {
        var plan = DriverSetBuilder.Plan([Verified("x", "1"), new DriverSetCandidate("a", DriverAction.Offline, new(Item("a", "2"), DriverFileStatus.Failed), null)]);

        plan.Items.Select(i => i.LineageKey).Should().Equal("x");
    }

    [Fact]
    public void Plan_WithdrawnComponent_StaysAtItsLastVerifiedVersion()
    {
        var plan = DriverSetBuilder.Plan([new DriverSetCandidate("a", DriverAction.Offline, null, Item("a", "3"))]);

        plan.Items.Should().ContainSingle().Which.Version.Should().Be("3");
    }

    [Fact]
    public void Order_OfflineFirst_ThenCategoryAndName()
    {
        var ordered = DriverSetBuilder.Order([
            Item("s", "1") with { Action = DriverAction.Specialize, Category = "Audio", Name = "A" },
            Item("n2", "1") with { Category = "Network", Name = "Wireless" },
            Item("n1", "1") with { Category = "Network", Name = "Ethernet" },
            Item("o", "1") with { Action = DriverAction.OfflineAndSpecialize, Category = "Audio", Name = "Z" },
        ]);

        ordered.Select(i => i.LineageKey).Should().Equal("o", "n1", "n2", "s");
    }

    [Fact]
    public void Decide_FirstReadyPlan_BecomesCurrentVersionOne()
    {
        var plan = DriverSetBuilder.Plan([Verified("a", "1")]);

        var decision = DriverSetBuilder.Decide(new DriverSetPointerRecord(), plan, Now);

        decision.Kind.Should().Be(DriverSetDecisionKind.NewCurrent);
        decision.Next.CurrentVersion.Should().Be(1);
        decision.Next.PreviousVersion.Should().BeNull();
        decision.Next.BuiltAt.Should().Be(Now);
    }

    [Fact]
    public void Decide_UnchangedFingerprint_CreatesNoSet()
    {
        var plan = DriverSetBuilder.Plan([Verified("a", "1")]);
        var pointer = new DriverSetPointerRecord { CurrentVersion = 4, LastVersion = 4, CurrentFingerprint = plan.Fingerprint };

        DriverSetBuilder.Decide(pointer, plan, Now).Kind.Should().Be(DriverSetDecisionKind.NoChange);
    }

    [Fact]
    public void Decide_FingerprintOfTheSetRolledBackFrom_CreatesNoSet()
    {
        var rolledBackFrom = DriverSetBuilder.Plan([Verified("a", "2")]);
        var pointer = new DriverSetPointerRecord { CurrentVersion = 3, LastVersion = 4, CurrentFingerprint = "previous", RolledBackFromFingerprint = rolledBackFrom.Fingerprint };

        DriverSetBuilder.Decide(pointer, rolledBackFrom, Now).Kind.Should().Be(DriverSetDecisionKind.NoChange);
    }

    [Fact]
    public void Decide_PlanWaitingForFiles_IsPendingThenBecomesCurrentWithTheSameVersion()
    {
        var waiting = DriverSetBuilder.Plan([new DriverSetCandidate("a", DriverAction.Offline, new(Item("a", "2"), DriverFileStatus.Downloading), null)]);
        var pointer = new DriverSetPointerRecord { CurrentVersion = 6, LastVersion = 6, CurrentFingerprint = "old" };

        var pending = DriverSetBuilder.Decide(pointer, waiting, Now);
        var ready = DriverSetBuilder.Decide(pending.Next, DriverSetBuilder.Plan([Verified("a", "2")]), Now);

        pending.Kind.Should().Be(DriverSetDecisionKind.Pending);
        pending.Next.PendingVersion.Should().Be(7);
        pending.Next.CurrentVersion.Should().Be(6);
        ready.Kind.Should().Be(DriverSetDecisionKind.NewCurrent);
        ready.Next.CurrentVersion.Should().Be(7);
        ready.Next.PreviousVersion.Should().Be(6);
        ready.Next.PendingVersion.Should().BeNull();
    }

    [Fact]
    public void Decide_HoldForTesting_PutsAReadySetInTesting_AndKeepsTheCurrentSet()
    {
        var pointer = new DriverSetPointerRecord { CurrentVersion = 6, PreviousVersion = 5, LastVersion = 6, CurrentFingerprint = "six" };

        var decision = DriverSetBuilder.Decide(pointer, DriverSetBuilder.Plan([Verified("a", "2")]), Now, holdForTesting: true);

        decision.Kind.Should().Be(DriverSetDecisionKind.NewTesting);
        decision.Version.Should().Be(7);
        decision.Next.TestingVersion.Should().Be(7);
        decision.Next.TestingSince.Should().Be(Now);
        decision.Next.CurrentVersion.Should().Be(6);
        decision.Next.PreviousVersion.Should().Be(5);
        decision.Next.LastVersion.Should().Be(7);
    }

    [Fact]
    public void Decide_HoldForTesting_SameAsTheSetInTesting_CreatesNoSet_ButANewerOneReplacesIt()
    {
        var seven = DriverSetBuilder.Plan([Verified("a", "2")]);
        var pointer = new DriverSetPointerRecord { CurrentVersion = 6, LastVersion = 7, CurrentFingerprint = "six", TestingVersion = 7, TestingFingerprint = seven.Fingerprint };

        DriverSetBuilder.Decide(pointer, seven, Now, holdForTesting: true).Kind.Should().Be(DriverSetDecisionKind.NoChange);
        var newer = DriverSetBuilder.Decide(pointer, DriverSetBuilder.Plan([Verified("a", "3")]), Now, holdForTesting: true);
        newer.Kind.Should().Be(DriverSetDecisionKind.NewTesting);
        newer.Next.TestingVersion.Should().Be(8);
    }

    [Fact]
    public void Decide_BackToTheCurrentSet_EndsTesting()
    {
        var six = DriverSetBuilder.Plan([Verified("a", "1")]);
        var pointer = new DriverSetPointerRecord { CurrentVersion = 6, LastVersion = 7, CurrentFingerprint = six.Fingerprint, TestingVersion = 7, TestingFingerprint = "seven" };

        var decision = DriverSetBuilder.Decide(pointer, six, Now, holdForTesting: true);

        decision.Kind.Should().Be(DriverSetDecisionKind.NoChange);
        decision.Next.TestingVersion.Should().BeNull();
    }

    [Fact]
    public void Release_MakesTheSetInTestingCurrent()
    {
        var pointer = new DriverSetPointerRecord { CurrentVersion = 6, PreviousVersion = 5, LastVersion = 7, CurrentFingerprint = "six", RolledBackFromFingerprint = "x", TestingVersion = 7, TestingFingerprint = "seven", TestingSince = Now };

        var released = DriverSetBuilder.Release(pointer, Now);

        released.CurrentVersion.Should().Be(7);
        released.CurrentFingerprint.Should().Be("seven");
        released.PreviousVersion.Should().Be(6);
        released.RolledBackFromFingerprint.Should().BeNull();
        released.TestingVersion.Should().BeNull();
        released.TestingFingerprint.Should().BeNull();
    }

    [Fact]
    public void RollBack_SwapsCurrentAndPrevious_AndRemembersTheOneRolledBackFrom()
    {
        var pointer = new DriverSetPointerRecord { CurrentVersion = 7, PreviousVersion = 6, LastVersion = 7, CurrentFingerprint = "seven" };

        var rolled = DriverSetBuilder.RollBack(pointer, "six", Now);

        rolled.CurrentVersion.Should().Be(6);
        rolled.CurrentFingerprint.Should().Be("six");
        rolled.RolledBackFromFingerprint.Should().Be("seven");
        rolled.PreviousVersion.Should().Be(7);
    }

    private static DriverSetCandidate Verified(string lineage, string version, DriverAction action = DriverAction.Offline) =>
        new(lineage, action, new DriverSetFileCandidate(Item(lineage, version), DriverFileStatus.Verified), null);

    private static DriverSetItem Item(string lineage, string version) => new()
    {
        LineageKey = lineage,
        ComponentId = $"sp-{lineage}",
        Name = $"Component {lineage}",
        Category = "Driver - Network",
        Version = version,
        Action = DriverAction.Offline,
        Origin = DriverComponentOrigin.Vendor,
        FileKey = $"{lineage}_{version}",
        BlobName = $"files/hp/{lineage}/{version}/{lineage}.exe",
        Sha256 = new string(lineage[0], 64),
        Size = 100,
        FileName = $"{lineage}.exe",
    };
}

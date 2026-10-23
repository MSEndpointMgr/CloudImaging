using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;
using static CloudImaging.ImagingCoreApi.Tests.TestDoubles.SetupProfileTestBed;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class SetupProfileServiceTests
{
    private readonly SetupProfileTestBed _bed = new();

    private static SetupScript Script(string name = "Enable Wake on LAN", string content = "exit 0", Guid? id = null, int order = 1) =>
        new() { Id = id, Name = name, Runs = SetupScriptPhase.WinPE, Order = order, TimeoutMinutes = 5, Content = content };

    [Fact]
    public async Task Create_WritesVersionOne_BlobRowTargetsAndHistory()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm, _bed.Gothenburg));

        detail.CurrentVersion.Should().Be(1);
        detail.CreatedBy.Should().Be("ops@contoso.com");
        detail.Targeting.LocationIds.Should().BeEquivalentTo([_bed.Stockholm, _bed.Gothenburg]);
        detail.Content.RegistryValues.Should().ContainSingle().Which.Id.Should().NotBeNull();
        _bed.Blobs.Blobs.Keys.Should().ContainSingle().Which.Should().Be($"{detail.ProfileId:D}/000001.json");

        var row = await _bed.Versions.GetAsync(detail.ProfileId, 1);
        row!.ContentSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        row.ContentSizeBytes.Should().Be(_bed.Blobs.Blobs.Values.Single().ToArray().Length);
        row.ChangeSummary.RegistryValues.Added.Should().Be(1);

        var history = await _bed.History.ListAsync(detail.ProfileId);
        history.Select(h => h.Kind).Should().BeEquivalentTo([SetupProfileHistoryKind.Created, SetupProfileHistoryKind.VersionSaved]);
        history.Single(h => h.Kind == SetupProfileHistoryKind.Created).Change.Should().Be("Created, targeting Stockholm HQ and Gothenburg Office");
    }

    [Fact]
    public async Task Create_WithoutActor_IsRefused()
    {
        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest { Name = "Lab", Targeting = AllDevices, Content = Content() });

        result.Outcome.Should().Be(SetupProfileOutcome.BadRequest);
        result.Detail.Should().Be("The acting user is missing.");
    }

    [Fact]
    public async Task Create_DuplicateName_IgnoringCase_IsRefused()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest { Name = "STOCKHOLM", Targeting = At(_bed.Gothenburg), Content = Content(), Actor = Actor() });

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        result.Detail.Should().Be("A setup profile named \"STOCKHOLM\" already exists. Choose another name.");
    }

    [Fact]
    public async Task Create_LocationOwnedByAnotherProfile_IsRefused_AndChangesNothing()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest { Name = "Lab", Targeting = At(_bed.Gothenburg, _bed.Stockholm), Content = Content(), Actor = Actor() });

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        result.Detail.Should().Be("Stockholm HQ is already targeted by \"Stockholm\". Remove it there first.");
        (await _bed.Service.ListAsync()).Should().ContainSingle();
        (await _bed.Targets.GetOwnerAsync(_bed.Gothenburg.ToString("D"))).Should().BeNull();
    }

    [Fact]
    public async Task Create_AllDevicesTaken_IsRefused()
    {
        await _bed.CreateAsync("Everywhere", AllDevices);

        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest { Name = "Second", Targeting = AllDevices, Content = Content(), Actor = Actor() });

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        result.Detail.Should().Be("All devices is already used by \"Everywhere\". Change that profile first.");
    }

    [Fact]
    public async Task Create_Duplicate_RecordsTheSourceOnVersionOneAndInHistory()
    {
        var source = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest
        {
            Name = "Stockholm (copy)",
            Targeting = At(_bed.Gothenburg),
            Content = source.Content,
            DuplicatedFrom = new SetupProfileSourceReference { ProfileId = source.ProfileId, Version = 1 },
            Actor = Actor(),
        });

        var copy = (SetupProfileDetail)result.Value!;
        (await _bed.Versions.GetAsync(copy.ProfileId, 1))!.DuplicatedFrom.Should().BeEquivalentTo(new SetupProfileDuplicatedFrom { ProfileId = source.ProfileId, ProfileName = "Stockholm", Version = 1 });
        (await _bed.History.ListAsync(copy.ProfileId)).Should().Contain(h => h.Kind == SetupProfileHistoryKind.Duplicated && h.Change == "Duplicated from Stockholm, version 1");
    }

    [Fact]
    public async Task Save_WithoutContent_ChangesNameAndTargetingOnly()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        _bed.Time.Advance(TimeSpan.FromMinutes(5));

        var result = await _bed.SaveAsync(detail, targeting: At(_bed.Stockholm, _bed.Gothenburg), name: "Stockholm and Gothenburg", upn: "ops2@contoso.com");

        var saved = (SetupProfileDetail)result.Value!;
        saved.CurrentVersion.Should().Be(1);
        saved.Name.Should().Be("Stockholm and Gothenburg");
        saved.LastModifiedBy.Should().Be("ops2@contoso.com");
        (await _bed.Versions.ListAsync(detail.ProfileId)).Should().ContainSingle();
        var history = await _bed.Service.ListHistoryAsync(detail.ProfileId);
        history!.Select(h => h.Change).Should().Contain(["Renamed from Stockholm to Stockholm and Gothenburg", "Added Gothenburg Office"]);
    }

    [Fact]
    public async Task Save_IdenticalContent_CreatesNoVersion()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.SaveAsync(detail, detail.Content);

        ((SetupProfileDetail)result.Value!).CurrentVersion.Should().Be(1);
        (await _bed.Versions.ListAsync(detail.ProfileId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Save_ChangedContent_CreatesNextVersion_AndKeepsItemIds()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var id = detail.Content.RegistryValues[0].Id;

        var result = await _bed.SaveAsync(detail, detail.Content with { RegistryValues = [detail.Content.RegistryValues[0] with { Data = "0" }] });

        var saved = (SetupProfileDetail)result.Value!;
        saved.CurrentVersion.Should().Be(2);
        saved.Content.Version.Should().Be(2);
        saved.Content.RegistryValues.Single().Id.Should().Be(id);
        (await _bed.Versions.GetAsync(detail.ProfileId, 2))!.ChangeSummary.RegistryValues.Changed.Should().Be(1);
    }

    [Fact]
    public async Task Save_StaleBaseVersion_NamesWhoSavedAndWhen()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        _bed.Time.Now = new DateTimeOffset(2026, 10, 23, 10, 41, 0, TimeSpan.Zero);
        await _bed.SaveAsync(detail, Content(Value(data: "0")), upn: "ops2@contoso.com");

        var result = await _bed.SaveAsync(detail, Content(Value(data: "1")));

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        result.Detail.Should().Be("This profile was changed by ops2@contoso.com at 2026-10-23 10:41. Reload to see their changes.");
    }

    [Fact]
    public async Task Save_SwitchToAllDevices_ReplacesTheLocationRowsInOneChange()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm, _bed.Gothenburg));

        await _bed.SaveAsync(detail, targeting: AllDevices);

        (await _bed.Targets.ListByProfileAsync(detail.ProfileId)).Should().BeEquivalentTo(["all-devices"]);
        (await _bed.Service.ListHistoryAsync(detail.ProfileId))!.Should().Contain(h => h.Change == "Targeting changed from Selected locations to All devices");
    }

    [Fact]
    public async Task Save_InvalidContent_ReturnsEveryProblem_AndWritesNothing()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.SaveAsync(detail, Content(Value(data: "seven") with { Key = "HKLM\\SOFTWARE" }));

        result.Outcome.Should().Be(SetupProfileOutcome.BadRequest);
        result.Detail.Should().Contain("relative to the hive").And.Contain("must be a DWORD");
        (await _bed.Versions.ListAsync(detail.ProfileId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Save_UnknownProfile_IsNotFound()
    {
        var result = await _bed.Service.SaveAsync(Guid.NewGuid(), new SaveSetupProfileRequest { BaseVersion = 1, Name = "x", Targeting = AllDevices, Actor = Actor() });

        result.Outcome.Should().Be(SetupProfileOutcome.NotFound);
        result.Detail.Should().Be("This setup profile no longer exists.");
    }

    [Fact]
    public async Task Save_Scripts_HashesContent_AndCarriesModifiedFieldsForUnchangedScripts()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm), Content() with { Scripts = [Script()] });
        var script = detail.Content.Scripts.Single();
        script.Sha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("exit 0"))));
        script.LastModifiedAt.Should().Be(Start);

        _bed.Time.Advance(TimeSpan.FromHours(1));
        var result = await _bed.SaveAsync(detail, detail.Content with
        {
            Scripts = [script, Script("Second", "exit 1", order: 2)],
        }, upn: "ops2@contoso.com");

        var scripts = ((SetupProfileDetail)result.Value!).Content.Scripts;
        scripts.Single(s => s.Name == "Enable Wake on LAN").LastModifiedAt.Should().Be(Start);
        scripts.Single(s => s.Name == "Second").LastModifiedBy.Should().Be("ops2@contoso.com");
    }

    [Fact]
    public async Task Delete_TombstonesTheProfile_RemovesTargets_KeepsVersionBlobs()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var result = await _bed.Service.DeleteAsync(detail.ProfileId, "ops@contoso.com");

        result.Outcome.Should().Be(SetupProfileOutcome.NoContent);
        (await _bed.Service.ListAsync()).Should().BeEmpty();
        (await _bed.Service.GetDetailAsync(detail.ProfileId)).Should().BeNull();
        (await _bed.Targets.ListByProfileAsync(detail.ProfileId)).Should().BeEmpty();
        _bed.Blobs.Blobs.Should().ContainSingle();
        (await _bed.History.ListAsync(detail.ProfileId)).Should().Contain(h => h.Kind == SetupProfileHistoryKind.Deleted);
    }

    [Fact]
    public async Task Restore_CreatesANewVersionWithTheOldContent_AndKeepsTargeting()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var v2 = (SetupProfileDetail)(await _bed.SaveAsync(detail, Content(Value(data: "2")))).Value!;
        var v3 = (SetupProfileDetail)(await _bed.SaveAsync(v2, Content(Value(data: "3")))).Value!;

        var result = await _bed.Service.RestoreAsync(detail.ProfileId, new RestoreSetupProfileVersionRequest { Version = 1, BaseVersion = v3.CurrentVersion, Actor = Actor() });

        var restored = (SetupProfileDetail)result.Value!;
        restored.CurrentVersion.Should().Be(4);
        restored.Content.RegistryValues.Single().Data.Should().Be("1");
        restored.Targeting.LocationIds.Should().Equal(_bed.Stockholm);
        (await _bed.Versions.GetAsync(detail.ProfileId, 4))!.RestoredFrom.Should().Be(1);
        (await _bed.History.ListAsync(detail.ProfileId)).Should().Contain(h => h.Kind == SetupProfileHistoryKind.VersionRestored && h.Change == "Restored version 1 as version 4");
    }

    [Fact]
    public async Task Restore_StaleBaseVersion_OrUnknownVersion_IsRefused()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "2")));

        (await _bed.Service.RestoreAsync(detail.ProfileId, new RestoreSetupProfileVersionRequest { Version = 1, BaseVersion = 1, Actor = Actor() }))
            .Outcome.Should().Be(SetupProfileOutcome.Conflict);
        (await _bed.Service.RestoreAsync(detail.ProfileId, new RestoreSetupProfileVersionRequest { Version = 9, BaseVersion = 2, Actor = Actor() }))
            .Outcome.Should().Be(SetupProfileOutcome.NotFound);
    }

    [Fact]
    public async Task Save_PrunesBeyondRetention_ButKeepsPinnedVersions()
    {
        await _bed.Config.UpsertAsync(new PortalConfiguration { SetupProfileVersionRetention = 5 });
        var current = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Stockholm, current.ProfileId, 1);

        for (var i = 2; i <= 7; i++)
        {
            current = (SetupProfileDetail)(await _bed.SaveAsync(current, Content(Value(data: i.ToString(System.Globalization.CultureInfo.InvariantCulture))))).Value!;
        }

        var versions = (await _bed.Versions.ListAsync(current.ProfileId)).Select(v => v.Version).ToList();
        versions.Should().BeEquivalentTo([7, 6, 5, 4, 3, 1], "the newest five are kept, and version 1 is pinned by a running deployment");
        _bed.Blobs.Blobs.Keys.Should().NotContain($"{current.ProfileId:D}/000002.json");
    }

    [Fact]
    public async Task Save_PruneFailure_DoesNotFailTheSave()
    {
        await _bed.Config.UpsertAsync(new PortalConfiguration { SetupProfileVersionRetention = 5 });
        var current = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        for (var i = 2; i <= 5; i++)
        {
            current = (SetupProfileDetail)(await _bed.SaveAsync(current, Content(Value(data: i.ToString(System.Globalization.CultureInfo.InvariantCulture))))).Value!;
        }
        _bed.Blobs.FailNextDelete = true;

        var result = await _bed.SaveAsync(current, Content(Value(data: "6")));

        result.Outcome.Should().Be(SetupProfileOutcome.Ok);
        ((SetupProfileDetail)result.Value!).CurrentVersion.Should().Be(6);
    }

    [Fact]
    public async Task ListVersions_ReportsCurrentAndInUseCounts()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "2")));
        await _bed.AddSessionAsync(SessionState.SessionStarted, _bed.Stockholm, detail.ProfileId, 1);
        await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Stockholm, detail.ProfileId, 1);

        var versions = await _bed.Service.ListVersionsAsync(detail.ProfileId);

        versions!.Select(v => (v.Version, v.IsCurrent, v.InUseCount)).Should().Equal((2, true, 0), (1, false, 2));
    }

    [Fact]
    public async Task Sweep_DeletesVersionsOfDeletedProfiles_OnlyOnceUnpinned()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Stockholm, detail.ProfileId, 1);
        await _bed.Service.DeleteAsync(detail.ProfileId, "ops@contoso.com");

        await _bed.Service.SweepAsync();
        _bed.Blobs.Blobs.Should().ContainSingle();

        await _bed.Sessions.UpdateAsync(session with { State = SessionState.SessionCompleted });
        await _bed.Service.SweepAsync();
        _bed.Blobs.Blobs.Should().BeEmpty();
        (await _bed.History.ListAsync(detail.ProfileId)).Should().NotBeEmpty();
    }
}

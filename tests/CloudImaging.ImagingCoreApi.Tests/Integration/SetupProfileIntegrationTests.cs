using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static CloudImaging.ImagingCoreApi.Tests.TestDoubles.SetupProfileTestBed;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>Setup profiles end to end through the real services and functions over in-memory storage.</summary>
public sealed class SetupProfileIntegrationTests
{
    private readonly SetupProfileTestBed _bed = new();
    private readonly OsImageRepository _images;
    private readonly Guid _imageId = Guid.NewGuid();

    public SetupProfileIntegrationTests()
    {
        _images = new OsImageRepository(_bed.Tables);
        _images.CreateAsync(new OsImage { ImageId = _imageId, Name = "Windows 11", Version = "24H2", StoragePath = "os-images/w11.wim", Sha256Hash = new string('a', 64) }).GetAwaiter().GetResult();
    }

    private AssignSessionFunction AssignFunction() =>
        new(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, NullLogger<AssignSessionFunction>.Instance);

    private SessionSetupProfileFunction DescriptorFunction() =>
        new(_bed.Sessions, _bed.Profiles, _bed.Versions, _bed.Service, _bed.BlobService, _bed.Time, NullLogger<SessionSetupProfileFunction>.Instance);

    private LocationFunctions Locations() =>
        new(_bed.Locations, new RegionConfigurationRepository(_bed.Tables, NullLogger<RegionConfigurationRepository>.Instance), new WifiNetworkRepository(_bed.Tables), _bed.Service, NullLogger<LocationFunctions>.Instance);

    private async Task<DeviceSession> AssignAsync(Guid? locationId)
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionAssigned, locationId);
        var response = await AssignFunction().Run(FunctionTestHost.Request("POST", $"https://core/api/internal/sessions/{session.SessionId}/assign", new { osImageId = _imageId }), session.SessionId.ToString(), FunctionTestHost.Context);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await _bed.Sessions.GetByIdAsync(session.SessionId))!;
    }

    [Fact]
    public async Task ConcurrentSavesClaimingTheSameLocation_ExactlyOneWins()
    {
        var a = await _bed.CreateAsync("A", At(_bed.Stockholm));
        var b = await _bed.CreateAsync("B", At(_bed.Malmo));

        var results = await Task.WhenAll(
            _bed.SaveAsync(a, targeting: At(_bed.Stockholm, _bed.Gothenburg)),
            _bed.SaveAsync(b, targeting: At(_bed.Malmo, _bed.Gothenburg)));

        results.Count(r => r.Outcome == SetupProfileOutcome.Ok).Should().Be(1);
        results.Count(r => r.Outcome == SetupProfileOutcome.Conflict).Should().Be(1);
        var owners = await _bed.Targets.ListAllAsync();
        owners.Keys.Count(k => k == _bed.Gothenburg.ToString("D")).Should().Be(1);
    }

    [Fact]
    public async Task Create_NameClaimedByACreateInFlight_IsRefused()
    {
        (await _bed.Profiles.TryReserveNameAsync("Lab", Guid.NewGuid(), _bed.Time.Now)).Should().BeTrue();

        var result = await _bed.Service.CreateAsync(new CreateSetupProfileRequest { Name = "LAB", Targeting = At(_bed.Stockholm), Content = Content(), Actor = Actor() });

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        result.Detail.Should().Be("A setup profile named \"LAB\" already exists. Choose another name.");
        (await _bed.Targets.GetOwnerAsync(_bed.Stockholm.ToString("D"))).Should().BeNull();
    }

    [Fact]
    public async Task Create_NameClaimAbandonedByAFailedCreate_IsTakenOver()
    {
        await _bed.Profiles.TryReserveNameAsync("Lab", Guid.NewGuid(), _bed.Time.Now);
        _bed.Time.Advance(TimeSpan.FromMinutes(6));

        (await _bed.CreateAsync("Lab", At(_bed.Stockholm))).Name.Should().Be("Lab");
    }

    [Fact]
    public async Task Rename_ToANameClaimedByACreateInFlight_IsRefused_AndTheOldNameStaysClaimed()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.Profiles.TryReserveNameAsync("Lab", Guid.NewGuid(), _bed.Time.Now);

        var result = await _bed.SaveAsync(profile, name: "lab");

        result.Outcome.Should().Be(SetupProfileOutcome.Conflict);
        (await _bed.Profiles.TryReserveNameAsync("Stockholm", Guid.NewGuid(), _bed.Time.Now)).Should().BeFalse();
    }

    [Fact]
    public async Task Rename_ReleasesTheOldName_AndDeleteReleasesTheNewOne()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        (await _bed.SaveAsync(profile, name: "Stockholm HQ profile")).Outcome.Should().Be(SetupProfileOutcome.Ok);
        (await _bed.CreateAsync("Stockholm", At(_bed.Gothenburg))).Name.Should().Be("Stockholm");

        await _bed.Service.DeleteAsync(profile.ProfileId, "ops@contoso.com");
        (await _bed.CreateAsync("Stockholm HQ profile", At(_bed.Stockholm))).Name.Should().Be("Stockholm HQ profile");
    }

    [Fact]
    public async Task Assign_PinsTheResolvedProfileVersion_AndLaterSavesDoNotChangeIt()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var session = await AssignAsync(_bed.Stockholm);
        await _bed.SaveAsync(profile, Content(Value(data: "0")));

        session.SetupProfileId.Should().Be(profile.ProfileId);
        session.SetupProfileVersion.Should().Be(1);
        session.SetupProfileName.Should().Be("Stockholm");
        (await _bed.Sessions.GetByIdAsync(session.SessionId))!.SetupProfileVersion.Should().Be(1);
    }

    [Fact]
    public async Task Assign_UntargetedLocation_GetsAllDevices_OrNoPin()
    {
        (await AssignAsync(_bed.Gothenburg)).SetupProfileId.Should().BeNull();

        var everywhere = await _bed.CreateAsync("Everywhere", AllDevices);

        (await AssignAsync(_bed.Gothenburg)).SetupProfileId.Should().Be(everywhere.ProfileId);
        (await AssignAsync(null)).SetupProfileId.Should().Be(everywhere.ProfileId);
    }

    [Fact]
    public async Task BulkAssign_ResolvesPerLocation_AndPinsEverySession()
    {
        var stockholm = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var everywhere = await _bed.CreateAsync("Everywhere", AllDevices);
        var first = await _bed.AddSessionAsync(SessionState.SessionAssigned, _bed.Stockholm);
        var second = await _bed.AddSessionAsync(SessionState.SessionAssigned, _bed.Stockholm);
        var other = await _bed.AddSessionAsync(SessionState.SessionAssigned, _bed.Malmo);
        var service = new BulkAssignmentService(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, NullLogger<BulkAssignmentService>.Instance);

        await service.AssignAsync([first.SessionId, second.SessionId, other.SessionId], _imageId);

        (await _bed.Sessions.GetByIdAsync(first.SessionId))!.SetupProfileId.Should().Be(stockholm.ProfileId);
        (await _bed.Sessions.GetByIdAsync(second.SessionId))!.SetupProfileId.Should().Be(stockholm.ProfileId);
        (await _bed.Sessions.GetByIdAsync(other.SessionId))!.SetupProfileId.Should().Be(everywhere.ProfileId);
    }

    [Fact]
    public async Task Descriptor_StatusCodes_FollowTheSessionAndPin()
    {
        var unknown = await DescriptorFunction().Run(FunctionTestHost.Request("GET", "https://core/x"), Guid.NewGuid().ToString(), FunctionTestHost.Context);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var waiting = await _bed.AddSessionAsync(SessionState.SessionAssigned);
        (await DescriptorFunction().Run(FunctionTestHost.Request("GET", "https://core/x"), waiting.SessionId.ToString(), FunctionTestHost.Context))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var unpinned = await AssignAsync(_bed.Gothenburg);
        (await DescriptorFunction().Run(FunctionTestHost.Request("GET", "https://core/x"), unpinned.SessionId.ToString(), FunctionTestHost.Context))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Descriptor_ReturnsAReadUrlForExactlyThePinnedVersion_EvenForADeletedProfile()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var session = await AssignAsync(_bed.Stockholm);
        await _bed.SaveAsync(profile, Content(Value(data: "0")));
        await _bed.Service.DeleteAsync(profile.ProfileId, "ops@contoso.com");

        var response = await DescriptorFunction().Run(FunctionTestHost.Request("GET", "https://core/x"), session.SessionId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("profileName").GetString().Should().Be("Stockholm");
        body.GetProperty("contentUrl").GetString().Should().Contain($"setup-profiles/{profile.ProfileId:D}/000001.json").And.Contain("sp=r");
        body.GetProperty("contentUrlExpiresAt").GetDateTimeOffset().Should().Be(_bed.Time.Now.AddMinutes(15));
        body.GetProperty("contentSha256").GetString().Should().Be((await _bed.Versions.GetAsync(profile.ProfileId, 1))!.ContentSha256);
    }

    [Fact]
    public async Task DeleteLocation_NotTargeted_DeletesNormally()
    {
        var response = await Locations().DeleteLocation(FunctionTestHost.Request("DELETE", "https://core/x?changedBy=ops%40contoso.com"), _bed.Malmo.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _bed.Locations.GetByIdAsync(_bed.Malmo)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteLocation_ProfileHasOtherTargets_RemovesTheTargetAndRecordsHistory()
    {
        var profile = await _bed.CreateAsync("Stockholm and Gothenburg", At(_bed.Stockholm, _bed.Gothenburg));

        var response = await Locations().DeleteLocation(FunctionTestHost.Request("DELETE", "https://core/x?changedBy=ops%40contoso.com"), _bed.Gothenburg.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _bed.Service.GetDetailAsync(profile.ProfileId))!.Targeting.LocationIds.Should().Equal(_bed.Stockholm);
        var history = await _bed.History.ListAsync(profile.ProfileId);
        history.Should().Contain(h => h.Kind == SetupProfileHistoryKind.LocationDeleted
            && h.Change == "Removed Gothenburg Office because the location was deleted" && h.By == "ops@contoso.com");
    }

    [Fact]
    public async Task DeleteLocation_OnlyTargetOfAProfile_IsRefused_AndNothingIsDeleted()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        var response = await Locations().DeleteLocation(FunctionTestHost.Request("DELETE", "https://core/x?changedBy=ops%40contoso.com"), _bed.Stockholm.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should()
            .Be("Stockholm HQ is the only location of setup profile \"Stockholm\". Add another location to the profile or delete the profile, then try again.");
        (await _bed.Locations.GetByIdAsync(_bed.Stockholm)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteLocation_DetachesUnderTheProfileETag_SoAConcurrentSaveSeesAStaleProfile()
    {
        var profile = await _bed.CreateAsync("Stockholm and Gothenburg", At(_bed.Stockholm, _bed.Gothenburg));
        var loaded = await _bed.Profiles.GetAsync(profile.ProfileId);
        _bed.Time.Advance(TimeSpan.FromMinutes(1));

        (await _bed.Service.DetachLocationAsync(_bed.Gothenburg, "Gothenburg Office", "ops2@contoso.com")).Should().BeNull();

        (await _bed.Profiles.ReplaceAsync(loaded! with { LastModifiedBy = "ops@contoso.com" })).Should().BeFalse();
        var latest = await _bed.Profiles.GetAsync(profile.ProfileId);
        latest!.LastModifiedBy.Should().Be("ops2@contoso.com");
        (await _bed.Targets.ListByProfileAsync(profile.ProfileId)).Should().Equal(_bed.Stockholm.ToString("D"));
    }

    [Fact]
    public async Task RemoveDeletedLocation_NeverLeavesAProfileWithoutALocation()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        (await _bed.Service.RemoveDeletedLocationAsync(_bed.Stockholm, "Stockholm HQ", "ops@contoso.com")).Should().BeNull();

        (await _bed.Targets.ListByProfileAsync(profile.ProfileId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Purge_DeletesTheSessionsStepItems()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionCompleted);
        await _bed.Sessions.UpdateAsync(session with { PurgeAt = Start.AddMinutes(-1), TerminalAt = Start.AddDays(-2) });
        await _bed.Items.ReplaceAsync(session.SessionId, ImagingStepName.PrepareSetup, [new ImagingStepItem { Index = 0, Name = "a", Outcome = StepItemOutcome.Succeeded }]);
        var lifecycle = new DeviceSessionLifecycleService(_bed.Sessions, new SessionHistoryRepository(_bed.Tables), _bed.Config, new PreFlightOverrideRepository(_bed.Tables), _bed.Items, NullLogger<DeviceSessionLifecycleService>.Instance);

        await lifecycle.PurgeTerminalSessionsAsync();

        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup)).Should().BeEmpty();
    }

    [Fact]
    public async Task Descriptor_ListsOnlyTheScriptsForTheSessionsLocation_EachWithAReadUrl()
    {
        var content = Content() with
        {
            Scripts =
            [
                _bed.Script("Everywhere", "Write-Output all", order: 1),
                _bed.Script("Stockholm only", "Write-Output sto", order: 2, locations: [_bed.Stockholm]),
                _bed.Script("Gothenburg only", "Write-Output got", order: 3, locations: [_bed.Gothenburg]),
            ],
            AnswerFile = _bed.AnswerFile("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><settings pass=\"oobeSystem\"/></unattend>"),
        };
        await _bed.CreateAsync("Everywhere", AllDevices, content);

        var stockholm = await DescriptorJsonAsync(await AssignAsync(_bed.Stockholm));
        var noLocation = await DescriptorJsonAsync(await AssignAsync(null));

        var scripts = stockholm.GetProperty("scripts").EnumerateArray().ToList();
        scripts.Select(s => s.GetProperty("contentSha256").GetString()).Should().Equal(Sha("Write-Output all"), Sha("Write-Output sto"));
        scripts[1].GetProperty("sizeBytes").GetInt64().Should().Be(16);
        scripts[1].GetProperty("contentUrl").GetString().Should().Contain($"setup-profiles/content/{Sha("Write-Output sto")}").And.Contain("sp=r");
        scripts[1].GetProperty("contentUrlExpiresAt").GetDateTimeOffset().Should().Be(_bed.Time.Now.AddMinutes(15));
        stockholm.GetProperty("answerFile").GetProperty("contentUrl").GetString().Should().Contain("setup-profiles/content/");
        noLocation.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("contentSha256").GetString()).Should().Equal(Sha("Write-Output all"));
    }

    [Fact]
    public async Task Descriptor_LeavesOutAScriptWhoseLocationWasDeleted_EvenForOldMediaOfThatLocation()
    {
        var content = Content() with
        {
            Scripts =
            [
                _bed.Script("Everywhere", "Write-Output all", order: 1),
                _bed.Script("Gothenburg only", "Write-Output got", order: 2, locations: [_bed.Gothenburg]),
            ],
        };
        await _bed.CreateAsync("Everywhere", AllDevices, content);
        await _bed.Locations.DeleteAsync(_bed.Gothenburg);

        // USB media prepared for Gothenburg still carries its id and falls back to All devices.
        var oldMedia = await DescriptorJsonAsync(await AssignAsync(_bed.Gothenburg));

        oldMedia.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("contentSha256").GetString()).Should().Equal(Sha("Write-Output all"));
    }

    [Fact]
    public async Task Descriptor_LeavesOutAScriptWhoseLocationTheProfileNoLongerTargets()
    {
        var content = Content() with { Scripts = [_bed.Script("Gothenburg only", "Write-Output got", locations: [_bed.Gothenburg])] };
        var profile = await _bed.CreateAsync("West", At(_bed.Stockholm, _bed.Gothenburg), content);
        (await _bed.Service.DetachLocationAsync(_bed.Gothenburg, "Gothenburg Office", "ops2@contoso.com")).Should().BeNull();
        var pinned = await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Gothenburg, profile.ProfileId, profile.CurrentVersion);

        var descriptor = await DescriptorJsonAsync(pinned);

        descriptor.GetProperty("scripts").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_DeletesOldUnreferencedContent_AndKeepsReferencedOrRecentContent()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm), Content() with { Scripts = [_bed.Script("Used", "Write-Output used")] });
        var orphan = _bed.Upload("Write-Output orphan");
        _bed.Time.Advance(TimeSpan.FromHours(25));
        var recent = _bed.Upload("Write-Output recent");

        await _bed.Service.SweepAsync();

        _bed.Blobs.Blobs.Keys.Should().Contain($"content/{Sha("Write-Output used")}").And.Contain($"content/{recent}").And.NotContain($"content/{orphan}");

        // At most once a day: a sweep within the day of the last one leaves an old orphan alone.
        var old = (await _bed.Texts.StoreAsync(System.Text.Encoding.UTF8.GetBytes("Write-Output old"), "script", Start)).Sha256;
        _bed.Time.Advance(TimeSpan.FromHours(23));
        await _bed.Service.SweepAsync();
        _bed.Blobs.Blobs.Keys.Should().Contain($"content/{old}");
        _bed.Time.Advance(TimeSpan.FromHours(1));
        await _bed.Service.SweepAsync();
        _bed.Blobs.Blobs.Keys.Should().NotContain($"content/{old}").And.NotContain($"content/{recent}");
    }

    [Fact]
    public async Task Sweep_KeepsContentAPinnedVersionOfADeletedProfileStillUses()
    {
        var profile = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm), Content() with { Scripts = [_bed.Script("Used", "Write-Output pinned")] });
        await AssignAsync(_bed.Stockholm);
        await _bed.Service.DeleteAsync(profile.ProfileId, "ops@contoso.com");
        _bed.Time.Advance(TimeSpan.FromDays(2));

        await _bed.Service.SweepAsync();

        _bed.Blobs.Blobs.Keys.Should().Contain($"content/{Sha("Write-Output pinned")}");
    }

    private async Task<System.Text.Json.JsonElement> DescriptorJsonAsync(DeviceSession session)
    {
        await _bed.Sessions.UpdateAsync(session with { State = SessionState.SessionInProgress });
        var response = await DescriptorFunction().Run(FunctionTestHost.Request("GET", "https://core/x"), session.SessionId.ToString(), FunctionTestHost.Context);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }
}

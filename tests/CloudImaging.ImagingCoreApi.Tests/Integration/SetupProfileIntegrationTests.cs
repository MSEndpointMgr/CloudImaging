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
        new(_bed.Sessions, _bed.Profiles, _bed.Versions, _bed.BlobService, _bed.Time, NullLogger<SessionSetupProfileFunction>.Instance);

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
    public async Task Purge_DeletesTheSessionsStepItems()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionCompleted);
        await _bed.Sessions.UpdateAsync(session with { PurgeAt = Start.AddMinutes(-1), TerminalAt = Start.AddDays(-2) });
        await _bed.Items.ReplaceAsync(session.SessionId, ImagingStepName.PrepareSetup, [new ImagingStepItem { Index = 0, Name = "a", Outcome = StepItemOutcome.Succeeded }]);
        var lifecycle = new DeviceSessionLifecycleService(_bed.Sessions, new SessionHistoryRepository(_bed.Tables), _bed.Config, new PreFlightOverrideRepository(_bed.Tables), _bed.Items, NullLogger<DeviceSessionLifecycleService>.Instance);

        await lifecycle.PurgeTerminalSessionsAsync();

        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup)).Should().BeEmpty();
    }
}

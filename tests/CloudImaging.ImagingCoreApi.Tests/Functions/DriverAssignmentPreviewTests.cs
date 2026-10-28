using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>The Driver set column preview: what each coupled device would get, and what can be chosen instead.</summary>
public sealed class DriverAssignmentPreviewTests
{
    private const string Url = "https://core.test/api/internal/drivers/assignment-preview";
    private readonly DriverSyncHarness _h = new();
    private readonly SetupProfileTestBed _bed = new();
    private readonly OsImageRepository _images;
    private readonly OsImage _image = DriverSetResolverTests.Image();
    private readonly DriverAssignmentFunctions _functions;

    public DriverAssignmentPreviewTests()
    {
        _images = new OsImageRepository(_bed.Tables);
        _images.CreateAsync(_image).GetAwaiter().GetResult();
        _functions = new DriverAssignmentFunctions(_bed.Sessions, _images, new DriverSetResolver(_h.Bed.Store, _h.Bed.Models, _h.Bed.Pointers, [_h.Adapter]), _h.Bed.SetDocuments);
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"));
        _h.PublishComponents("8C27", DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
    }

    private async Task<DeviceSession> AddDeviceAsync(string systemId, SessionState state = SessionState.SessionAssigned)
    {
        var session = DriverSetResolverTests.Device(systemId) with { State = state };
        await _bed.Sessions.CreateAsync(session);
        return session;
    }

    private Task<HttpResponseData> PreviewAsync(Guid imageId, params Guid[] sessionIds) =>
        _functions.PreviewDriverAssignment(FunctionTestHost.Request("POST", Url, new { osImageId = imageId, sessionIds }), FunctionTestHost.Context);

    [Fact]
    public async Task EachAssignableSession_GetsItsMatchOrSkipReason_AndOptionsAreModelsWithACurrentSet()
    {
        await _h.EnableAsync();
        var elite = await _h.OnboardAsync("8C27", "HP EliteBook 860 G11");
        var probook = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.OnboardAsync("8C28", "HP ZBook draft", DriverModelState.Draft);
        await _h.RunAsync();
        var matched = await AddDeviceAsync("8C26");
        var unmatched = await AddDeviceAsync("8A3B");
        var draft = await AddDeviceAsync("8C28");
        var started = await AddDeviceAsync("8C26", SessionState.SessionStarted);
        var setsBefore = (await _h.Bed.SetDocuments.ListAsync()).Count;

        var response = await PreviewAsync(_image.ImageId, matched.SessionId, unmatched.SessionId, draft.SessionId, started.SessionId, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        json.GetProperty("release").GetString().Should().Be("Windows11_24H2");
        var options = json.GetProperty("options").EnumerateArray().ToList();
        options.Select(o => o.GetProperty("displayName").GetString()).Should().Equal("HP EliteBook 840 G11", "HP EliteBook 860 G11");
        options[0].GetProperty("modelId").GetGuid().Should().Be(probook.ModelId);
        options[0].GetProperty("maxDownloadBytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length);
        options[1].GetProperty("modelId").GetGuid().Should().Be(elite.ModelId);
        var sessions = json.GetProperty("sessions").EnumerateArray().ToDictionary(s => s.GetProperty("sessionId").GetGuid());
        sessions.Keys.Should().BeEquivalentTo([matched.SessionId, unmatched.SessionId, draft.SessionId], "started and unknown sessions are left out");
        sessions[matched.SessionId].GetProperty("matchedModelId").GetGuid().Should().Be(probook.ModelId);
        sessions[unmatched.SessionId].GetProperty("matchedModelId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        sessions[unmatched.SessionId].GetProperty("skipReason").GetString().Should().Be("NoMatchingModel");
        sessions[unmatched.SessionId].GetProperty("matchKey").GetString().Should().Be("8A3B");
        sessions[draft.SessionId].GetProperty("skipReason").GetString().Should().Be("NoMatchingModel", "a draft model is never matched");
        (await _h.Bed.SetDocuments.ListAsync()).Should().HaveCount(setsBefore, "nothing is written");
        (await _bed.Sessions.GetByIdAsync(matched.SessionId))!.DriverModelId.Should().BeNull();
    }

    [Fact]
    public async Task AnImageWithoutARelease_HasNoOptions_AndEverySessionSaysWhy()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        var image = DriverSetResolverTests.Image(release: null);
        await _images.CreateAsync(image);
        var session = await AddDeviceAsync("8C26");

        var json = await (await PreviewAsync(image.ImageId, session.SessionId)).ReadJsonAsync();

        json.GetProperty("release").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        json.GetProperty("options").GetArrayLength().Should().Be(0);
        json.GetProperty("sessions").EnumerateArray().Single().GetProperty("skipReason").GetString().Should().Be("NoWindowsRelease");
    }

    [Fact]
    public async Task AnotherArchitecture_IsNotOffered()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        var arm = DriverSetResolverTests.Image(architecture: MachineArchitecture.Arm64);
        await _images.CreateAsync(arm);

        var json = await (await PreviewAsync(arm.ImageId)).ReadJsonAsync();

        json.GetProperty("architecture").GetString().Should().Be("Arm64");
        json.GetProperty("options").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownImage_Is404_AndOver200Sessions_Is400()
    {
        (await PreviewAsync(Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PreviewAsync(_image.ImageId, Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

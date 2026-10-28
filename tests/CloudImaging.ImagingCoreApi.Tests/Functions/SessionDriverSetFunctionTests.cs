using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class SessionDriverSetFunctionTests
{
    private readonly DriverSyncHarness _h = new();
    private readonly SessionDriverSetFunction _function;

    public SessionDriverSetFunctionTests()
    {
        _function = new SessionDriverSetFunction(_h.Sessions, _h.Bed.SetDocuments, _h.Storage, NullLogger<SessionDriverSetFunction>.Instance);
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
    }

    private async Task<DriverModelRecord> ModelWithSetAsync()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        return model;
    }

    private async Task<DeviceSession> SessionAsync(SessionState state, Guid? modelId = null, int? version = null, DriverSkipReason? skip = null, string? critical = null)
    {
        var session = new DeviceSession
        {
            SessionId = Guid.NewGuid(),
            State = state,
            DeviceSerialNumber = "5CG1234",
            DeviceManufacturer = "HP",
            DeviceModel = "EliteBook",
            DriverModelId = modelId,
            DriverModelName = modelId is null ? null : "HP EliteBook 840 G11",
            DriverRelease = DriverSyncHarness.Release,
            DriverSetVersion = version,
            DriverSkipReason = skip,
            DriverMatchKey = "8C26",
            DriverCriticalCategories = critical,
        };
        await _h.Sessions.CreateAsync(session);
        return session;
    }

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> GetAsync(Guid sessionId) =>
        _function.GetDriverSet(FunctionTestHost.Request("GET", $"https://core/api/internal/sessions/{sessionId}/driver-set"), sessionId.ToString(), FunctionTestHost.Context);

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> LinkAsync(Guid sessionId, string index) =>
        _function.RefreshLink(FunctionTestHost.Request("POST", $"https://core/api/internal/sessions/{sessionId}/driver-set/components/{index}/link"), sessionId.ToString(), index, FunctionTestHost.Context);

    [Fact]
    public async Task Descriptor_ListsThePinnedSet_WithOneLinkEach_AndCriticalFromTheSession()
    {
        var model = await ModelWithSetAsync();
        var session = await SessionAsync(SessionState.SessionStarted, modelId: model.ModelId, version: 1, critical: "[\"driver - network\"]");

        var response = await GetAsync(session.SessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("setVersion").GetInt32().Should().Be(1);
        body.GetProperty("release").GetString().Should().Be("Windows11_24H2");
        body.GetProperty("modelName").GetString().Should().Be("HP EliteBook 840 G11");
        body.GetProperty("skipReason").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        var components = body.GetProperty("components").EnumerateArray().ToList();
        components.Select(c => c.GetProperty("index").GetInt32()).Should().Equal(0, 1);
        var network = components.Single(c => c.GetProperty("componentId").GetString() == "sp200");
        network.GetProperty("critical").GetBoolean().Should().BeTrue();
        network.GetProperty("version").GetString().Should().Be("1.0");
        network.GetProperty("origin").GetString().Should().Be("Vendor");
        network.GetProperty("action").GetString().Should().Be("Offline");
        network.GetProperty("extract").GetString().Should().Be("Hp");
        network.GetProperty("downloadUrl").GetString().Should().Contain("drivers/files/Hp/sp200/1.0/sp200.exe").And.Contain("sp=r");
        network.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(60));
        components.Single(c => c.GetProperty("componentId").GetString() == "sp201").GetProperty("critical").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Descriptor_SkipReason_HasNoComponents_AndCarriesTheMatchKey()
    {
        var session = await SessionAsync(SessionState.SessionInProgress, skip: DriverSkipReason.NoMatchingModel);

        var body = await (await GetAsync(session.SessionId)).ReadJsonAsync();

        body.GetProperty("skipReason").GetString().Should().Be("NoMatchingModel");
        body.GetProperty("matchKey").GetString().Should().Be("8C26");
        body.GetProperty("components").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Descriptor_OutsideStartedOrInProgress_Is409_AndUnknownSessionIs404()
    {
        var model = await ModelWithSetAsync();
        var waiting = await SessionAsync(SessionState.SessionAssigned, modelId: model.ModelId, version: 1);
        var done = await SessionAsync(SessionState.SessionCompleted, modelId: model.ModelId, version: 1);

        (await GetAsync(waiting.SessionId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync(done.SessionId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAsync(Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Descriptor_SessionWithoutAnyPin_Is404()
    {
        var session = await SessionAsync(SessionState.SessionStarted);

        (await GetAsync(session.SessionId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Link_ReturnsANewLinkForOneIndex_And400OutsideTheSet()
    {
        var model = await ModelWithSetAsync();
        var session = await SessionAsync(SessionState.SessionInProgress, modelId: model.ModelId, version: 1);
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(50));

        var link = await LinkAsync(session.SessionId, "1");
        var outside = await LinkAsync(session.SessionId, "2");

        link.StatusCode.Should().Be(HttpStatusCode.OK);
        (await link.ReadJsonAsync()).GetProperty("downloadUrlExpiresAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(110));
        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Descriptor_PinnedSetOfARemovedModel_StillResolves()
    {
        var model = await ModelWithSetAsync();
        var session = await SessionAsync(SessionState.SessionInProgress, modelId: model.ModelId, version: 1);
        await _h.Bed.Models.DeleteAsync(model.ModelId);
        await _h.Bed.Pointers.DeleteAsync(model.ModelId, DriverSyncHarness.Release);
        await _h.Sync.CleanUpAsync();

        var response = await GetAsync(session.SessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("components").GetArrayLength().Should().Be(2);
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0")).Should().NotBeNull("the active session protects its files");
    }
}

using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class DriverOnboardingFunctionsTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private static readonly object Actor = new { upn = "operator@contoso.com", objectId = "oid-1" };
    private static readonly string[] Releases = ["Windows11_24H2"];
    private static readonly string[] BothModels = ["8C26_X64", "8C27_X64"];
    private static readonly string[] OneModel = ["8C26_X64"];
    private readonly DriverSyncHarness _h = new();
    private readonly DriverFunctions _functions;

    public DriverOnboardingFunctionsTests()
    {
        _functions = new DriverFunctions(_h.ModelService, _h.PolicyService);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"), DriverSyncHarness.CatalogModel("8C27", "HP EliteBook 860 G11"));
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.PublishComponents("8C27", DriverSyncHarness.Component("sp110", "sp210", "1.0", "Intel Graphics Driver", "Driver - Graphics"));
    }

    [Fact]
    public async Task Vendors_ListEveryVendor_WithSupportSyncAndModelCount()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var rows = (await (await _functions.GetDriverVendors(FunctionTestHost.Request("GET", $"{Base}/vendors"), FunctionTestHost.Context)).ReadJsonAsync()).EnumerateArray().ToList();

        rows.Select(r => r.GetProperty("vendor").GetString()).Should().Equal("Hp", "Dell", "Lenovo");
        rows[0].GetProperty("supported").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("syncEnabled").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("onboardedModels").GetInt32().Should().Be(1);
        rows[0].GetProperty("matchKeyType").GetString().Should().Be("system ID");
        rows[2].GetProperty("supported").GetBoolean().Should().BeFalse();
        rows[2].GetProperty("syncEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CatalogModels_MissingList_IsFetchedWithSyncOff_AndFlagsOnboardedModels()
    {
        var onboarded = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var response = await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("stale").GetBoolean().Should().BeFalse();
        body.GetProperty("catalogUrl").GetString().Should().Be(_h.Adapter.ModelListUrl);
        var models = body.GetProperty("models").EnumerateArray().ToList();
        models.Select(m => m.GetProperty("displayName").GetString()).Should().Equal("HP EliteBook 840 G11", "HP EliteBook 860 G11");
        models[0].GetProperty("onboardedModelId").GetGuid().Should().Be(onboarded.ModelId);
        models[1].GetProperty("onboardedModelId").ValueKind.Should().Be(JsonValueKind.Null);
        (await _h.Bed.Store.CatalogLists.GetAsync(DriverStore.ListPartition, "Hp"))!.Value.FetchedAt.Should().Be(DriverTestBed.Start);
        (await _h.Bed.Runs.ListAsync()).Should().BeEmpty("reading the model list is not a run");
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.LastRunStartedAt.Should().BeNull("no limit moves");
    }

    [Fact]
    public async Task CatalogModels_ListFetchedWithinSevenDays_IsNotFetchedAgain()
    {
        await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);
        _h.Bed.Time.Advance(TimeSpan.FromDays(6));

        var response = await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp&search=860"), FunctionTestHost.Context);

        _h.Bed.Vendor.Requests.Count(u => u.ToString() == _h.Adapter.ModelListUrl).Should().Be(1);
        (await response.ReadJsonAsync()).GetProperty("models").EnumerateArray().Select(m => m.GetProperty("catalogModelKey").GetString()).Should().Equal("8C27_X64");
    }

    [Fact]
    public async Task CatalogModels_FailedFetchWithOlderList_ReturnsStaleWithWarning()
    {
        await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);
        _h.Bed.Time.Advance(TimeSpan.FromDays(8));
        _h.Bed.Vendor.ServeStatus(_h.Adapter.ModelListUrl, HttpStatusCode.ServiceUnavailable);

        var response = await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("stale").GetBoolean().Should().BeTrue();
        body.GetProperty("warning").GetString().Should().Be("Showing the model list from 2026-10-25 09:00 UTC. Can't reach catalog at https://hpia.hpcloud.hp.com.");
        body.GetProperty("models").GetArrayLength().Should().Be(2);
        (await _h.Bed.Store.CatalogLists.GetAsync(DriverStore.ListPartition, "Hp"))!.Value.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task CatalogModels_FailedFetchWithNoList_Returns503NamingTheCatalog()
    {
        _h.Bed.Vendor.ServeStatus(_h.Adapter.ModelListUrl, HttpStatusCode.ServiceUnavailable);

        var response = await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.ReadJsonAsync();
        body.GetProperty("detail").GetString().Should().Be("Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again.");
        body.GetProperty("catalogUrl").GetString().Should().Be(_h.Adapter.ModelListUrl);
    }

    [Fact]
    public async Task CatalogModels_UnsupportedVendor_Is400()
    {
        var response = await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Lenovo"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_ReturnsCountsPerActionAndSize_WithPerModelErrors()
    {
        await LoadModelListAsync();
        _h.Bed.Vendor.ServeStatus(FakeDriverVendorAdapter.ComponentCatalogUrl("8C27", DriverSyncHarness.Release), HttpStatusCode.InternalServerError);

        var response = await _functions.PreviewDriverModels(FunctionTestHost.Request("POST", $"{Base}/models/preview", new { vendor = "Hp", catalogModelKeys = BothModels, releases = Releases }), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        var model = body.GetProperty("models").EnumerateArray().Single();
        model.GetProperty("catalogModelKey").GetString().Should().Be("8C26_X64");
        model.GetProperty("offline").GetInt32().Should().Be(2);
        model.GetProperty("skip").GetInt32().Should().Be(1);
        model.GetProperty("sizeBytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length + DriverSyncHarness.Content("sp201", "2.0").Length);
        body.GetProperty("errors").EnumerateArray().Single().GetProperty("catalogModelKey").GetString().Should().Be("8C27_X64");
    }

    [Fact]
    public async Task Preview_MoreThanTwentyModels_Is400()
    {
        var keys = Enumerable.Range(0, 21).Select(i => $"K{i}_X64").ToArray();

        var response = await _functions.PreviewDriverModels(FunctionTestHost.Request("POST", $"{Base}/models/preview", new { vendor = "Hp", catalogModelKeys = keys, releases = Releases }), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Onboard_CreatesDraftsInRequestOrder_WithComponents_AndEnqueuesNothing()
    {
        await LoadModelListAsync();

        var response = await OnboardAsync("8C27_X64", "8C26_X64");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var ids = (await response.ReadJsonAsync()).GetProperty("modelIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        ids.Should().HaveCount(2);
        var first = (await _h.Bed.Models.GetAsync(ids[0]))!.Value;
        first.DisplayName.Should().Be("HP EliteBook 860 G11");
        first.State.Should().Be(DriverModelState.Draft);
        first.OnboardedBy.Should().Be("operator@contoso.com");
        (await _h.ComponentsAsync(ids[1])).Keys.Should().BeEquivalentTo("sp100", "sp101", "sp102");
        (await _h.Bed.Models.GetOwnerAsync(DriverVendor.Hp, "8C26")).Should().Be(ids[1]);
        _h.Bed.Queues.Sent.Should().BeEmpty("nothing is downloaded and no set is built for a draft");
        (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Should().Contain(r => r.Value.By == "operator@contoso.com" && r.Value.Subject == "HP EliteBook 860 G11");
    }

    [Fact]
    public async Task Onboard_MatchKeyOwnedByAnotherModel_Is409NamingIt_AndCreatesNothing()
    {
        await LoadModelListAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var response = await OnboardAsync("8C27_X64", "8C26_X64");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("8C26 already belongs to HP EliteBook 840 G11.");
        (await _h.Bed.Models.ListAsync()).Should().ContainSingle();
        (await _h.Bed.Models.GetOwnerAsync(DriverVendor.Hp, "8C27")).Should().BeNull("the keys reserved before the conflict are released");
    }

    [Fact]
    public async Task Onboard_WithoutActor_Is400()
    {
        await LoadModelListAsync();

        var response = await _functions.OnboardDriverModels(FunctionTestHost.Request("POST", $"{Base}/models", new { vendor = "Hp", catalogModelKeys = OneModel, releases = Releases }), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The acting user is missing.");
    }

    [Fact]
    public async Task Complete_ActivatesTheDraft_TurnsSyncOn_AndEnqueuesResolvePerRelease()
    {
        await LoadModelListAsync();
        var id = await OnboardOneAsync("8C26_X64");

        var response = await _functions.CompleteDriverModelOnboarding(FunctionTestHost.Request("POST", $"{Base}/models/{id}/complete", new { actor = Actor }), id.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var model = (await _h.Bed.Models.GetAsync(id))!.Value;
        model.State.Should().Be(DriverModelState.Active);
        model.CompletedBy.Should().Be("operator@contoso.com");
        (await _h.Bed.Store.IsSyncEnabledAsync(DriverVendor.Hp)).Should().BeTrue();
        _h.Bed.Queues.Sent.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.ResolveModel && m.Work.ModelId == id && m.Work.Release == DriverSyncHarness.Release);

        // Downloads start at once and build the first set.
        await _h.PumpAsync();
        (await _h.CurrentSetAsync(id))!.Version.Should().Be(1);
        (await _h.Bed.Store.SetBuilds.GetAsync(DriverTables.Pair(id, DriverSyncHarness.Release), "000001"))!.Value.Trigger.Should().Be("Onboarding");
    }

    [Fact]
    public async Task Complete_ActiveModel_Is409()
    {
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var response = await _functions.CompleteDriverModelOnboarding(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/complete", new { actor = Actor }), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddRelease_NotListedByTheCatalog_Is400()
    {
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var response = await _functions.AddDriverModelRelease(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/releases", new { release = "Windows10_22H2", actor = Actor }), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddRelease_Listed_AddsItResolvesComponentsAndMarksThePairDirty()
    {
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.Bed.Store.CatalogModels.UpsertAsync("Hp", model.CatalogModelKey, DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11").Model with { Releases = [DriverSyncHarness.Release, WindowsRelease.Windows11Version25H2] });
        _h.Bed.Vendor.ServeBytes(FakeDriverVendorAdapter.ComponentCatalogUrl("8C26", WindowsRelease.Windows11Version25H2), JsonSerializer.SerializeToUtf8Bytes(new[] { DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver") }, DriverJson.Options));

        var response = await _functions.AddDriverModelRelease(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/releases", new { release = "Windows11_25H2", actor = Actor }), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.Bed.Models.GetAsync(model.ModelId))!.Value.Releases.Should().Equal(DriverSyncHarness.Release, WindowsRelease.Windows11Version25H2);
        (await _h.Bed.Store.Components.ListPartitionAsync(DriverTables.Pair(model.ModelId, WindowsRelease.Windows11Version25H2))).Should().ContainSingle();
        (await _h.Bed.Pointers.GetAsync(model.ModelId, WindowsRelease.Windows11Version25H2))!.Value.Dirty.Should().BeTrue();
        _h.Bed.Queues.Sent.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.BuildSet && m.Work.Release == WindowsRelease.Windows11Version25H2);
    }

    [Fact]
    public async Task Models_ListsRowsWithCountsSizeAndStatus()
    {
        await _h.EnableAsync();
        var active = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await LoadModelListAsync();
        await OnboardOneAsync("8C27_X64");

        var response = await _functions.GetDriverModels(FunctionTestHost.Request("GET", $"{Base}/models"), FunctionTestHost.Context);

        var rows = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        var row = rows.Single(r => r.GetProperty("modelId").GetGuid() == active.ModelId);
        row.GetProperty("vendor").GetString().Should().Be("Hp");
        row.GetProperty("state").GetString().Should().Be("Active");
        row.GetProperty("included").GetInt32().Should().Be(2);
        row.GetProperty("skipped").GetInt32().Should().Be(1);
        row.GetProperty("matchKeys").EnumerateArray().Select(e => e.GetString()).Should().Equal("8C26");
        row.GetProperty("releases").EnumerateArray().Select(e => e.GetString()).Should().Equal("Windows11_24H2");
        row.GetProperty("status").GetProperty("syncOff").GetBoolean().Should().BeFalse();
        row.GetProperty("status").GetProperty("downloading").ValueKind.Should().Be(JsonValueKind.Null);
        rows.Single(r => r.GetProperty("modelId").GetGuid() != active.ModelId).GetProperty("state").GetString().Should().Be("Draft");
    }

    [Fact]
    public async Task ModelPage_HasTilesComponentsAndSetState()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();

        var response = await _functions.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}?release=Windows11_24H2"), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.ReadJsonAsync();
        page.GetProperty("release").GetString().Should().Be("Windows11_24H2");
        page.GetProperty("setVersion").GetInt32().Should().Be(1);
        page.GetProperty("hasPreviousSet").GetBoolean().Should().BeFalse();
        page.GetProperty("preparing").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("checkBlockedReason").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("checkAvailableAt").ValueKind.Should().Be(JsonValueKind.Null);
        var tiles = page.GetProperty("tiles");
        tiles.GetProperty("offline").GetInt32().Should().Be(2);
        tiles.GetProperty("skip").GetInt32().Should().Be(1);
        var components = page.GetProperty("components").EnumerateArray().ToList();
        components.Select(c => c.GetProperty("lineageKey").GetString()).Should().Equal("sp101", "sp100", "sp102");
        components[2].GetProperty("source").GetString().Should().Be("Default");
        components[2].GetProperty("action").GetString().Should().Be("Skip");
    }

    [Fact]
    public async Task ModelPage_DraftBlocksCheck_AndShowsNoPreparing()
    {
        await LoadModelListAsync();
        var id = await OnboardOneAsync("8C26_X64");

        var page = await (await _functions.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{id}"), id.ToString(), FunctionTestHost.Context)).ReadJsonAsync();

        page.GetProperty("model").GetProperty("state").GetString().Should().Be("Draft");
        page.GetProperty("checkBlockedReason").GetString().Should().Be("Draft");
        page.GetProperty("preparing").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("setVersion").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ModelPage_UnknownModelOrRelease_Is404()
    {
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var unknownModel = await _functions.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{Guid.NewGuid()}"), Guid.NewGuid().ToString(), FunctionTestHost.Context);
        var unknownRelease = await _functions.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}?release=Windows10_22H2"), model.ModelId.ToString(), FunctionTestHost.Context);

        unknownModel.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknownRelease.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task LoadModelListAsync() =>
        await _functions.GetDriverCatalogModels(FunctionTestHost.Request("GET", $"{Base}/catalog/models?vendor=Hp"), FunctionTestHost.Context);

    private async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> OnboardAsync(params string[] keys) =>
        await _functions.OnboardDriverModels(FunctionTestHost.Request("POST", $"{Base}/models", new { vendor = "Hp", catalogModelKeys = keys, releases = Releases, actor = Actor }), FunctionTestHost.Context);

    private async Task<Guid> OnboardOneAsync(string key)
    {
        var response = await OnboardAsync(key);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.ReadJsonAsync()).GetProperty("modelIds")[0].GetGuid();
    }
}

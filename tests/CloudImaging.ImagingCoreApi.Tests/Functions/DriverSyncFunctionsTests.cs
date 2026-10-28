using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class DriverSyncFunctionsTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private const string Upn = "admin@contoso.com";
    private static readonly object Actor = new { upn = Upn, objectId = "oid-1" };
    private readonly FakeDriverVendorAdapter _dell = new(DriverVendor.Dell);
    private readonly DriverSyncHarness _h;
    private readonly DriverSyncFunctions _sync;
    private readonly DriverFunctions _drivers;

    public DriverSyncFunctionsTests()
    {
        _h = new DriverSyncHarness(_dell);
        _sync = new DriverSyncFunctions(_h.Sync, _h.Bed.Runs, _h.Scheduler, NullLogger<DriverSyncFunctions>.Instance);
        _drivers = new DriverFunctions(_h.ModelService, _h.PolicyService);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.PublishModels(_dell, DriverSyncHarness.CatalogModel("0CB4", "Latitude 7450"));
        _h.PublishComponents(_dell, "0CB4", DriverSyncHarness.Component("dell-1", "CP0VC", "A16", "Intel Chipset Driver"));
    }

    private static object Settings(string schedule = "Daily", int weekdays = 31, int repeatWeeks = 1, string localTime = "02:00", string timeZone = "Europe/Berlin", bool approval = false) =>
        new { schedule, weekdays, repeatWeeks, localTime, timeZone, waitForApprovalOfNewComponents = approval, actor = Actor };

    private async Task<HttpResponseData> PutSettingsAsync(object body) =>
        await _sync.SetDriverSyncSettings(FunctionTestHost.Request("PUT", $"{Base}/sync/settings", body), FunctionTestHost.Context);

    private async Task<JsonElement> GetSettingsAsync() =>
        await (await _sync.GetDriverSyncSettings(FunctionTestHost.Request("GET", $"{Base}/sync/settings"), FunctionTestHost.Context)).ReadJsonAsync();

    private async Task<HttpResponseData> SyncNowAsync() =>
        await _sync.StartDriverSyncRun(FunctionTestHost.Request("POST", $"{Base}/sync/runs", new { actor = Actor }), FunctionTestHost.Context);

    private async Task<HttpResponseData> CheckAllAsync() =>
        await _sync.CheckAllDriverModelUpdates(FunctionTestHost.Request("POST", $"{Base}/models/check", new { actor = Actor }), FunctionTestHost.Context);

    private async Task<HttpResponseData> CheckModelAsync(Guid modelId) =>
        await _sync.CheckDriverModelUpdates(FunctionTestHost.Request("POST", $"{Base}/models/{modelId}/check", new { actor = Actor }), modelId.ToString(), FunctionTestHost.Context);

    private async Task<HttpResponseData> SetVendorAsync(string vendor, bool on) =>
        await _drivers.SetDriverVendor(FunctionTestHost.Request("PUT", $"{Base}/vendors/{vendor}", new { syncEnabled = on, actor = Actor }), vendor, FunctionTestHost.Context);

    private async Task<List<DriverChangeLogRecord>> LogAsync() =>
        (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Select(r => r.Value).ToList();

    private async Task<DriverSyncRunRecord> RunOf(HttpResponseData started)
    {
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var runId = (await started.ReadJsonAsync()).GetProperty("runId").GetGuid();
        await _h.PumpAsync();
        return (await _h.Bed.Runs.GetAsync(runId))!.Value;
    }

    [Fact]
    public async Task Settings_Get_ReturnsTheDefaults_TimeZonesInWindowsOrder_AndNextRunAt()
    {
        var settings = await GetSettingsAsync();

        settings.GetProperty("schedule").GetString().Should().Be("Daily");
        settings.GetProperty("localTime").GetString().Should().Be("02:00");
        settings.GetProperty("nextRunAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero));
        settings.GetProperty("syncNowAvailableAt").ValueKind.Should().Be(JsonValueKind.Null);
        var zones = settings.GetProperty("timeZones").EnumerateArray().ToList();
        zones.Should().HaveCount(142);
        zones[0].GetProperty("displayName").GetString().Should().Be("(UTC-12:00) International Date Line West");
        var berlin = zones.Single(z => z.GetProperty("ianaId").GetString() == "Europe/Berlin");
        berlin.GetProperty("displayName").GetString().Should().Be("(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna");
        berlin.GetProperty("covers").EnumerateArray().Select(c => c.GetString()).Should().Contain("Europe/Stockholm");
    }

    [Fact]
    public async Task Settings_Set_AppliesAtOnce_EnqueuesTheNextSlot_AndLogsTheChange()
    {
        var response = await PutSettingsAsync(Settings("SelectedDays", weekdays: 1, repeatWeeks: 2));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("nextRunAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero));
        _h.Bed.Queues.Sent.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.ScheduledSync && m.Work.SlotUtc == new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero));
        var stored = (await _h.Bed.Store.GetSyncSettingsAsync()).Value;
        stored.Schedule.Should().Be(DriverSyncSchedule.SelectedDays);
        stored.RepeatWeeks.Should().Be(2);
        stored.ChangedBy.Should().Be(Upn);
        (await LogAsync()).Should().ContainSingle(l => l.Area == "Sync" && l.Subject == "Schedule" && l.By == Upn);
    }

    [Theory]
    [InlineData("Europe/Stockholm")]
    [InlineData("W. Europe Standard Time")]
    public async Task Settings_ATimeZoneNotInTheList_Is400_AndChangesNothing(string zone)
    {
        var response = await PutSettingsAsync(Settings(timeZone: zone, localTime: "05:00"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Choose a time zone from the list.");
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.LocalTime.Should().Be("02:00");
    }

    [Fact]
    public async Task Settings_SelectedDaysWithoutADay_Is400()
    {
        var response = await PutSettingsAsync(Settings("SelectedDays", weekdays: 0));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Choose at least one day.");
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.Schedule.Should().Be(DriverSyncSchedule.Daily);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Settings_RepeatOutsideOneToFour_Is400(int repeat)
    {
        var response = await PutSettingsAsync(Settings("SelectedDays", repeatWeeks: repeat));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Choose a repeat from the list.");
    }

    [Fact]
    public async Task Settings_Manual_HasNoNextRun()
    {
        var set = await (await PutSettingsAsync(Settings("Manual"))).ReadJsonAsync();

        set.GetProperty("nextRunAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await GetSettingsAsync()).GetProperty("nextRunAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SyncNowAndCheckAll_ShareOneLimit_409WhileRunning_429WithinTheHour()
    {
        await _h.EnableAsync();
        var started = await SyncNowAsync();
        var running = await SyncNowAsync();
        var checkWhileRunning = await CheckAllAsync();

        running.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await running.ReadJsonAsync()).GetProperty("availableAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(60));
        checkWhileRunning.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetSettingsAsync()).GetProperty("syncNowAvailableAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        await RunOf(started);
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(10));
        var tooSoon = await CheckAllAsync();

        tooSoon.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await tooSoon.ReadJsonAsync()).GetProperty("availableAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(60));
        tooSoon.Headers.GetValues("Retry-After").Should().ContainSingle();
        (await GetSettingsAsync()).GetProperty("syncNowAvailableAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(60));

        _h.Bed.Time.Advance(TimeSpan.FromMinutes(51));
        (await CheckAllAsync()).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task SyncNow_RefreshesTheModelLists_ButCheckAll_ReadsComponentsOnly()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        await RunOf(await CheckAllAsync());
        var afterCheck = _h.Bed.Vendor.Requests.Count(u => u.ToString() == _h.Adapter.ModelListUrl);
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(61));
        await RunOf(await SyncNowAsync());

        afterCheck.Should().Be(0);
        _h.Bed.Vendor.Requests.Count(u => u.ToString() == _h.Adapter.ModelListUrl).Should().Be(1);
    }

    [Fact]
    public async Task VendorSwitch_OnAndOff_LogsIt_AndLenovoIsRefused()
    {
        var on = await SetVendorAsync("Hp", true);
        var off = await SetVendorAsync("Hp", false);
        var lenovo = await SetVendorAsync("Lenovo", true);

        on.StatusCode.Should().Be(HttpStatusCode.OK);
        (await on.ReadJsonAsync()).GetProperty("syncEnabled").GetBoolean().Should().BeTrue();
        (await off.ReadJsonAsync()).GetProperty("syncEnabled").GetBoolean().Should().BeFalse();
        lenovo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _h.Bed.Store.IsSyncEnabledAsync(DriverVendor.Lenovo)).Should().BeFalse();
        (await LogAsync()).Where(l => l.Area == "Vendor").Select(l => l.Change).Should().BeEquivalentTo("Sync turned on", "Sync turned off");
    }

    [Fact]
    public async Task ARun_ReadsOnlyEnabledVendors_AndTheirOnboardedModels()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.OnboardAsync("0CB4", "Latitude 7450", vendor: DriverVendor.Dell);

        var run = await RunOf(await SyncNowAsync());

        run.Vendors.Select(v => v.Vendor).Should().Equal(DriverVendor.Hp);
        _h.Bed.Vendor.Requests.Should().NotContain(u => u.AbsolutePath.Contains("/fake/Dell/", StringComparison.Ordinal));
        _h.Bed.Vendor.Requests.Should().OnlyContain(u => DriverStorageService.AllowedHosts.Contains(u.Host), "only published catalog and file URLs on allowed vendor hosts are read");
    }

    [Fact]
    public async Task AVendorTurnedOffDuringARun_LetsItFinish_AndTheNextRunSkipsIt()
    {
        await _h.EnableAsync();
        await _h.EnableAsync(DriverVendor.Dell);
        await _h.OnboardAsync("0CB4", "Latitude 7450", vendor: DriverVendor.Dell);
        var started = await SyncNowAsync();
        await SetVendorAsync("Dell", false);

        var run = await RunOf(started);
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(61));
        var next = await RunOf(await SyncNowAsync());

        run.Vendors.Should().Contain(v => v.Vendor == DriverVendor.Dell && v.Processed);
        next.Vendors.Should().NotContain(v => v.Vendor == DriverVendor.Dell);
    }

    [Fact]
    public async Task OneVendorsFailure_LeavesTheOthersChangesApplied_AndEndsPartlyFailed()
    {
        await _h.EnableAsync();
        await _h.EnableAsync(DriverVendor.Dell);
        var hp = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.OnboardAsync("0CB4", "Latitude 7450", vendor: DriverVendor.Dell);
        _h.Bed.Vendor.ServeStatus(_dell.CatalogUrl("0CB4", DriverSyncHarness.Release), HttpStatusCode.ServiceUnavailable);

        var run = await RunOf(await SyncNowAsync());

        run.Result.Should().Be(DriverSyncResult.PartlyFailed);
        (await _h.CurrentSetAsync(hp.ModelId)).Should().NotBeNull("HP's changes are applied although Dell failed");
    }

    [Fact]
    public async Task ARunWithNothingProcessed_IsFailed_AndOneWithoutFailures_IsCompleted()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var completed = await RunOf(await SyncNowAsync());
        _h.Bed.Vendor.ServeStatus(_h.Adapter.ModelListUrl, HttpStatusCode.ServiceUnavailable);
        _h.Bed.Vendor.ServeStatus(FakeDriverVendorAdapter.ComponentCatalogUrl("8C26", DriverSyncHarness.Release), HttpStatusCode.ServiceUnavailable);
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(61));

        var failed = await RunOf(await SyncNowAsync());

        completed.Result.Should().Be(DriverSyncResult.Completed);
        failed.Result.Should().Be(DriverSyncResult.Failed);
    }

    [Fact]
    public async Task RunsOlderThan90Days_AreDeletedAtTheEndOfARun()
    {
        var old = new DriverSyncRunRecord { RunId = Guid.NewGuid(), Trigger = DriverSyncTrigger.Scheduled, StartedAt = DriverTestBed.Start.AddDays(-91), Result = DriverSyncResult.Completed };
        await _h.Bed.Runs.TryAddAsync(old);
        await _h.Bed.Runs.AddItemAsync(old.RunId, old.StartedAt, new DriverSyncRunItem { Outcome = DriverSyncItemOutcome.Updated });

        await RunOf(await SyncNowAsync());

        (await _h.Bed.Runs.ListAsync()).Should().ContainSingle().Which.RunId.Should().NotBe(old.RunId);
        (await _h.Bed.Runs.ListItemsAsync(old.RunId)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunItems_ListOneLinePerChange_And404ForAnUnknownRun()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await RunOf(await SyncNowAsync());
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp210", "1.1", "Intel Wireless LAN Driver"), DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(61));
        var run = await RunOf(await CheckAllAsync());

        var items = await _sync.GetDriverSyncRunItems(FunctionTestHost.Request("GET", $"{Base}/sync/runs/{run.RunId}/items"), run.RunId.ToString(), FunctionTestHost.Context);
        var unknown = await _sync.GetDriverSyncRunItems(FunctionTestHost.Request("GET", $"{Base}/sync/runs/{Guid.NewGuid()}/items"), Guid.NewGuid().ToString(), FunctionTestHost.Context);

        var item = (await items.ReadJsonAsync()).EnumerateArray().Single();
        item.GetProperty("outcome").GetString().Should().Be("Updated");
        item.GetProperty("oldVersion").GetString().Should().Be("1.0");
        item.GetProperty("newVersion").GetString().Should().Be("1.1");
        item.GetProperty("lineageKey").GetString().Should().Be("sp100");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Progress_ReturnsTheStages_And404ForAnUnknownRun()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var run = await RunOf(await SyncNowAsync());

        var progress = await _sync.GetDriverSyncRunProgress(FunctionTestHost.Request("GET", $"{Base}/sync/runs/{run.RunId}/progress"), run.RunId.ToString(), FunctionTestHost.Context);
        var unknown = await _sync.GetDriverSyncRunProgress(FunctionTestHost.Request("GET", $"{Base}/sync/runs/{Guid.NewGuid()}/progress"), Guid.NewGuid().ToString(), FunctionTestHost.Context);
        var invalid = await _sync.GetDriverSyncRunProgress(FunctionTestHost.Request("GET", $"{Base}/sync/runs/x/progress"), "x", FunctionTestHost.Context);

        var body = await progress.ReadJsonAsync();
        body.GetProperty("models").GetProperty("done").GetInt32().Should().Be(1);
        body.GetProperty("components").GetProperty("total").GetInt32().Should().Be(run.DownloadedComponents).And.BeGreaterThan(0);
        body.GetProperty("run").GetProperty("downloadedComponents").GetInt32().Should().Be(run.DownloadedComponents);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=2026-10-01T00:00:00Z")]
    [InlineData("?from=2026-10-26T00:00:00Z&to=2026-10-01T00:00:00Z")]
    [InlineData("?from=yesterday&to=2026-10-01T00:00:00Z")]
    public async Task Report_WithoutAValidRange_Is400(string query)
    {
        var response = await _sync.GetDriverSyncReport(FunctionTestHost.Request("GET", $"{Base}/reports/sync{query}"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_ReturnsTheRunsOfTheRange_WithTheirItems()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var run = await RunOf(await SyncNowAsync());
        var from = Uri.EscapeDataString(run.StartedAt.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(run.StartedAt.AddDays(1).ToString("O"));

        var response = await _sync.GetDriverSyncReport(FunctionTestHost.Request("GET", $"{Base}/reports/sync?from={from}&to={to}"), FunctionTestHost.Context);

        var entry = (await response.ReadJsonAsync()).EnumerateArray().Single();
        entry.GetProperty("run").GetProperty("runId").GetGuid().Should().Be(run.RunId);
        entry.GetProperty("run").GetProperty("modelsChecked").GetInt32().Should().Be(1);
        entry.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ModelCheck_ReadsOnlyThatModel_DownloadsOnlyIncludedComponents_AndHasItsOwnLimit()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.OnboardAsync("8C27", "HP EliteBook 860 G11");
        _h.Bed.Vendor.Requests.Clear();

        var run = await RunOf(await CheckModelAsync(model.ModelId));
        var tooSoon = await CheckModelAsync(model.ModelId);

        run.Trigger.Should().Be(DriverSyncTrigger.ModelCheck);
        _h.Bed.Vendor.Requests.Should().NotContain(u => u.ToString() == _h.Adapter.ModelListUrl);
        _h.Bed.Vendor.Requests.Should().NotContain(u => u.ToString().Contains("8c27", StringComparison.Ordinal));
        _h.Handled.Should().NotContain(m => m.Kind == DriverWorkKind.DownloadFile && m.FileKey == "sp202_3.0", "a skipped component is not downloaded");
        tooSoon.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await tooSoon.ReadJsonAsync()).GetProperty("availableAt").GetDateTimeOffset().Should().Be(DriverTestBed.Start.AddMinutes(15));
        tooSoon.Headers.GetValues("Retry-After").Should().ContainSingle();
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.LastRunStartedAt.Should().BeNull("a model check does not move the 60-minute limit");
        (await SyncNowAsync()).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ModelCheck_IsRefusedForADraft_WithSyncOff_AndWhileAnyRunIsActive()
    {
        await _h.EnableAsync();
        var draft = await _h.OnboardAsync("8C27", "HP EliteBook 860 G11", DriverModelState.Draft);
        var active = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var forDraft = await CheckModelAsync(draft.ModelId);
        await SyncNowAsync();
        var whileRunning = await CheckModelAsync(active.ModelId);
        var page = await (await _drivers.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{active.ModelId}"), active.ModelId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();
        await _h.PumpAsync();
        await SetVendorAsync("Hp", false);
        var syncOff = await CheckModelAsync(active.ModelId);

        forDraft.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await forDraft.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Complete onboarding first.");
        whileRunning.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await whileRunning.ReadJsonAsync()).GetProperty("availableAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        page.GetProperty("checkBlockedReason").GetString().Should().Be("Running");
        syncOff.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await syncOff.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Sync is off for HP.");
        (await _h.Bed.Runs.ListAsync()).Should().ContainSingle("no refused check starts a run");
    }

    [Fact]
    public async Task CleanUp_RunsAfterSyncsAndChecks_ButNotAfterAModelCheck()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var orphan = new DriverFileRecord { BlobName = "files/Hp/sp999/1.0/sp999.exe", Sha256 = new string('a', 64), Status = DriverFileStatus.Verified };

        await _h.Bed.Store.Files.UpsertAsync("Hp", "sp999_1.0", orphan);
        await RunOf(await CheckModelAsync(model.ModelId));
        var afterModelCheck = await _h.Bed.Store.Files.GetAsync("Hp", "sp999_1.0");
        await RunOf(await CheckAllAsync());

        afterModelCheck.Should().NotBeNull();
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp999_1.0")).Should().BeNull();
    }

    [Fact]
    public async Task AThirdVendorAdapter_FlowsThroughPolicySyncStorageAndMatching()
    {
        var other = new FakeDriverVendorAdapter(DriverVendor.Lenovo);
        var h = new DriverSyncHarness(other);
        h.PublishModels(other, DriverSyncHarness.CatalogModel("21KC", "ThinkPad T14"));
        h.PublishComponents(other, "21KC", DriverSyncHarness.Component("t-1", "n3xyz", "1.0", "Intel Wireless"), DriverSyncHarness.Component("t-2", "n3abc", "2.0", "Telemetry Agent", "Software"));
        await h.Bed.Store.VendorSettings.UpsertAsync(DriverStore.VendorPartition, "Lenovo", new DriverVendorSettingRecord { SyncEnabled = true });
        var model = await h.OnboardAsync("21KC", "ThinkPad T14", vendor: DriverVendor.Lenovo);
        await h.PolicyService.CreateRuleAsync(new DriverRuleRequest { Name = "No telemetry", Scope = DriverRuleScope.AllVendors, Match = DriverRuleMatch.NamePattern, Value = "*Telemetry*", Action = DriverAction.Skip, Actor = new DriverActor { Upn = Upn } });

        (await h.Sync.StartRunAsync(DriverSyncTrigger.Manual, Upn)).Status.Should().Be(DriverRunStartStatus.Started);
        await h.PumpAsync();
        var resolver = new DriverSetResolver(h.Bed.Store, h.Bed.Models, h.Bed.Pointers, [h.Adapter, other]);
        var pin = await resolver.ResolveAsync(Services.Drivers.DriverSetResolverTests.Device(null, "Lenovo", "21kc"), Services.Drivers.DriverSetResolverTests.Image());

        (await h.CurrentSetAsync(model.ModelId))!.Items.Select(i => i.LineageKey).Should().Equal("t-1");
        pin.ModelId.Should().Be(model.ModelId);
        pin.SetVersion.Should().Be(1);
    }
}

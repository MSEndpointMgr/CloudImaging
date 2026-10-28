using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>With Wait for approval on, a new lineage is held until someone approves it; newer versions of known components never wait.</summary>
public sealed class DriverApprovalFunctionsTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private const string Upn = "operator@contoso.com";
    private static readonly string[] Bluetooth = ["sp500"];
    private static readonly string[] BluetoothAndWireless = ["sp500", "sp100"];
    private readonly DriverSyncHarness _h = new();
    private readonly DriverFunctions _functions;

    public DriverApprovalFunctionsTests()
    {
        _functions = new DriverFunctions(_h.ModelService, _h.PolicyService);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"));
    }

    private async Task<DriverModelRecord> ModelWithANewComponentWaitingAsync(DriverAction defaultAction = DriverAction.Offline)
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Bed.Store.SyncSettings.UpsertAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, (await _h.Bed.Store.GetSyncSettingsAsync()).Value with { WaitForApprovalOfNewComponents = true });
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp210", "1.1", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp500", "sp500", "1.0", "Intel Bluetooth Driver", "Driver - Bluetooth", defaultAction));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        var run = await _h.RunAsync();
        run.Result.Should().Be(DriverSyncResult.Completed);
        _h.Bed.Queues.Sent.Clear();
        return model;
    }

    private async Task<HttpResponseData> ApproveAsync(Guid modelId, object body) =>
        await _functions.ApproveDriverComponents(FunctionTestHost.Request("POST", $"{Base}/models/{modelId}/releases/Windows11_24H2/approvals", body), modelId.ToString(), "Windows11_24H2", FunctionTestHost.Context);

    [Fact]
    public async Task ANewLineage_Waits_IsLeftOutOfTheSet_AndCountsInTheModelStatus_WhileANewerVersionDoesNot()
    {
        var model = await ModelWithANewComponentWaitingAsync();

        var components = await _h.ComponentsAsync(model.ModelId);
        components["sp500"].State.Should().Be(DriverComponentState.WaitingForApproval);
        components["sp100"].State.Should().Be(DriverComponentState.Active);
        var set = await _h.CurrentSetAsync(model.ModelId);
        set!.Items.Should().ContainSingle(i => i.LineageKey == "sp100" && i.Version == "1.1", "a newer version of a known component never waits");
        set.Items.Should().NotContain(i => i.LineageKey == "sp500");
        (await _h.ModelService.ListModelsAsync()).Single().Status.WaitingForApproval.Should().Be(1);
    }

    [Fact]
    public async Task Approve_ConfirmsTheCurrentAction_RebuildsTheSet_AndLogsTheApproval()
    {
        var model = await ModelWithANewComponentWaitingAsync();

        var response = await ApproveAsync(model.ModelId, new { lineageKeys = Bluetooth, actor = new { upn = Upn } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.ComponentsAsync(model.ModelId))["sp500"].State.Should().Be(DriverComponentState.Active);
        (await _h.Bed.Store.Overrides.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release))).Should().BeEmpty("the current action is confirmed, not overridden");
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().Contain(i => i.LineageKey == "sp500");
        var log = (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Select(r => r.Value).ToList();
        log.Should().ContainSingle(l => l.Area == "Approval" && l.By == Upn && l.Subject == "HP EliteBook 840 G11: Intel Bluetooth Driver" && l.Change == "Approved as Offline for Windows 11, version 24H2");
    }

    [Fact]
    public async Task Approve_AsSkip_KeepsTheComponentOutOfTheSet()
    {
        var model = await ModelWithANewComponentWaitingAsync();

        var response = await ApproveAsync(model.ModelId, new { approvals = new[] { new { lineageKey = "sp500", action = "Skip" } }, actor = new { upn = Upn } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await response.ReadJsonAsync()).GetProperty("components").EnumerateArray().Single(c => c.GetProperty("lineageKey").GetString() == "sp500");
        row.GetProperty("state").GetString().Should().Be("Active");
        row.GetProperty("action").GetString().Should().Be("Skip");
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().NotContain(i => i.LineageKey == "sp500");
    }

    [Fact]
    public async Task Approve_AComponentThatIsNotWaiting_Is409_AndChangesNothing()
    {
        var model = await ModelWithANewComponentWaitingAsync();

        var response = await ApproveAsync(model.ModelId, new { lineageKeys = BluetoothAndWireless, actor = new { upn = Upn } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _h.ComponentsAsync(model.ModelId))["sp500"].State.Should().Be(DriverComponentState.WaitingForApproval);
    }

    [Fact]
    public async Task Approve_WithoutAnActor_Is400()
    {
        var model = await ModelWithANewComponentWaitingAsync();

        (await ApproveAsync(model.ModelId, new { lineageKeys = Bluetooth })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithTheSettingOff_NewComponentsEnterTheSet_AndAreRecordedAsNew()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"), DriverSyncHarness.Component("sp500", "sp500", "1.0", "Intel Bluetooth Driver", "Driver - Bluetooth"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        var run = await _h.RunAsync();

        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.New && i.LineageKey == "sp500");
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().Contain(i => i.LineageKey == "sp500");
    }
}

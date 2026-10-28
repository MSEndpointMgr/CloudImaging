using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Each component's version history, and rolling one component back while the others keep updating.</summary>
public sealed class DriverComponentHistoryTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private const string Upn = "operator@contoso.com";
    private const string Release = "Windows11_24H2";
    private readonly DriverSyncHarness _h = new();
    private readonly DriverComponentHistoryFunctions _functions;
    private DriverModelRecord _model = null!;

    public DriverComponentHistoryTests()
    {
        _functions = new DriverComponentHistoryFunctions(_h.History);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
    }

    private static string Id(string version) => version == "1.0" ? "sp200" : $"sp2{version.Replace(".", string.Empty, StringComparison.Ordinal)}";

    private static string Url(string version) => $"https://ftp.hp.com/pub/softpaq/{Id(version)}.exe";

    private void Publish(string wireless, string audio = "2.0") =>
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", Id(wireless), wireless, "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", $"sp3{audio.Replace(".", string.Empty, StringComparison.Ordinal)}", audio, "Realtek Audio Driver", "Driver - Audio"));

    private async Task SyncAsync(string wireless, string audio = "2.0")
    {
        Publish(wireless, audio);
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();
    }

    /// <summary>Versions 1.0, 1.1 and 1.2 of the wireless driver; with one previous version kept, 1.0 is no longer stored.</summary>
    private async Task ThreeVersionsAsync()
    {
        Publish("1.0");
        await _h.EnableAsync();
        _model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await SyncAsync("1.1");
        await SyncAsync("1.2");
    }

    private async Task<HttpResponseData> VersionsAsync(bool check = false) =>
        await _functions.GetDriverComponentVersions(FunctionTestHost.Request("GET", $"{Base}/models/{_model.ModelId}/releases/{Release}/components/sp100/versions{(check ? "?check=true" : string.Empty)}"), _model.ModelId.ToString(), Release, "sp100", FunctionTestHost.Context);

    private async Task<HttpResponseData> RollBackAsync(string version, string until = "NewerVersion") =>
        await _functions.SetDriverComponentRollBack(FunctionTestHost.Request("PUT", $"{Base}/models/{_model.ModelId}/releases/{Release}/components/sp100/rollback", new { version, until, actor = new { upn = Upn } }), _model.ModelId.ToString(), Release, "sp100", FunctionTestHost.Context);

    private async Task<string?> CurrentWirelessAsync() => (await _h.CurrentSetAsync(_model.ModelId))!.Items.Single(i => i.LineageKey == "sp100").Version;

    [Fact]
    public async Task EachVersion_HasAHistoryRow_WithItsSets_Dates_WhatChangedIt_StatusAndSize()
    {
        await ThreeVersionsAsync();

        var rows = (await (await VersionsAsync()).ReadJsonAsync()).EnumerateArray().ToList();

        rows.Select(r => r.GetProperty("version").GetString()).Should().Equal("1.2", "1.1", "1.0");
        rows[0].GetProperty("firstSet").GetInt32().Should().Be(3);
        rows[0].GetProperty("lastSet").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        rows[0].GetProperty("to").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        rows[0].GetProperty("changedBy").GetString().Should().Be("Sync");
        rows[1].GetProperty("firstSet").GetInt32().Should().Be(2);
        rows[1].GetProperty("lastSet").GetInt32().Should().Be(2);
        rows[2].GetProperty("changedBy").GetString().Should().Be($"Onboarding by {Upn}");
        rows.Select(r => r.GetProperty("status").GetString()).Should().Equal("Stored", "Stored", "DownloadsOnRollBack");
        rows[2].GetProperty("sizeBytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length);
    }

    [Fact]
    public async Task Check_AsksTheVendorOnlyAboutVersionsThatAreNotStored_AndRecordsTheAnswer()
    {
        await ThreeVersionsAsync();
        _h.Bed.Vendor.ServeStatus(Url("1.0"), HttpStatusCode.NotFound);
        var before = _h.Bed.Vendor.Requests.Count;

        var rows = (await (await VersionsAsync(check: true)).ReadJsonAsync()).EnumerateArray().ToList();

        rows.Single(r => r.GetProperty("version").GetString() == "1.0").GetProperty("status").GetString().Should().Be("Unavailable");
        _h.Bed.Vendor.Requests.Skip(before).Select(u => u.ToString()).Should().Equal(Url("1.0"));
        var stored = (await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(_model.ModelId, DriverSyncHarness.Release))).Single(r => r.Value.Version == "1.0").Value;
        stored.Available.Should().BeFalse();
        stored.AvailabilityCheckedAt.Should().Be(_h.Bed.Time.GetUtcNow());
    }

    [Fact]
    public async Task RollBack_ToAVersionNoLongerStored_DownloadsAndVerifiesIt_HoldsIt_WhileOthersUpdate_UntilTheVendorMovesOn()
    {
        await ThreeVersionsAsync();

        var response = await RollBackAsync("1.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.ReadJsonAsync();
        row.GetProperty("source").GetString().Should().Be("RolledBack");
        row.GetProperty("version").GetString().Should().Be("1.0");
        row.GetProperty("rollBack").GetProperty("skippedVersion").GetString().Should().Be("1.2");
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0"))!.Value.Status.Should().Be(DriverFileStatus.Verified);
        (await _h.Bed.Pointers.GetAsync(_model.ModelId, DriverSyncHarness.Release))!.Value.Dirty.Should().BeTrue();
        (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Select(r => r.Value)
            .Should().ContainSingle(l => l.Area == "Component" && l.By == Upn && l.Change.StartsWith("Rolled back to 1.0 until HP publishes a version newer than 1.2", StringComparison.Ordinal));

        await _h.Sync.CleanUpAsync();
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0")).Should().NotBeNull("clean-up keeps the held version");
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        (await CurrentWirelessAsync()).Should().Be("1.0");
        (await _h.CurrentSetAsync(_model.ModelId))!.Items.Single(i => i.LineageKey == "sp100").Origin.Should().Be(DriverComponentOrigin.RolledBack);

        await SyncAsync("1.2", audio: "2.1");
        var set = (await _h.CurrentSetAsync(_model.ModelId))!;
        set.Items.Single(i => i.LineageKey == "sp100").Version.Should().Be("1.0");
        set.Items.Single(i => i.LineageKey == "sp101").Version.Should().Be("2.1", "other components keep updating");

        await SyncAsync("1.3", audio: "2.1");
        (await CurrentWirelessAsync()).Should().Be("1.3", "a version newer than the skipped one ends the roll back");
        (await _h.Bed.Store.RollBacks.ListPartitionAsync(DriverTables.Pair(_model.ModelId, DriverSyncHarness.Release))).Should().BeEmpty();
    }

    [Fact]
    public async Task RollBack_UntilUndone_HoldsThroughNewerVersions_UntilTheDelete()
    {
        await ThreeVersionsAsync();
        (await RollBackAsync("1.1", "Undone")).StatusCode.Should().Be(HttpStatusCode.OK);
        await SyncAsync("1.3");
        (await CurrentWirelessAsync()).Should().Be("1.1");

        var undo = await _functions.ClearDriverComponentRollBack(FunctionTestHost.Request("DELETE", $"{Base}/models/{_model.ModelId}/releases/{Release}/components/sp100/rollback?changedBy={Upn}"), _model.ModelId.ToString(), Release, "sp100", FunctionTestHost.Context);

        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        (await undo.ReadJsonAsync()).GetProperty("source").GetString().Should().Be("Default");
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        (await CurrentWirelessAsync()).Should().Be("1.3");
        var again = await _functions.ClearDriverComponentRollBack(FunctionTestHost.Request("DELETE", $"{Base}/models/{_model.ModelId}/releases/{Release}/components/sp100/rollback?changedBy={Upn}"), _model.ModelId.ToString(), Release, "sp100", FunctionTestHost.Context);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RollBack_WhenTheVersionCannotBeDownloadedAgain_Is409WithTheReason_AndWritesNothing()
    {
        await ThreeVersionsAsync();
        _h.Bed.Vendor.ServeBytes(Url("1.0"), "tampered"u8.ToArray());

        var response = await RollBackAsync("1.0");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadBodyAsync()).Should().Contain("could not be downloaded again from HP");
        (await _h.Bed.Store.RollBacks.ListPartitionAsync(DriverTables.Pair(_model.ModelId, DriverSyncHarness.Release))).Should().BeEmpty();
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0")).Should().BeNull();
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("9.9")]
    public async Task RollBack_ToTheCurrentOrAnUnknownVersion_Is400(string version)
    {
        await ThreeVersionsAsync();

        (await RollBackAsync(version)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

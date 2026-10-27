using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Storage, downloads, roll back and removal of models, releases and withdrawn components.</summary>
public sealed class DriverLifecycleFunctionsTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private const string Upn = "operator@contoso.com";
    private const string Release = "Windows11_24H2";
    private readonly DriverSyncHarness _h = new();
    private readonly DriverLifecycleFunctions _functions;

    public DriverLifecycleFunctionsTests()
    {
        _functions = new DriverLifecycleFunctions(_h.Lifecycle);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"), DriverSyncHarness.CatalogModel("8C27", "HP EliteBook 860 G11"));
        PublishVersion("8C26", "1.0");
    }

    private void PublishVersion(string matchKey, string wirelessVersion, bool withAudio = true)
    {
        var wireless = DriverSyncHarness.Component("sp100", $"sp2{wirelessVersion.Replace(".", string.Empty, StringComparison.Ordinal)}", wirelessVersion, "Intel Wireless LAN Driver");
        _h.PublishComponents(matchKey, withAudio ? [wireless, DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio")] : [wireless]);
    }

    private async Task<DriverModelRecord> ModelWithASetAsync()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        return model;
    }

    private async Task NextRunAsync()
    {
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        (await _h.RunAsync()).Result.Should().Be(DriverSyncResult.Completed);
    }

    private List<DriverChangeLogRecord> Log() =>
        _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition).GetAwaiter().GetResult().Select(r => r.Value).ToList();

    [Fact]
    public async Task Storage_CountsEveryFileOnce_PerVendorAndPerModel()
    {
        var model = await ModelWithASetAsync();
        var bytes = DriverSyncHarness.Content("sp200", "1.0").Length + DriverSyncHarness.Content("sp201", "2.0").Length;

        var json = await (await _functions.GetDriverStorage(FunctionTestHost.Request("GET", $"{Base}/storage"), FunctionTestHost.Context)).ReadJsonAsync();

        json.GetProperty("totalBytes").GetInt64().Should().Be(bytes);
        json.GetProperty("vendors").EnumerateArray().Single().GetProperty("bytes").GetInt64().Should().Be(bytes);
        var row = json.GetProperty("models").EnumerateArray().Single();
        row.GetProperty("modelId").GetGuid().Should().Be(model.ModelId);
        row.GetProperty("sizeBytes").GetInt64().Should().Be(bytes);
        row.GetProperty("sets").GetProperty("current").GetInt32().Should().Be(1);
        row.GetProperty("sets").GetProperty("rollBack").GetInt32().Should().Be(0);
        json.GetProperty("previousVersionsToKeep").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Storage_CountsASharedFileOnce_AndMarksAKeptPreviousVersion()
    {
        await _functions.SetDriverStorageSettings(FunctionTestHost.Request("PUT", $"{Base}/storage/settings", new { previousVersionsToKeep = 2, actor = new { upn = Upn } }), FunctionTestHost.Context);
        var model = await ModelWithASetAsync();
        _h.PublishComponents("8C27", DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
        await _h.OnboardAsync("8C27", "HP EliteBook 860 G11");
        foreach (var version in new[] { "1.1", "1.2" })
        {
            PublishVersion("8C26", version);
            await NextRunAsync();
        }

        var storage = await (await _functions.GetDriverStorage(FunctionTestHost.Request("GET", $"{Base}/storage"), FunctionTestHost.Context)).ReadJsonAsync();
        var audio = DriverSyncHarness.Content("sp201", "2.0").Length;
        var wireless = DriverSyncHarness.Content("sp200", "1.0").Length + DriverSyncHarness.Content("sp211", "1.1").Length + DriverSyncHarness.Content("sp212", "1.2").Length;

        storage.GetProperty("totalBytes").GetInt64().Should().Be(audio + wireless, "the shared audio file is stored once");
        storage.GetProperty("previousVersionsBytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length, "1.1 is kept by the roll-back set anyway");
        storage.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("sizeBytes").GetInt64()).Should().BeEquivalentTo([audio + wireless, (long)audio]);
        var detail = await (await _functions.GetDriverStorageModel(FunctionTestHost.Request("GET", $"{Base}/storage/models/{model.ModelId}"), model.ModelId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();
        var components = detail.GetProperty("releases").EnumerateArray().Single().GetProperty("components").EnumerateArray().ToList();
        components.Single(c => c.GetProperty("version").GetString() == "1.0").GetProperty("sets").EnumerateArray().Select(s => s.GetString()).Should().Equal("previous");
        components.Single(c => c.GetProperty("name").GetString() == "Realtek Audio Driver").GetProperty("sharedWithModels").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task StorageModel_ListsEachComponentWithTheSetsThatUseIt()
    {
        var model = await ModelWithASetAsync();
        PublishVersion("8C26", "1.1");
        await NextRunAsync();

        var response = await _functions.GetDriverStorageModel(FunctionTestHost.Request("GET", $"{Base}/storage/models/{model.ModelId}"), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var release = (await response.ReadJsonAsync()).GetProperty("releases").EnumerateArray().Single();
        release.GetProperty("current").GetProperty("version").GetInt32().Should().Be(2);
        release.GetProperty("rollBack").GetProperty("version").GetInt32().Should().Be(1);
        var components = release.GetProperty("components").EnumerateArray().ToList();
        components.Single(c => c.GetProperty("version").GetString() == "1.0").GetProperty("sets").EnumerateArray().Select(s => s.GetString()).Should().Equal("1");
        components.Single(c => c.GetProperty("version").GetString() == "1.1").GetProperty("sets").EnumerateArray().Select(s => s.GetString()).Should().Equal("2");
        components.Single(c => c.GetProperty("name").GetString() == "Realtek Audio Driver").GetProperty("sets").EnumerateArray().Select(s => s.GetString()).Should().Equal("2", "1");
        components.Should().OnlyContain(c => c.GetProperty("status").GetString() == "Stored");
        (await _functions.GetDriverStorageModel(FunctionTestHost.Request("GET", $"{Base}/storage/models/{Guid.NewGuid()}"), Guid.NewGuid().ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StorageSettings_ValidatesTheNumber_AndLogsTheChange()
    {
        var bad = await _functions.SetDriverStorageSettings(FunctionTestHost.Request("PUT", $"{Base}/storage/settings", new { previousVersionsToKeep = 5, actor = new { upn = Upn } }), FunctionTestHost.Context);
        var noActor = await _functions.SetDriverStorageSettings(FunctionTestHost.Request("PUT", $"{Base}/storage/settings", new { previousVersionsToKeep = 2 }), FunctionTestHost.Context);
        var ok = await _functions.SetDriverStorageSettings(FunctionTestHost.Request("PUT", $"{Base}/storage/settings", new { previousVersionsToKeep = 2, actor = new { upn = Upn } }), FunctionTestHost.Context);

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.ReadBodyAsync()).Should().Contain("Choose a number from the list.");
        noActor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.ReadJsonAsync()).GetProperty("previousVersionsToKeep").GetInt32().Should().Be(2);
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.PreviousVersionsToKeep.Should().Be(2);
        Log().Should().ContainSingle(l => l.Area == "Storage" && l.Subject == "Previous versions" && l.Change == "1 to 2" && l.By == Upn);
    }

    [Theory]
    [InlineData(0, new[] { "sp212_1.2", "sp213_1.3" }, new[] { "sp200_1.0", "sp211_1.1" })]
    [InlineData(1, new[] { "sp212_1.2", "sp213_1.3" }, new[] { "sp200_1.0", "sp211_1.1" })]
    [InlineData(2, new[] { "sp211_1.1", "sp212_1.2", "sp213_1.3" }, new[] { "sp200_1.0" })]
    public async Task CleanUp_KeepsTheChosenNumberOfPreviousVersions(int keep, string[] kept, string[] deleted)
    {
        await _functions.SetDriverStorageSettings(FunctionTestHost.Request("PUT", $"{Base}/storage/settings", new { previousVersionsToKeep = keep, actor = new { upn = Upn } }), FunctionTestHost.Context);
        await ModelWithASetAsync();
        foreach (var version in new[] { "1.1", "1.2", "1.3" })
        {
            PublishVersion("8C26", version);
            await NextRunAsync();
        }

        foreach (var key in kept)
        {
            (await _h.Bed.Store.Files.GetAsync("Hp", key)).Should().NotBeNull($"{key} is kept with {keep} previous versions");
        }

        foreach (var key in deleted)
        {
            (await _h.Bed.Store.Files.GetAsync("Hp", key)).Should().BeNull($"{key} is not kept with {keep} previous versions");
        }
    }

    [Fact]
    public async Task Downloads_ShowsThePendingSetWithItsFailure_AndFinishedBuildsOfTheLastSevenDays()
    {
        var model = await ModelWithASetAsync();
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp211", "1.1", "Intel Wireless LAN Driver"), DriverSyncHarness.Component("sp101", "sp202", "2.1", "Realtek Audio Driver", "Driver - Audio"));
        _h.Bed.Vendor.ServeBytes("https://ftp.hp.com/pub/softpaq/sp202.exe", "tampered"u8.ToArray());
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        // The wireless download is held back, so the set stays pending while the audio file fails.
        await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, Upn);
        var held = new List<DriverWorkMessage>();
        for (var batch = _h.Bed.Queues.Drain(); batch.Count > 0; batch = _h.Bed.Queues.Drain())
        {
            foreach (var sent in batch)
            {
                if (sent.Work is { Kind: DriverWorkKind.DownloadFile, FileKey: "sp211_1.1" })
                {
                    held.Add(sent.Work);
                }
                else if (sent.Delay is null || sent.Delay <= TimeSpan.Zero)
                {
                    await _h.Sync.HandleAsync(sent.Work);
                }
            }
        }

        var json = await (await _functions.GetDriverDownloads(FunctionTestHost.Request("GET", $"{Base}/downloads"), FunctionTestHost.Context)).ReadJsonAsync();

        held.Should().ContainSingle();
        var pending = json.GetProperty("inProgress").EnumerateArray().Single();
        pending.GetProperty("modelId").GetGuid().Should().Be(model.ModelId);
        pending.GetProperty("setVersion").GetInt32().Should().Be(2);
        pending.GetProperty("status").GetString().Should().Be("Failed");
        pending.GetProperty("failed").GetInt32().Should().Be(1);
        pending.GetProperty("components").GetInt32().Should().Be(2);
        pending.GetProperty("componentsDone").GetInt32().Should().Be(1);
        pending.GetProperty("reasons").EnumerateArray().Single().GetString().Should().StartWith("Realtek Audio Driver: ");
        json.GetProperty("finished").EnumerateArray().Single().GetProperty("setVersion").GetInt32().Should().Be(1);

        _h.Bed.Time.Advance(TimeSpan.FromDays(8));
        var later = await (await _functions.GetDriverDownloads(FunctionTestHost.Request("GET", $"{Base}/downloads"), FunctionTestHost.Context)).ReadJsonAsync();
        later.GetProperty("finished").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task RollBack_SwapsCurrentAndPrevious_Holds_AndIsLogged()
    {
        var model = await ModelWithASetAsync();
        PublishVersion("8C26", "1.1");
        await NextRunAsync();

        var response = await _functions.RollBackDriverSet(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/releases/{Release}/rollback", new { actor = new { upn = Upn } }), model.ModelId.ToString(), Release, FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.CurrentVersion.Should().Be(1);
        pointer.PreviousVersion.Should().Be(2);
        Log().Should().ContainSingle(l => l.Area == "RollBack" && l.Subject == "HP EliteBook 840 G11" && l.Change == "Driver set 2 to 1 for Windows 11, version 24H2");

        await NextRunAsync();
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.CurrentVersion.Should().Be(1, "the same catalog does not undo a roll back");
    }

    [Fact]
    public async Task RollBack_WithoutAPreviousSet_Is409_AndWithoutAnActorIs400()
    {
        var model = await ModelWithASetAsync();

        (await _functions.RollBackDriverSet(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/releases/{Release}/rollback", new { actor = new { upn = Upn } }), model.ModelId.ToString(), Release, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _functions.RollBackDriverSet(FunctionTestHost.Request("POST", $"{Base}/models/{model.ModelId}/releases/{Release}/rollback", new { }), model.ModelId.ToString(), Release, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemovalImpact_CountsOnlyBytesNoOtherModelUses()
    {
        var model = await ModelWithASetAsync();
        _h.PublishComponents("8C27", DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
        await _h.OnboardAsync("8C27", "HP EliteBook 860 G11");
        await NextRunAsync();

        var json = await (await _functions.GetDriverRemovalImpact(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}/removal-impact"), model.ModelId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();

        json.GetProperty("freedBytes").GetInt64().Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length, "the audio driver is shared with the other model");
        json.GetProperty("sets").GetInt32().Should().Be(1);
        json.GetProperty("runningSessions").GetInt32().Should().Be(0);
        (await _functions.GetDriverRemovalImpact(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}/removal-impact?release=Nope"), model.ModelId.ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemoveModel_StopsMatching_KeepsThePinnedSet_AndFreesFilesAtCleanUp()
    {
        var model = await ModelWithASetAsync();
        var sessionId = Guid.NewGuid();
        await _h.Sessions.CreateAsync(new DeviceSession
        {
            SessionId = sessionId,
            DeviceSerialNumber = "5CG1234",
            DeviceManufacturer = "HP",
            DeviceModel = "HP EliteBook 840 G11",
            State = SessionState.SessionStarted,
            DriverModelId = model.ModelId,
            DriverRelease = DriverSyncHarness.Release,
            DriverSetVersion = 1,
        });
        var impact = await (await _functions.GetDriverRemovalImpact(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}/removal-impact"), model.ModelId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();
        impact.GetProperty("runningSessions").GetInt32().Should().Be(1);

        var response = await _functions.RemoveDriverModel(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}?changedBy={Upn}"), model.ModelId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.Bed.Models.GetAsync(model.ModelId)).Should().BeNull();
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release)).Should().BeNull();
        (await _h.ComponentsAsync(model.ModelId)).Should().BeEmpty();
        (await _h.Bed.SetDocuments.ListAsync()).Should().ContainSingle(d => d.Version == 1, "a running deployment still uses it");
        Log().Should().ContainSingle(l => l.Area == "Model" && l.Subject == "HP EliteBook 840 G11" && l.Change == "Removed");

        await _h.Sync.CleanUpAsync();
        (await _h.Bed.Store.Files.ListAllAsync()).Should().NotBeEmpty("the pinned set keeps its files");
        await _h.Sessions.UpdateAsync((await _h.Sessions.GetByIdAsync(sessionId))! with { State = SessionState.SessionCompleted });
        await _h.Sync.CleanUpAsync();
        (await _h.Bed.Store.Files.ListAllAsync()).Should().BeEmpty();
        (await _h.Bed.SetDocuments.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveModel_WithoutChangedBy_Is400_AndAnUnknownModelIs404()
    {
        var model = await ModelWithASetAsync();

        (await _functions.RemoveDriverModel(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}"), model.ModelId.ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _functions.RemoveDriverModel(FunctionTestHost.Request("DELETE", $"{Base}/models/{Guid.NewGuid()}?changedBy={Upn}"), Guid.NewGuid().ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveRelease_RemovesOneRelease_ButNotTheLastOne()
    {
        var model = await ModelWithASetAsync();

        var last = await _functions.RemoveDriverModelRelease(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/{Release}?changedBy={Upn}"), model.ModelId.ToString(), Release, FunctionTestHost.Context);
        last.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await last.ReadBodyAsync()).Should().Contain("Remove the model instead.");

        var row = (await _h.Bed.Models.GetAsync(model.ModelId))!;
        (await _h.Bed.Models.TryUpdateAsync(row.Value with { Releases = [DriverSyncHarness.Release, WindowsRelease.Windows11Version25H2] }, row.ETag)).Should().BeTrue();
        var response = await _functions.RemoveDriverModelRelease(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/{Release}?changedBy={Upn}"), model.ModelId.ToString(), Release, FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.Bed.Models.GetAsync(model.ModelId))!.Value.Releases.Should().Equal(WindowsRelease.Windows11Version25H2);
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release)).Should().BeNull();
        Log().Should().ContainSingle(l => l.Area == "Release" && l.Change == "Removed Windows 11, version 24H2");
    }

    [Fact]
    public async Task RemoveComponent_OnlyForAWithdrawnComponent_AndQueuesARebuild()
    {
        var model = await ModelWithASetAsync();
        PublishVersion("8C26", "1.0", withAudio: false);
        await NextRunAsync();
        (await _h.ComponentsAsync(model.ModelId))["sp101"].State.Should().Be(DriverComponentState.Withdrawn);

        var active = await _functions.RemoveWithdrawnDriverComponent(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/{Release}/components/sp100?changedBy={Upn}"), model.ModelId.ToString(), Release, "sp100", FunctionTestHost.Context);
        var withdrawn = await _functions.RemoveWithdrawnDriverComponent(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/{Release}/components/sp101?changedBy={Upn}"), model.ModelId.ToString(), Release, "sp101", FunctionTestHost.Context);

        active.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.ComponentsAsync(model.ModelId)).Keys.Should().Equal("sp100");
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.Dirty.Should().BeTrue();
        Log().Should().ContainSingle(l => l.Area == "Component" && l.Subject == "HP EliteBook 840 G11: Realtek Audio Driver");
    }
}

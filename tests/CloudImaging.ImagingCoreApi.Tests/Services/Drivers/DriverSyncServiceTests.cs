using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverSyncServiceTests
{
    private readonly DriverSyncHarness _h = new();

    public DriverSyncServiceTests()
    {
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"), DriverSyncHarness.CatalogModel("8C27", "HP EliteBook 860 G11"));
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
    }

    [Fact]
    public async Task ManualRun_RefreshesModelList_AndRecordsNewModels()
    {
        await _h.EnableAsync();

        var run = await _h.RunAsync(DriverSyncTrigger.Manual);

        _h.Bed.Vendor.Requests.Should().Contain(u => u.ToString() == _h.Adapter.ModelListUrl);
        run.Vendors.Should().ContainSingle(v => v.Vendor == DriverVendor.Hp && v.Processed && v.NewModels == 2);
        (await _h.Bed.Store.CatalogModels.ListPartitionAsync("Hp")).Select(r => r.RowKey).Should().BeEquivalentTo("8C26_X64", "8C27_X64");
        (await _h.Bed.Store.CatalogLists.GetAsync(DriverStore.ListPartition, "Hp"))!.Value.FetchedAt.Should().Be(DriverTestBed.Start);
    }

    [Fact]
    public async Task CheckRun_ReadsNoModelList()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var run = await _h.RunAsync(DriverSyncTrigger.Check);

        _h.Bed.Vendor.Requests.Should().NotContain(u => u.ToString() == _h.Adapter.ModelListUrl);
        run.Vendors.Should().ContainSingle(v => v.Processed && v.NewModels == 0);
        run.Result.Should().Be(DriverSyncResult.Completed);
    }

    [Fact]
    public async Task Run_ResolvesDownloadsAndBuildsTheSet_WithBuildAndVersionRows()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var run = await _h.RunAsync();

        run.Result.Should().Be(DriverSyncResult.Completed);
        run.FinishedAt.Should().NotBeNull();
        var set = await _h.CurrentSetAsync(model.ModelId);
        set.Should().NotBeNull();
        set!.Version.Should().Be(1);
        set.Items.Select(i => i.ComponentId).Should().BeEquivalentTo("sp201", "sp200");
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0"))!.Value.Status.Should().Be(DriverFileStatus.Verified);
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp202_3.0")).Should().BeNull("a skipped component is never downloaded");
        _h.Bed.Blobs.Blobs.Keys.Should().Contain("files/Hp/sp200/1.0/sp200.exe");

        var builds = await _h.Bed.Store.SetBuilds.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release));
        builds.Should().ContainSingle();
        builds[0].Value.Should().Match<DriverSetBuildRecord>(b => b.Trigger == "Onboarding" && b.ReadyAt != null && b.Components == 2 && b.NewComponents == 2);
        var versions = await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release));
        versions.Select(v => (v.Value.ComponentId, v.Value.FirstSet, v.Value.LastSet)).Should().BeEquivalentTo(new[] { ("sp200", 1, (int?)null), ("sp201", 1, (int?)null) });
        (await _h.Bed.Runs.GetActiveAsync()).Should().BeNull("the lock is released");
    }

    [Fact]
    public async Task SecondRun_NewVersion_IsUpdatedItem_NewSetAndClosesTheOldVersionRow()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        var run = await _h.RunAsync();

        var items = await _h.Bed.Runs.ListItemsAsync(run.RunId);
        items.Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.Updated && i.OldVersion == "1.0" && i.NewVersion == "1.1" && i.ModelName == "HP EliteBook 840 G11");
        run.Counts.Should().ContainKey(DriverSyncItemOutcome.Updated).WhoseValue.Should().Be(1);
        var set = await _h.CurrentSetAsync(model.ModelId);
        set!.Version.Should().Be(2);
        set.Items.Select(i => i.ComponentId).Should().Contain("sp300");
        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.PreviousVersion.Should().Be(1);
        var versions = (await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release))).Select(v => v.Value).ToList();
        versions.Should().ContainSingle(v => v.ComponentId == "sp200" && v.LastSet == 1 && v.To != null);
        versions.Should().ContainSingle(v => v.ComponentId == "sp300" && v.FirstSet == 2 && v.ChangedBy == "Sync");
        (await _h.Bed.Store.SetBuilds.GetAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release), "000002"))!.Value.Should().Match<DriverSetBuildRecord>(b => b.Trigger == "Sync" && b.NewComponents == 1 && b.ReusedComponents == 1);
    }

    [Fact]
    public async Task WaitForTesting_ANewerSetWaitsInTesting_IsKept_AndReleaseMakesItCurrent()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Bed.Store.SyncSettings.UpsertAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, (await _h.Bed.Store.GetSyncSettingsAsync()).Value with { WaitForTesting = true });
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        await _h.RunAsync();

        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.CurrentVersion.Should().Be(1, "production keeps the current set");
        pointer.TestingVersion.Should().Be(2);
        (await _h.Bed.SetDocuments.GetAsync(model.ModelId, DriverSyncHarness.Release, 2))!.Items.Select(i => i.ComponentId).Should().Contain("sp300");
        (await _h.Sync.KeptSetsAsync()).Should().Contain((model.ModelId, DriverSyncHarness.Release, 2));
        var versions = (await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release))).Select(v => v.Value).ToList();
        versions.Should().NotContain(v => v.ComponentId == "sp300", "history follows the current set");

        (await _h.Sync.ReleaseTestingAsync(model.ModelId, DriverSyncHarness.Release)).Should().Be(2);

        var released = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        released.CurrentVersion.Should().Be(2);
        released.PreviousVersion.Should().Be(1);
        released.TestingVersion.Should().BeNull();
        versions = (await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release))).Select(v => v.Value).ToList();
        versions.Should().ContainSingle(v => v.ComponentId == "sp300" && v.FirstSet == 2);
        versions.Should().ContainSingle(v => v.ComponentId == "sp200" && v.LastSet == 1);
        (await _h.Sync.ReleaseTestingAsync(model.ModelId, DriverSyncHarness.Release)).Should().BeNull("nothing is in testing any more");
    }

    [Fact]
    public async Task WaitForTesting_TheFirstSetOfANewModelWaitsInTesting_AndNothingIsCurrent()
    {
        await _h.EnableAsync();
        await _h.Bed.Store.SyncSettings.UpsertAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, (await _h.Bed.Store.GetSyncSettingsAsync()).Value with { WaitForTesting = true });
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        await _h.RunAsync();

        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.CurrentVersion.Should().BeNull();
        pointer.TestingVersion.Should().Be(1);
        (await _h.Bed.Store.SetBuilds.GetAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release), "000001"))!.Value.Should().Match<DriverSetBuildRecord>(b => b.Trigger == "Onboarding" && b.ReadyAt != null);
    }

    [Fact]
    public async Task WithdrawnComponent_StaysAtLastVerifiedVersion_AndRepublishingClearsTheState()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan", "Software - Enabling", DriverAction.Skip));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        var run = await _h.RunAsync();

        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.Withdrawn && i.LineageKey == "sp100" && i.OldVersion == "1.0");
        (await _h.ComponentsAsync(model.ModelId))["sp100"].State.Should().Be(DriverComponentState.Withdrawn);
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().Contain(i => i.LineageKey == "sp100" && i.Version == "1.0");
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.CurrentVersion.Should().Be(1, "the set did not change");

        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp400", "1.2", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();

        var republished = (await _h.ComponentsAsync(model.ModelId))["sp100"];
        republished.State.Should().Be(DriverComponentState.Active);
        republished.Version.Should().Be("1.2");
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().Contain(i => i.LineageKey == "sp100" && i.Version == "1.2");
    }

    [Fact]
    public async Task NewLineage_WaitsForApproval_WhenTheSettingIsOn()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Bed.Store.SyncSettings.UpsertAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, (await _h.Bed.Store.GetSyncSettingsAsync()).Value with { WaitForApprovalOfNewComponents = true });
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp500", "sp500", "1.0", "Intel Bluetooth Driver", "Driver - Bluetooth"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));

        var run = await _h.RunAsync();

        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.NewWaitingForApproval && i.LineageKey == "sp500");
        (await _h.ComponentsAsync(model.ModelId))["sp500"].State.Should().Be(DriverComponentState.WaitingForApproval);
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().NotContain(i => i.LineageKey == "sp500");
        run.Result.Should().Be(DriverSyncResult.Completed, "a component waiting for approval does not affect the result");
    }

    [Fact]
    public async Task EachStep_IsOneMessage_AndOnlyTheLastEnqueuesFinalizeRun()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var run = await _h.RunAsync();

        _h.Handled.Select(m => m.Kind).Should().StartWith([DriverWorkKind.FetchVendorCatalog, DriverWorkKind.ResolveModel, DriverWorkKind.BuildSet]);
        _h.Handled.Count(m => m.Kind == DriverWorkKind.DownloadFile).Should().Be(2);
        _h.Handled.Count(m => m.Kind == DriverWorkKind.FinalizeRun).Should().Be(1);
        _h.Handled.Last().Kind.Should().Be(DriverWorkKind.FinalizeRun);
        _h.Handled.Should().OnlyContain(m => m.RunId == run.RunId);
        run.Outstanding.Should().Be(0);
        _h.Delayed.Should().ContainSingle(d => d.Work.Kind == DriverWorkKind.FinalizeRun && d.Delay == TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task MessageHandledTwice_ChangesNothingTheSecondTime()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        var fetch = _h.Bed.Queues.Drain().Single(m => m.Delay is null).Work;
        _h.Delayed.Clear();

        await _h.Sync.HandleAsync(fetch);
        var afterFirst = (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value.Outstanding;
        var children = _h.Bed.Queues.Drain().Where(m => m.Delay is null).ToList();
        await _h.Sync.HandleAsync(fetch);

        _h.Bed.Queues.Sent.Should().BeEmpty();
        (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value.Outstanding.Should().Be(afterFirst);
        children.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.ResolveModel && m.Work.ModelId == model.ModelId);
        afterFirst.Should().Be(1);
    }

    [Fact]
    public async Task PoisonMessage_RecordsItsItemAsFailed_AndTheRunStillFinishes()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");

        // Run everything, but hand the first download to the poison handler as the host would after 3 failures.
        var poisoned = false;
        while (_h.Bed.Queues.Drain() is { Count: > 0 } batch)
        {
            foreach (var sent in batch.Where(m => m.Delay is null))
            {
                if (!poisoned && sent.Work.Kind == DriverWorkKind.DownloadFile)
                {
                    poisoned = true;
                    await _h.Sync.HandlePoisonAsync(sent.Work);
                    continue;
                }

                await _h.Sync.HandleAsync(sent.Work);
            }
        }

        var run = (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value;
        run.Result.Should().Be(DriverSyncResult.PartlyFailed);
        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.Failed);
        (await _h.Bed.Runs.GetActiveAsync()).Should().BeNull();
    }

    [Fact]
    public async Task PoisonedDownload_BuildsThePendingSetWaitingOnIt_InsteadOfLeavingItUnbuilt()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        var poisoned = false;
        var builtAfterPoison = false;
        while (_h.Bed.Queues.Drain() is { Count: > 0 } batch)
        {
            foreach (var sent in batch.Where(m => m.Delay is null))
            {
                if (!poisoned && sent.Work.Kind == DriverWorkKind.DownloadFile)
                {
                    poisoned = true;
                    await _h.Sync.HandlePoisonAsync(sent.Work);
                    continue;
                }

                builtAfterPoison |= poisoned && sent.Work.Kind == DriverWorkKind.BuildSet;
                await _h.Sync.HandleAsync(sent.Work);
            }
        }

        builtAfterPoison.Should().BeTrue("the pending set waiting on the poisoned file is built again");
        (await _h.Bed.Pointers.GetAsync(model.ModelId, WindowsRelease.Windows11Version24H2))!.Value.PendingVersion.Should().BeNull();
    }

    [Fact]
    public async Task TheStoredSetDocument_HasTheDocumentedShape()
    {
        var item = new DriverSetItem { LineageKey = "sp1", ComponentId = "sp2", Name = "n", FileKey = "k", BlobName = "files/Hp/sp2/1/sp2.exe", Sha256 = "ab", FileName = "sp2.exe", Size = 5, Extract = DriverExtractKind.Hp };
        var document = new DriverSetDocument { ModelId = Guid.NewGuid(), Fingerprint = "f", Items = [item] };

        var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(document, DriverJson.Options)).RootElement;
        var back = System.Text.Json.JsonSerializer.Deserialize<DriverSetDocument>(json.GetRawText(), DriverJson.Options)!;

        json.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        var stored = json.GetProperty("items")[0];
        stored.GetProperty("file").GetString().Should().Be("files/Hp/sp2/1/sp2.exe");
        stored.GetProperty("sizeBytes").GetInt64().Should().Be(5);
        stored.GetProperty("install").GetProperty("extract").GetString().Should().Be("Hp");
        stored.TryGetProperty("blobName", out _).Should().BeFalse();
        back.Items[0].Should().BeEquivalentTo(item);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StartRun_ALockPastItsWatchdog_IsReleased_AndANewRunStarts()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        (await _h.Bed.Runs.TryAcquireAsync(Guid.NewGuid(), _h.Bed.Time.GetUtcNow())).Should().BeTrue();
        (await _h.Sync.StartRunAsync(DriverSyncTrigger.Scheduled, null)).Status.Should().Be(DriverRunStartStatus.Running);

        _h.Bed.Time.Advance(DriverSyncService.Watchdog + TimeSpan.FromMinutes(16));

        (await _h.Sync.StartRunAsync(DriverSyncTrigger.Scheduled, null)).Status.Should().Be(DriverRunStartStatus.Started);
    }

    [Fact]
    public async Task SetDocument_WrittenByAnotherBuildFirst_IsNotOverwritten()
    {
        var modelId = Guid.NewGuid();
        var release = WindowsRelease.Windows11Version24H2;
        var seen = await _h.Bed.SetDocuments.GetETagAsync(modelId, release, 1);
        DriverSetDocument Doc(string fingerprint) => new() { ModelId = modelId, Release = release, Version = 1, Fingerprint = fingerprint };

        (await _h.Bed.SetDocuments.SaveAsync(Doc("winner"), seen)).Should().BeTrue();
        (await _h.Bed.SetDocuments.SaveAsync(Doc("loser"), seen)).Should().BeFalse();

        (await _h.Bed.SetDocuments.GetAsync(modelId, release, 1))!.Fingerprint.Should().Be("winner");
    }

    [Fact]
    public async Task Watchdog_EndsARunStillRunningAsFailed()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        var watchdog = _h.Bed.Queues.Drain().Single(m => m.Delay == DriverSyncService.Watchdog).Work;
        _h.Bed.Time.Advance(DriverSyncService.Watchdog);

        await _h.Sync.HandleAsync(watchdog);

        var run = (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value;
        run.Result.Should().Be(DriverSyncResult.Failed);
        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().ContainSingle(i => i.Reason == "The sync did not finish within 2 hours.");
        (await _h.Bed.Runs.GetActiveAsync()).Should().BeNull();
    }

    [Fact]
    public async Task DraftModel_ComponentsRefreshed_ButNothingDownloadedOrBuilt()
    {
        await _h.EnableAsync();
        var draft = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11", DriverModelState.Draft);

        var run = await _h.RunAsync(DriverSyncTrigger.Check);

        (await _h.ComponentsAsync(draft.ModelId)).Keys.Should().BeEquivalentTo("sp100", "sp101", "sp102");
        _h.Handled.Should().NotContain(m => m.Kind == DriverWorkKind.DownloadFile || m.Kind == DriverWorkKind.BuildSet);
        (await _h.Bed.Pointers.GetAsync(draft.ModelId, DriverSyncHarness.Release)).Should().BeNull();
        run.Result.Should().Be(DriverSyncResult.Completed);
    }

    [Fact]
    public async Task CleanUp_DeletesFilesNoCurrentPreviousOrPinnedSetReferences()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Bed.Store.Files.UpsertAsync("Hp", "sp999_9.0", new DriverFileRecord { BlobName = "files/Hp/sp999/9.0/sp999.exe", Sha256 = "00", Status = DriverFileStatus.Verified });
        await _h.Bed.Blobs.GetBlobClient("files/Hp/sp999/9.0/sp999.exe").UploadAsync(BinaryData.FromString("orphan"), new Azure.Storage.Blobs.Models.BlobUploadOptions());

        // Three newer versions of one component: set 1 falls out of current and previous.
        foreach (var version in new[] { "1.1", "1.2", "1.3" })
        {
            _h.PublishComponents(
                "8C26",
                DriverSyncHarness.Component("sp100", $"sp2{version.Replace(".", string.Empty, StringComparison.Ordinal)}", version, "Intel Wireless LAN Driver"),
                DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
            _h.Bed.Time.Advance(TimeSpan.FromHours(2));
            await _h.RunAsync();
        }

        (await _h.Bed.Store.Files.GetAsync("Hp", "sp999_9.0")).Should().BeNull();
        _h.Bed.Blobs.Blobs.Keys.Should().NotContain("files/Hp/sp999/9.0/sp999.exe");
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0")).Should().BeNull("set 1 is neither current nor previous");
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp212_1.2")).Should().NotBeNull("set 3 is the previous set");
        (await _h.Bed.Store.Files.GetAsync("Hp", "sp213_1.3")).Should().NotBeNull();
        (await _h.Bed.SetDocuments.ListAsync()).Select(d => d.Version).Should().BeEquivalentTo([3, 4]);
    }

    [Fact]
    public async Task CleanUp_KeepsTheSetPinnedToAnActiveSession()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Sessions.CreateAsync(new DeviceSession
        {
            SessionId = Guid.NewGuid(),
            DeviceSerialNumber = "5CG1234",
            DeviceManufacturer = "HP",
            DeviceModel = "HP EliteBook 840 G11",
            State = SessionState.SessionStarted,
            DriverModelId = model.ModelId,
            DriverRelease = DriverSyncHarness.Release,
            DriverSetVersion = 1,
        });

        foreach (var version in new[] { "1.1", "1.2" })
        {
            _h.PublishComponents(
                "8C26",
                DriverSyncHarness.Component("sp100", $"sp2{version.Replace(".", string.Empty, StringComparison.Ordinal)}", version, "Intel Wireless LAN Driver"),
                DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
            _h.Bed.Time.Advance(TimeSpan.FromHours(2));
            await _h.RunAsync();
        }

        (await _h.Bed.Store.Files.GetAsync("Hp", "sp200_1.0")).Should().NotBeNull("a running deployment still uses set 1");
        (await _h.Bed.SetDocuments.ListAsync()).Select(d => d.Version).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public async Task StartRun_RefusesWhileRunning_AndWithinTheMinute()
    {
        await _h.EnableAsync();
        var first = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");

        var whileRunning = await _h.Sync.StartRunAsync(DriverSyncTrigger.Check, "operator@contoso.com");
        await _h.PumpAsync();
        _h.Bed.Time.Advance(TimeSpan.FromSeconds(30));
        var tooSoon = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");

        first.Status.Should().Be(DriverRunStartStatus.Started);
        whileRunning.Status.Should().Be(DriverRunStartStatus.Running);
        tooSoon.Status.Should().Be(DriverRunStartStatus.TooSoon);
        tooSoon.AvailableAt.Should().Be(DriverTestBed.Start + DriverSyncService.RunLimit);
    }
}

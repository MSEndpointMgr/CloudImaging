using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverSyncProgressTests
{
    private readonly DriverSyncHarness _h = new();

    public DriverSyncProgressTests()
    {
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
    }

    [Fact]
    public async Task FinishedRun_RecordsWhatEachStageDid()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var run = await _h.RunAsync();

        run.CatalogsTotal.Should().Be(1);
        run.ModelsTotal.Should().Be(1);
        run.ModelsChecked.Should().Be(1);
        run.Downloads.Should().Be(2);
        run.DownloadedComponents.Should().Be(2);
        run.DownloadedBytes.Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length + DriverSyncHarness.Content("sp201", "2.0").Length);
        run.SetChecks.Should().BeGreaterThan(0);
        run.NewSets.Should().Be(1);
    }

    [Fact]
    public async Task RunningRun_CountsEachStage_AsItsStepsFinish()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        var runId = start.RunId!.Value;

        var started = (await _h.Sync.GetProgressAsync(runId))!;
        started.Catalogs.Should().Be(new DriverSyncStage { Done = 0, Total = 1 });
        started.Models.Should().Be(new DriverSyncStage { Done = 0, Total = 0 });

        // The catalog step runs; the model it found is now counted but not yet checked.
        await HandleNextAsync(DriverWorkKind.FetchVendorCatalog);
        var afterCatalog = (await _h.Sync.GetProgressAsync(runId))!;
        afterCatalog.Catalogs.Should().Be(new DriverSyncStage { Done = 1, Total = 1 });
        afterCatalog.Models.Should().Be(new DriverSyncStage { Done = 0, Total = 1 });

        await HandleNextAsync(DriverWorkKind.ResolveModel);
        var afterModel = (await _h.Sync.GetProgressAsync(runId))!;
        afterModel.Models.Should().Be(new DriverSyncStage { Done = 1, Total = 1 });
        afterModel.DriverSets.Should().Be(new DriverSyncStage { Done = 0, Total = 1 });

        await HandleNextAsync(DriverWorkKind.BuildSet);
        var afterBuild = (await _h.Sync.GetProgressAsync(runId))!;
        afterBuild.Components.Should().Be(new DriverSyncStage { Done = 0, Total = 2 });
        afterBuild.Run.Result.Should().Be(DriverSyncResult.Running);

        await _h.PumpAsync();
        var finished = (await _h.Sync.GetProgressAsync(runId))!;
        finished.Run.Result.Should().Be(DriverSyncResult.Completed);
        finished.Components.Should().Be(new DriverSyncStage { Done = 2, Total = 2 });
        finished.Run.DownloadedComponents.Should().Be(2);
    }

    [Fact]
    public async Task ModelWithTwoReleases_CountsAsOneModel_CheckedOnceBothAreDone()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var row = (await _h.Bed.Models.GetAsync(model.ModelId))!;
        await _h.Bed.Models.TryUpdateAsync(row.Value with { Releases = [DriverSyncHarness.Release, WindowsRelease.Windows11Version25H2] }, row.ETag);
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        await HandleNextAsync(DriverWorkKind.FetchVendorCatalog);

        await HandleNextAsync(DriverWorkKind.ResolveModel);
        (await _h.Sync.GetProgressAsync(start.RunId!.Value))!.Models.Should().Be(new DriverSyncStage { Done = 0, Total = 1 }, "one release is still open");

        await HandleNextAsync(DriverWorkKind.ResolveModel);
        (await _h.Sync.GetProgressAsync(start.RunId!.Value))!.Models.Should().Be(new DriverSyncStage { Done = 1, Total = 1 });
    }

    [Fact]
    public async Task Progress_ShowsWhatWasFoundSoFar_NewestFirst()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp500", "sp500", "1.0", "Intel Bluetooth Driver", "Driver - Bluetooth"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        await HandleNextAsync(DriverWorkKind.FetchVendorCatalog);
        await HandleNextAsync(DriverWorkKind.ResolveModel);

        var progress = (await _h.Sync.GetProgressAsync(start.RunId!.Value))!;

        progress.Found.Should().BeEquivalentTo(new Dictionary<DriverSyncItemOutcome, int> { [DriverSyncItemOutcome.Updated] = 1, [DriverSyncItemOutcome.New] = 1 });
        progress.Latest.Should().HaveCount(2);
        progress.Latest.Should().OnlyContain(i => i.At != null && i.Vendor == DriverVendor.Hp && i.ModelName == "HP EliteBook 840 G11");
        progress.Latest.Should().Contain(i => i.ComponentName == "Intel Bluetooth Driver" && i.Category == "Driver - Bluetooth");
    }

    [Fact]
    public async Task Report_ListsTheRunsOfTheRange_WithTheirItems()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var first = await _h.RunAsync();
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));
        _h.Bed.Time.Advance(TimeSpan.FromDays(2));
        var second = await _h.RunAsync();

        var report = await _h.Sync.GetReportAsync(second.StartedAt.AddMinutes(-1), second.StartedAt.AddMinutes(1));
        var all = await _h.Sync.GetReportAsync(first.StartedAt, second.StartedAt);

        report.Should().ContainSingle().Which.Run.RunId.Should().Be(second.RunId);
        report[0].Items.Should().ContainSingle(i => i.Outcome == DriverSyncItemOutcome.Updated && i.OldVersion == "1.0" && i.NewVersion == "1.1" && i.Category == "Driver - Network");
        report[0].Run.DownloadedComponents.Should().Be(1);
        all.Select(r => r.Run.RunId).Should().Equal(second.RunId, first.RunId);
    }

    [Fact]
    public async Task AFailedDownload_IsReportedByComponentNameVersionAndCategory_AndIsNotCountedAsDownloaded()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        _h.Bed.Vendor.ServeStatus("https://ftp.hp.com/pub/softpaq/sp201.exe", System.Net.HttpStatusCode.NotFound);

        var run = await _h.RunAsync();

        var failure = (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Single(i => i.Outcome == DriverSyncItemOutcome.Failed);
        failure.Should().Match<DriverSyncRunItem>(i => i.ComponentName == "Realtek Audio Driver" && i.NewVersion == "2.0" && i.Category == "Driver - Audio" && i.Vendor == DriverVendor.Hp && i.ModelId == null);
        run.Downloads.Should().Be(2);
        run.DownloadedComponents.Should().Be(1);
        run.DownloadedBytes.Should().Be(DriverSyncHarness.Content("sp200", "1.0").Length);
    }

    [Fact]
    public async Task APoisonedCatalog_ShowsAsAFailedVendor_SoTheCatalogStageFinishes()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        var fetch = _h.Bed.Queues.Drain().Single(m => m.Delay is null && m.Work.Kind == DriverWorkKind.FetchVendorCatalog).Work;

        await _h.Sync.HandlePoisonAsync(fetch);
        await _h.PumpAsync();

        var progress = (await _h.Sync.GetProgressAsync(start.RunId!.Value))!;
        progress.Catalogs.Should().Be(new DriverSyncStage { Done = 1, Total = 1 });
        progress.Run.Vendors.Should().ContainSingle(v => v.Vendor == DriverVendor.Hp && !v.Processed && v.FailureReason != null);
        progress.Run.Result.Should().Be(DriverSyncResult.Failed);
        (await _h.Bed.Runs.ListItemsAsync(start.RunId!.Value)).Should().BeEmpty("the vendor carries the failure, not a nameless item");
    }

    [Fact]
    public async Task APoisonedDownload_IsReportedByComponentName_NotItsFileKey()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
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

        var failure = (await _h.Bed.Runs.ListItemsAsync(start.RunId!.Value)).Single(i => i.Outcome == DriverSyncItemOutcome.Failed);
        failure.ComponentName.Should().BeOneOf("Intel Wireless LAN Driver", "Realtek Audio Driver");
        failure.NewVersion.Should().BeOneOf("1.0", "2.0");
        (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value.Downloads.Should().Be(2);
    }

    [Fact]
    public async Task ARun_RecordsThePlannedVendorsBeforeReadingAnyCatalog()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");

        var run = (await _h.Bed.Runs.GetAsync(start.RunId!.Value))!.Value;
        run.PlannedVendors.Should().Equal(DriverVendor.Hp);
        run.Vendors.Should().BeEmpty();
        DriverSyncService.ToContract(run).PlannedVendors.Should().Equal(DriverVendor.Hp);
    }

    [Fact]
    public async Task RunningTotals_KeepTheThreeNewestItems_AndGiveWayToTheRecountWhenTheRunEnds()
    {
        await _h.EnableAsync();
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp500", "sp500", "1.0", "Intel Bluetooth Driver", "Driver - Bluetooth"),
            DriverSyncHarness.Component("sp501", "sp501", "1.0", "Synaptics TouchPad Driver", "Driver - Input"),
            DriverSyncHarness.Component("sp502", "sp502", "1.0", "Intel Chipset Driver", "Driver - Chipset"),
            DriverSyncHarness.Component("sp503", "sp503", "1.0", "Intel Graphics Driver", "Driver - Video"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        var start = await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        await HandleNextAsync(DriverWorkKind.FetchVendorCatalog);
        await HandleNextAsync(DriverWorkKind.ResolveModel);

        var running = (await _h.Sync.GetProgressAsync(start.RunId!.Value))!;
        running.Found.Should().BeEquivalentTo(new Dictionary<DriverSyncItemOutcome, int> { [DriverSyncItemOutcome.New] = 4 });
        running.Latest.Should().HaveCount(3);
        running.Latest.Select(i => i.ComponentName).Should().Equal("Intel Graphics Driver", "Intel Chipset Driver", "Synaptics TouchPad Driver");

        await _h.PumpAsync();
        var ended = (await _h.Sync.GetProgressAsync(start.RunId!.Value))!;
        ended.Found.Should().BeEquivalentTo(ended.Run.Counts);
    }

    private async Task HandleNextAsync(DriverWorkKind kind)
    {
        var batch = _h.Bed.Queues.Drain();
        var next = batch.First(m => m.Delay is null && m.Work.Kind == kind);
        _h.Bed.Queues.Sent.AddRange(batch.Where(m => !ReferenceEquals(m, next)));

        await _h.Sync.HandleAsync(next.Work);
    }
}

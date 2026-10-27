using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Per-item outcomes and the optional Prepare setup step through the real progress function.</summary>
public sealed class ReportProgressItemsTests
{
    private readonly SetupProfileTestBed _bed = new();

    private ReportProgressFunction Function() => new(
        _bed.Sessions, _bed.Steps, _bed.Items, new SessionHistoryRepository(_bed.Tables), _bed.Config, NullLogger<ReportProgressFunction>.Instance);

    private async Task<HttpStatusCode> ReportAsync(Guid sessionId, object body)
    {
        var response = await Function().Run(FunctionTestHost.Request("POST", "https://core/x", body), sessionId.ToString(), FunctionTestHost.Context);
        return response.StatusCode;
    }

    private static object Item(StepItemKind kind, StepItemOutcome outcome, int index = 0, string name = "Item", string? reason = null) =>
        new { index, kind = kind.ToString(), name, outcome = outcome.ToString(), reason };

    [Fact]
    public async Task FinalReport_StoresItems_CountsAndWarningCount()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);

        await ReportAsync(session.SessionId, new
        {
            stepName = "PrepareSetup",
            status = "Completed",
            warningCount = 1,
            items = new[]
            {
                Item(StepItemKind.RegistryValue, StepItemOutcome.Succeeded, 0),
                Item(StepItemKind.RegistryValue, StepItemOutcome.Succeeded, 1),
                Item(StepItemKind.WinPEScript, StepItemOutcome.Failed, 2, reason: "Exited with code 1."),
            },
        });

        var step = (await _bed.Steps.GetBySessionAsync(session.SessionId)).Single();
        step.WarningCount.Should().Be(1);
        step.ItemCounts.Should().BeEquivalentTo(new[]
        {
            new ImagingStepItemCount { Kind = StepItemKind.RegistryValue, Outcome = StepItemOutcome.Succeeded, Count = 2 },
            new ImagingStepItemCount { Kind = StepItemKind.WinPEScript, Outcome = StepItemOutcome.Failed, Count = 1 },
        });
        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup)).Should().HaveCount(3);
    }

    [Fact]
    public async Task InstallDriversFinalReport_StoresDriverItemsSkippedOutcomesAndTheStatusNote()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);

        await ReportAsync(session.SessionId, new
        {
            stepName = "InstallDrivers",
            status = "Completed",
            warningCount = 0,
            statusNote = "Installed the drivers of HP EliteBook 840 G11, driver set 7.",
            items = new object[]
            {
                new { index = 0, kind = "DriverComponent", name = "Intel Wireless LAN Driver", target = "sp150123", outcome = "Succeeded", driverAction = "Offline", componentVersion = "23.80.0.4", driverOrigin = "Vendor", fromUsbCache = true },
                new { index = 1, kind = "DriverComponent", name = "Realtek Audio Driver", target = "sp151002", outcome = "Queued", driverAction = "OfflineAndSpecialize", componentVersion = "6.0.9733.1", driverOrigin = "RolledBack" },
                new { index = 2, kind = "DriverComponent", name = "Intel WWAN Driver", target = "CP0VC", outcome = "Skipped", reason = "No matching hardware.", driverAction = "Offline", componentVersion = "A04", driverOrigin = "Vendor" },
            },
        });

        var step = (await _bed.Steps.GetBySessionAsync(session.SessionId)).Single();
        step.StepName.Should().Be(ImagingStepName.InstallDrivers);
        step.StatusNote.Should().Be("Installed the drivers of HP EliteBook 840 G11, driver set 7.");
        step.ItemCounts.Should().Contain(new ImagingStepItemCount { Kind = StepItemKind.DriverComponent, Outcome = StepItemOutcome.Skipped, Count = 1 });
        var items = await _bed.Items.ListAsync(session.SessionId, ImagingStepName.InstallDrivers);
        items.Should().HaveCount(3);
        items[0].DriverAction.Should().Be(DriverAction.Offline);
        items[0].ComponentVersion.Should().Be("23.80.0.4");
        items[0].FromUsbCache.Should().BeTrue();
        items[1].DriverOrigin.Should().Be(DriverComponentOrigin.RolledBack);
        items[2].Outcome.Should().Be(StepItemOutcome.Skipped);
    }

    [Fact]
    public async Task InstallDriversReport_LongStatusNote_IsCutTo300Characters()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);

        await ReportAsync(session.SessionId, new { stepName = "InstallDrivers", status = "Completed", statusNote = new string('n', 400), items = Array.Empty<object>() });

        (await _bed.Steps.GetBySessionAsync(session.SessionId)).Single().StatusNote.Should().HaveLength(300);
    }

    [Fact]
    public async Task InstallDriversReport_1600Items_KeepsTheFirst1500()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        var items = Enumerable.Range(0, 1600).Select(i => new { index = i, kind = "DriverComponent", name = $"C{i}", outcome = "Succeeded", driverAction = "Offline" }).ToArray();

        await ReportAsync(session.SessionId, new { stepName = "InstallDrivers", status = "Completed", items });

        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.InstallDrivers)).Should().HaveCount(1500);
        (await _bed.Steps.GetBySessionAsync(session.SessionId)).Single().ItemsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task InProgressReport_IgnoresItems()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);

        await ReportAsync(session.SessionId, new { stepName = "PrepareSetup", status = "InProgress", items = new[] { Item(StepItemKind.RegistryValue, StepItemOutcome.Succeeded) } });

        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup)).Should().BeEmpty();
    }

    [Fact]
    public async Task FinalReport_TruncatesTextAndKeepsTheFirst1500Items()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        var items = Enumerable.Range(0, 1501).Select(i => Item(StepItemKind.RegistryValue, StepItemOutcome.Failed, i, new string('n', 150), new string('r', 600))).ToArray();

        (await ReportAsync(session.SessionId, new { stepName = "PrepareSetup", status = "Completed", warningCount = 1501, items })).Should().Be(HttpStatusCode.NoContent);

        var step = (await _bed.Steps.GetBySessionAsync(session.SessionId)).Single();
        step.ItemsTruncated.Should().BeTrue();
        var stored = await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup);
        stored.Should().HaveCount(1500);
        stored[0].Name.Should().HaveLength(100);
        stored[0].Reason.Should().HaveLength(500);
    }

    [Fact]
    public async Task SecondFinalReport_ReplacesTheItems()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        await ReportAsync(session.SessionId, new { stepName = "PrepareSetup", status = "Completed", items = new[] { Item(StepItemKind.RegistryValue, StepItemOutcome.Succeeded, 0), Item(StepItemKind.RegistryValue, StepItemOutcome.Succeeded, 1) } });

        await ReportAsync(session.SessionId, new { stepName = "PrepareSetup", status = "Completed", items = new[] { Item(StepItemKind.AnswerFile, StepItemOutcome.Succeeded, 0) } });

        (await _bed.Items.ListAsync(session.SessionId, ImagingStepName.PrepareSetup)).Should().ContainSingle().Which.Kind.Should().Be(StepItemKind.AnswerFile);
    }

    [Fact]
    public async Task PrepareSetupFailed_FailsTheSession_AndCompletedWithEveryRequiredStepCompletesIt()
    {
        var failing = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        await ReportAsync(failing.SessionId, new { stepName = "PrepareSetup", status = "Failed", errorDetail = "Enable Wake on LAN: Exited with code 1." });
        (await _bed.Sessions.GetByIdAsync(failing.SessionId))!.State.Should().Be(SessionState.SessionFailed);

        var completing = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        foreach (var step in new[] { "FormatDisk", "DownloadImage", "ApplyImage", "PrepareSetup", "ConfigureBoot", "ApplyRecoveryImage" })
        {
            await ReportAsync(completing.SessionId, new { stepName = step, status = "Completed" });
        }
        (await _bed.Sessions.GetByIdAsync(completing.SessionId))!.State.Should().Be(SessionState.SessionCompleted);
    }
}

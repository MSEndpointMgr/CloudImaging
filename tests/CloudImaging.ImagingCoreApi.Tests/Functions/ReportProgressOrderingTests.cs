using System.Net;
using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>
/// The device does not wait for its percent reports, so one can reach Core after the step's end
/// report. Such a report must never put the ended step back to running: the session would then
/// never complete, and the inactivity sweep would fail a device that imaged successfully.
/// </summary>
public sealed class ReportProgressOrderingTests
{
    private readonly SetupProfileTestBed _bed = new();

    private ReportProgressFunction Function(ImagingStepRepository? steps = null) => new(
        _bed.Sessions, steps ?? _bed.Steps, _bed.Items, new SessionHistoryRepository(_bed.Tables), _bed.Config, NullLogger<ReportProgressFunction>.Instance);

    private async Task<HttpStatusCode> ReportAsync(Guid sessionId, object body, ImagingStepRepository? steps = null)
    {
        var response = await Function(steps).Run(FunctionTestHost.Request("POST", "https://core/x", body), sessionId.ToString(), FunctionTestHost.Context);
        return response.StatusCode;
    }

    private async Task<ImagingStep> StepAsync(Guid sessionId, ImagingStepName name) =>
        (await _bed.Steps.GetBySessionAsync(sessionId)).Single(s => s.StepName == name);

    [Fact]
    public async Task PercentReport_ArrivingAfterItsStepEnded_IsIgnored_AndTheSessionStillCompletes()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionStarted);
        var id = session.SessionId;
        await ReportAsync(id, new { stepName = "FormatDisk", status = "Completed" });
        await ReportAsync(id, new { stepName = "DownloadImage", status = "InProgress", stepProgressPercent = 90 });
        await ReportAsync(id, new { stepName = "DownloadImage", status = "Completed" });

        (await ReportAsync(id, new { stepName = "DownloadImage", status = "InProgress", stepProgressPercent = 99 })).Should().Be(HttpStatusCode.NoContent);

        var download = await StepAsync(id, ImagingStepName.DownloadImage);
        download.Status.Should().Be(ImagingStepStatus.Completed);
        download.CompletedAt.Should().NotBeNull();

        foreach (var step in new[] { "ApplyImage", "ConfigureBoot", "ApplyRecoveryImage" })
        {
            await ReportAsync(id, new { stepName = step, status = "Completed" });
        }

        var completed = await _bed.Sessions.GetByIdAsync(id);
        completed!.State.Should().Be(SessionState.SessionCompleted);
        completed.OverallProgressPercent.Should().Be(100);
    }

    [Theory]
    [InlineData("InProgress", 40)]
    [InlineData("InProgress", null)]
    [InlineData("Pending", null)]
    public async Task StartOrPercentReport_ForAnEndedStep_ChangesNothing(string status, int? percent)
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        await ReportAsync(session.SessionId, new { stepName = "ApplyImage", status = "InProgress" });
        await ReportAsync(session.SessionId, new { stepName = "ApplyImage", status = "Completed" });
        var before = await _bed.Sessions.GetByIdAsync(session.SessionId);

        (await ReportAsync(session.SessionId, new { stepName = "ApplyImage", status, stepProgressPercent = percent })).Should().Be(HttpStatusCode.NoContent);

        (await StepAsync(session.SessionId, ImagingStepName.ApplyImage)).Status.Should().Be(ImagingStepStatus.Completed);
        var after = await _bed.Sessions.GetByIdAsync(session.SessionId);
        after!.CurrentStep.Should().Be(before!.CurrentStep);
        after.OverallProgressPercent.Should().Be(before.OverallProgressPercent);
    }

    [Fact]
    public async Task EndReport_ForAnEndedStep_IsStillStored()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        await ReportAsync(session.SessionId, new { stepName = "ApplyImage", status = "Completed" });

        (await ReportAsync(session.SessionId, new { stepName = "ApplyImage", status = "Failed", errorDetail = "DISM failed." })).Should().Be(HttpStatusCode.NoContent);

        var applied = await StepAsync(session.SessionId, ImagingStepName.ApplyImage);
        applied.Status.Should().Be(ImagingStepStatus.Failed);
        applied.ErrorDetail.Should().Be("DISM failed.");
        (await _bed.Sessions.GetByIdAsync(session.SessionId))!.State.Should().Be(SessionState.SessionFailed);
    }

    [Fact]
    public async Task SessionHistory_RecordsWhenImagingStarted_NotWhenTheDeviceRegistered()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionStarted);
        var id = session.SessionId;
        await ReportAsync(id, new { stepName = "FormatDisk", status = "InProgress" });
        var started = (await StepAsync(id, ImagingStepName.FormatDisk)).StartedAt;

        foreach (var step in new[] { "FormatDisk", "DownloadImage", "ApplyImage", "ConfigureBoot", "ApplyRecoveryImage" })
        {
            await ReportAsync(id, new { stepName = step, status = "Completed" });
        }

        var history = await new SessionHistoryRepository(_bed.Tables).QueryAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).SingleAsync();
        history.FinalState.Should().Be(SessionState.SessionCompleted);
        started.Should().NotBeNull();
        history.ImagingStartedAt.Should().Be(started);
        history.CreatedAt.Should().Be(session.CreatedAt);
    }

    [Fact]
    public async Task PercentReport_ThatRacesItsStepsEndReport_IsJudgedAgainAgainstIt()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        var id = session.SessionId;
        await ReportAsync(id, new { stepName = "DownloadImage", status = "InProgress", stepProgressPercent = 90 });

        // The end report is stored after the percent report read the step and before it writes.
        var racing = new RacingTable(_bed.Table("ImagingSteps"));
        var tables = new Mock<TableServiceClient>();
        tables.Setup(t => t.GetTableClient(It.IsAny<string>())).Returns((string name) => name == "ImagingSteps" ? racing : (TableClient)_bed.Table(name));
        racing.BeforeNextWrite = () => ReportAsync(id, new { stepName = "DownloadImage", status = "Completed" });

        var status = await ReportAsync(id, new { stepName = "DownloadImage", status = "InProgress", stepProgressPercent = 99 }, new ImagingStepRepository(tables.Object));

        status.Should().Be(HttpStatusCode.NoContent);
        racing.BeforeNextWrite.Should().BeNull("the end report ran between the read and the write");
        (await StepAsync(id, ImagingStepName.DownloadImage)).Status.Should().Be(ImagingStepStatus.Completed);
    }

    [Fact]
    public async Task TryWriteAsync_RefusesAStepThatChangedSinceItWasRead()
    {
        var id = Guid.NewGuid();
        await _bed.Steps.UpsertAsync(id, new ImagingStep { StepName = ImagingStepName.ApplyImage, Status = ImagingStepStatus.InProgress });
        var read = (await _bed.Steps.GetStoredBySessionAsync(id)).Single();
        await _bed.Steps.UpsertAsync(id, new ImagingStep { StepName = ImagingStepName.ApplyImage, Status = ImagingStepStatus.Completed });
        var late = new ImagingStep { StepName = ImagingStepName.ApplyImage, Status = ImagingStepStatus.InProgress, StepProgressPercent = 99 };

        (await _bed.Steps.TryWriteAsync(id, late, read.ETag)).Should().BeFalse();
        (await _bed.Steps.TryWriteAsync(id, late, etag: null)).Should().BeFalse("the step exists, so it cannot be added again");
        (await StepAsync(id, ImagingStepName.ApplyImage)).Status.Should().Be(ImagingStepStatus.Completed);
    }

    /// <summary>The steps table, running a competing report just before the next write, as if it had arrived at the same moment.</summary>
    private sealed class RacingTable(InMemoryTableClient inner) : TableClient
    {
        public Func<Task>? BeforeNextWrite { get; set; }

        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default) =>
            inner.QueryAsync<T>(filter, maxPerPage, select, cancellationToken);

        public override async Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
        {
            await RaceAsync();
            return await inner.AddEntityAsync(entity, cancellationToken);
        }

        public override async Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            await RaceAsync();
            return await inner.UpdateEntityAsync(entity, ifMatch, mode, cancellationToken);
        }

        private async Task RaceAsync()
        {
            if (BeforeNextWrite is { } race)
            {
                BeforeNextWrite = null;
                await race();
            }
        }
    }
}

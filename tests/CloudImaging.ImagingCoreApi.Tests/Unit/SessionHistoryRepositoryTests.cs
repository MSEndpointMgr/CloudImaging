using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using FluentAssertions;
using Moq;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SessionHistoryRepository"/> (Reports feature).
/// Uses Moq stubs for TableServiceClient and TableClient, mirroring the pattern in
/// <see cref="BootMediaCertificateRepositoryTests"/>.
/// </summary>
public sealed class SessionHistoryRepositoryTests
{
    private static AsyncPageable<TableEntity> ToAsyncPageable(List<TableEntity> entities) =>
        AsyncPageable<TableEntity>.FromPages(new[]
        {
            Page<TableEntity>.FromValues(entities, null, Mock.Of<Response>()),
        });

    private static (SessionHistoryRepository Repo, Mock<TableClient> MockTable)
        BuildRepo(IEnumerable<TableEntity>? existingEntities = null)
    {
        var mockTableClient  = new Mock<TableClient>();
        var mockTableService = new Mock<TableServiceClient>();
        mockTableService
            .Setup(s => s.GetTableClient(It.IsAny<string>()))
            .Returns(mockTableClient.Object);

        var entities = existingEntities?.ToList() ?? new List<TableEntity>();

        mockTableClient
            .Setup(t => t.QueryAsync<TableEntity>(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncPageable(entities));

        mockTableClient
            .Setup(t => t.UpsertEntityAsync(
                It.IsAny<TableEntity>(),
                It.IsAny<TableUpdateMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        var repo = new SessionHistoryRepository(mockTableService.Object);
        return (repo, mockTableClient);
    }

    private static SessionHistoryRecord SampleRecord(
        SessionState finalState = SessionState.SessionCompleted,
        ImagingStepName? failedStepName = null,
        string? errorDetail = null) => new()
    {
        SessionId = Guid.NewGuid(),
        FinalState = finalState,
        DeviceSerialNumber = "SN-123",
        DeviceManufacturer = "Contoso",
        DeviceModel = "Widget 3000",
        PreFlightAuthorizationResult = PreFlightAuthorizationResult.MatchedAutopilotV1,
        AssignedOsImageId = Guid.NewGuid(),
        FailedStepName = failedStepName,
        ErrorDetail = errorDetail,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        TerminalAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task CreateAsync_UpsertsEntityWithFixedPartitionKeyAndComputedPurgeAt()
    {
        var (repo, mockTable) = BuildRepo();
        var record = SampleRecord();
        const int retentionDays = 90;

        TableEntity? captured = null;
        mockTable
            .Setup(t => t.UpsertEntityAsync(
                It.IsAny<TableEntity>(),
                TableUpdateMode.Replace,
                It.IsAny<CancellationToken>()))
            .Callback<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) => captured = e)
            .ReturnsAsync(Mock.Of<Response>());

        await repo.CreateAsync(record, retentionDays);

        captured.Should().NotBeNull();
        captured!.PartitionKey.Should().Be("history");
        captured.RowKey.Should().Be(record.SessionId.ToString());
        captured["FinalState"].Should().Be(record.FinalState.ToString());
        captured["DeviceSerialNumber"].Should().Be(record.DeviceSerialNumber);
        var purgeAt = (DateTimeOffset)captured["PurgeAt"];
        purgeAt.Should().BeCloseTo(record.TerminalAt.AddDays(retentionDays), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task QueryAsync_MapsEntitiesBackToRecords()
    {
        var record = SampleRecord(SessionState.SessionFailed, ImagingStepName.ApplyImage, "disk write failed");

        var entity = new TableEntity("history", record.SessionId.ToString())
        {
            ["FinalState"] = record.FinalState.ToString(),
            ["DeviceSerialNumber"] = record.DeviceSerialNumber,
            ["DeviceManufacturer"] = record.DeviceManufacturer,
            ["DeviceModel"] = record.DeviceModel,
            ["PreFlightAuthorizationResult"] = record.PreFlightAuthorizationResult.ToString(),
            ["AssignedOsImageId"] = record.AssignedOsImageId?.ToString(),
            ["FailedStepName"] = record.FailedStepName?.ToString(),
            ["ErrorDetail"] = record.ErrorDetail,
            ["CreatedAt"] = record.CreatedAt,
            ["TerminalAt"] = record.TerminalAt,
            ["PurgeAt"] = record.TerminalAt.AddDays(90),
        };

        var (repo, _) = BuildRepo(new[] { entity });

        var results = await repo.QueryAsync(
            record.TerminalAt.AddDays(-1), record.TerminalAt.AddDays(1)).ToListAsync();

        results.Should().ContainSingle();
        var mapped = results[0];
        mapped.SessionId.Should().Be(record.SessionId);
        mapped.FinalState.Should().Be(SessionState.SessionFailed);
        mapped.FailedStepName.Should().Be(ImagingStepName.ApplyImage);
        mapped.ErrorDetail.Should().Be("disk write failed");
    }

    [Fact]
    public async Task QueryDueForPurgeAsync_ReturnsSessionIdAndTerminalAtTuples()
    {
        var sessionId = Guid.NewGuid();
        var terminalAt = DateTimeOffset.UtcNow.AddDays(-100);
        var entity = new TableEntity("history", sessionId.ToString())
        {
            ["TerminalAt"] = terminalAt,
            ["PurgeAt"] = terminalAt.AddDays(90),
        };

        var (repo, _) = BuildRepo(new[] { entity });

        var due = await repo.QueryDueForPurgeAsync(DateTimeOffset.UtcNow).ToListAsync();

        due.Should().ContainSingle();
        due[0].SessionId.Should().Be(sessionId);
        due[0].TerminalAt.Should().BeCloseTo(terminalAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeletePurgedAsync_SwallowsNotFound()
    {
        var mockTableClient  = new Mock<TableClient>();
        var mockTableService = new Mock<TableServiceClient>();
        mockTableService.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);
        mockTableClient
            .Setup(t => t.DeleteEntityAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ETag>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        var repo = new SessionHistoryRepository(mockTableService.Object);

        var act = async () => await repo.DeletePurgedAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }
}

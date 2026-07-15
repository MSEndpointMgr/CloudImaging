using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Linq.Expressions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="BootMediaCertificateRepository"/> atomic-swap semantics (T161a).
/// Uses Moq stubs for TableServiceClient and TableClient.
/// </summary>
public sealed class BootMediaCertificateRepositoryTests
{
    private const string Thumbprint1 = "AABBCCDDEEFF0011223344556677889900112233";
    private const string Thumbprint2 = "1122334455667788990011223344556677889900";

    // ── Helper to build a fake active TableEntity ─────────────────────────────

    private static TableEntity ActiveEntity(string thumbprint) => new("cert", thumbprint)
    {
        ["NotBefore"]          = DateTimeOffset.UtcNow.AddDays(-1),
        ["NotAfter"]           = DateTimeOffset.UtcNow.AddDays(364),
        ["IsActive"]           = true,
        ["KeyVaultSecretName"] = $"boot-media-cert-{thumbprint[..8].ToLowerInvariant()}",
    };

    // ── Build repo with mock table client ─────────────────────────────────────

    private static (BootMediaCertificateRepository Repo, Mock<TableClient> MockTable,
        List<TableTransactionAction> CapturedActions)
        BuildRepo(IEnumerable<TableEntity>? existingActiveEntities = null)
    {
        var mockTableClient  = new Mock<TableClient>();
        var mockTableService = new Mock<TableServiceClient>();
        mockTableService
            .Setup(s => s.GetTableClient(It.IsAny<string>()))
            .Returns(mockTableClient.Object);

        var entities = existingActiveEntities?.ToList() ?? new List<TableEntity>();

        // Stub QueryAsync — both string filter (GetActiveAsync) and expression filter (ActivateAsync)
        mockTableClient
            .Setup(t => t.QueryAsync<TableEntity>(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncPageable(entities));

        // Expression-based overload used by ActivateAsync demote loop
        mockTableClient
            .Setup(t => t.QueryAsync<TableEntity>(
                It.IsAny<Expression<Func<TableEntity, bool>>>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncPageable(entities));

        // Capture the transaction actions on SubmitTransactionAsync
        var capturedActions = new List<TableTransactionAction>();
        mockTableClient
            .Setup(t => t.SubmitTransactionAsync(
                It.IsAny<IEnumerable<TableTransactionAction>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TableTransactionAction>, CancellationToken>(
                (actions, _) => capturedActions.AddRange(actions))
            .ReturnsAsync(Mock.Of<Response<IReadOnlyList<Response>>>());

        var repo = new BootMediaCertificateRepository(
            mockTableService.Object,
            NullLogger<BootMediaCertificateRepository>.Instance);

        return (repo, mockTableClient, capturedActions);
    }

    private static AsyncPageable<TableEntity> ToAsyncPageable(List<TableEntity> entities) =>
        AsyncPageable<TableEntity>.FromPages(new[]
        {
            Page<TableEntity>.FromValues(entities, null, Mock.Of<Response>()),
        });

    // ── Test: ActivateAsync submits a transaction ─────────────────────────────

    [Fact]
    public async Task ActivateAsync_SubmitsTransactionWithNewActiveEntry()
    {
        // Arrange — no existing active certs
        var (repo, _, capturedActions) = BuildRepo();

        var newCert = new BootMediaCertificate
        {
            Thumbprint         = Thumbprint1,
            NotBefore          = DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter           = DateTimeOffset.UtcNow.AddDays(364),
            IsActive           = false,
            KeyVaultSecretName = "boot-media-cert-aabbccdd",
        };

        // Act
        await repo.ActivateAsync(newCert);

        // Assert — transaction must contain an UpsertReplace action for the new cert with IsActive=true
        capturedActions.Should().NotBeEmpty("a transaction must be submitted");
        var activateAction = capturedActions.FirstOrDefault(a =>
            a.ActionType == TableTransactionActionType.UpsertReplace
            && ((TableEntity)a.Entity).RowKey == Thumbprint1);

        activateAction.Should().NotBeNull("new cert must be inserted as UpsertReplace");
        ((bool)((TableEntity)activateAction!.Entity)["IsActive"]).Should().BeTrue("new cert IsActive must be true");
    }

    [Fact]
    public async Task ActivateAsync_WithExistingActiveCert_DemotesOldAndActivatesNew()
    {
        // Arrange — one cert is currently active
        var existing = ActiveEntity(Thumbprint1);
        var (repo, _, capturedActions) = BuildRepo(new[] { existing });

        var newCert = new BootMediaCertificate
        {
            Thumbprint         = Thumbprint2,
            NotBefore          = DateTimeOffset.UtcNow,
            NotAfter           = DateTimeOffset.UtcNow.AddDays(365),
            IsActive           = false,
            KeyVaultSecretName = "boot-media-cert-11223344",
        };

        // Act
        await repo.ActivateAsync(newCert);

        // Assert — transaction includes: demote of Thumbprint1 AND activate of Thumbprint2
        var demoteAction = capturedActions.FirstOrDefault(a =>
            a.ActionType == TableTransactionActionType.UpdateMerge
            && ((TableEntity)a.Entity).RowKey == Thumbprint1);
        var activateAction = capturedActions.FirstOrDefault(a =>
            a.ActionType == TableTransactionActionType.UpsertReplace
            && ((TableEntity)a.Entity).RowKey == Thumbprint2);

        demoteAction.Should().NotBeNull("existing active cert must be demoted");
        ((bool)((TableEntity)demoteAction!.Entity)["IsActive"]).Should().BeFalse("demoted cert IsActive must be false");

        activateAction.Should().NotBeNull("new cert must be activated");
        ((bool)((TableEntity)activateAction!.Entity)["IsActive"]).Should().BeTrue("new cert IsActive must be true");
    }

    [Fact]
    public async Task ActivateAsync_EnsuresExactlyOneCertIsActive_AfterSwap()
    {
        // Arrange — two existing active certs (edge case)
        var existing1 = ActiveEntity(Thumbprint1);
        var existing2 = ActiveEntity(Thumbprint2);
        var (repo, _, capturedActions) = BuildRepo(new[] { existing1, existing2 });

        var newCert = new BootMediaCertificate
        {
            Thumbprint         = "CCDDAABBCCDDAABBCCDDAABBCCDDAABB00112233",
            NotBefore          = DateTimeOffset.UtcNow,
            NotAfter           = DateTimeOffset.UtcNow.AddDays(365),
            IsActive           = false,
            KeyVaultSecretName = "boot-media-cert-ccddaabb",
        };

        // Act
        await repo.ActivateAsync(newCert);

        // Assert — BOTH existing active certs are demoted in the same transaction
        int demoteCount = capturedActions.Count(a =>
            a.ActionType == TableTransactionActionType.UpdateMerge
            && (bool)((TableEntity)a.Entity)["IsActive"] == false);

        demoteCount.Should().Be(2, "both previously active certs must be demoted atomically");
    }
}

using System.Net;
using Azure.Core;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>
/// End-to-end lifecycle of an Autopilot registration request through
/// <see cref="AutopilotRegistrationService"/>, backed by in-memory tables and a mocked Graph client.
/// </summary>
public sealed class AutopilotRegistrationLifecycleTests
{
    private const string ValidHash = "VGhpcyBpcyBhIGZha2UgNEsgaGFyZHdhcmUgaGFzaA==";

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<AutopilotGraphClient> _graph = new(new HttpClient(), Mock.Of<TokenCredential>());
    private readonly PortalConfigurationRepository _config;
    private readonly AutopilotRegistrationRepository _requests;
    private readonly AutopilotGroupTagRepository _tags;
    private readonly LocationRepository _locations;
    private readonly AutopilotRegistrationService _service;

    public AutopilotRegistrationLifecycleTests()
    {
        var (tableService, _) = InMemoryTableClient.CreateService();
        _config = new PortalConfigurationRepository(tableService, NullLogger<PortalConfigurationRepository>.Instance);
        _requests = new AutopilotRegistrationRepository(tableService);
        _tags = new AutopilotGroupTagRepository(tableService);
        _locations = new LocationRepository(tableService);
        _service = new AutopilotRegistrationService(_requests, _tags, _locations, _config, _graph.Object, NullLogger<AutopilotRegistrationService>.Instance, _time);
        _graph.Setup(g => g.IsSerialRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    private Task EnableAsync(bool groupTagRequired = false, int expiryDays = 7, int retentionDays = 365) =>
        _config.UpsertAsync(new PortalConfiguration
        {
            AutopilotRegistrationEnabled = true,
            AutopilotGroupTagRequired = groupTagRequired,
            AutopilotPendingExpiryDays = expiryDays,
            AutopilotRetentionDays = retentionDays,
        });

    private static AutopilotHashSubmission Submission(string serial = "SN-001", string hash = ValidHash, Guid? locationId = null, string? locationName = null) => new()
    {
        SerialNumber = serial,
        Manufacturer = "Contoso",
        Model = "Laptop 7",
        HardwareHash = hash,
        LocationId = locationId,
        LocationName = locationName,
    };

    private async Task<AutopilotSubmissionResponse> SubmitAsync(AutopilotHashSubmission? submission = null)
    {
        var result = await _service.SubmitAsync(submission ?? Submission(), CancellationToken.None);
        result.Outcome.Should().Be(AutopilotOutcome.Ok, result.Error);
        return result.Value!;
    }

    private async Task<AutopilotRegistrationRequest> StoredAsync(Guid id) => (await _requests.GetAsync(id))!.Value.Request;

    private static AutopilotDecision Decision(Guid? tagId = null, string? reason = null) =>
        new() { DecidedByUpn = "approver@contoso.com", DecidedByObjectId = "oid-1", GroupTagDefinitionId = tagId, Reason = reason };

    private void ImportReturns(string importId) =>
        _graph.Setup(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(importId);

    [Fact]
    public async Task Submit_IsRefused_WhileTheFeatureIsDisabled()
    {
        var result = await _service.SubmitAsync(Submission(), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Disabled);
    }

    [Fact]
    public async Task Submit_StoresAPendingRequest_WithHashedStatusTokenAndConfiguredExpiry()
    {
        await EnableAsync(expiryDays: 5);

        var response = await SubmitAsync();

        response.State.Should().Be(AutopilotRegistrationState.PendingApproval);
        response.ReferenceCode.Should().MatchRegex("^AP-[A-Z2-9]{5}$");
        var stored = await StoredAsync(response.RequestId);
        stored.HardwareHash.Should().Be(ValidHash);
        stored.StatusTokenHash.Should().Be(AutopilotRegistrationService.HashStatusToken(response.StatusToken)).And.NotBe(response.StatusToken);
        stored.ExpiresAt.Should().Be(_time.GetUtcNow().AddDays(5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_RecordsWhetherTheHashSupportsPreProvisioning(bool withEkPub)
    {
        await EnableAsync();
        var hash = AutopilotGraphClientTests.SyntheticHash("2.0", withEkPub ? [7, 7, 7] : null);

        var response = await SubmitAsync(Submission(hash: hash));

        var stored = await StoredAsync(response.RequestId);
        stored.TpmVersion.Should().Be("2.0");
        stored.PreProvisioningReady.Should().Be(withEkPub);
    }

    [Fact]
    public async Task Submit_LeavesPreProvisioningUnknown_WhenTheHashCannotBeRead()
    {
        await EnableAsync();

        var response = await SubmitAsync();

        (await StoredAsync(response.RequestId)).PreProvisioningReady.Should().BeNull();
    }

    [Theory]
    [InlineData("UNKNOWN", ValidHash)]
    [InlineData("SN-1", "not base64!")]
    [InlineData("SN-1", "")]
    public async Task Submit_RejectsUnusableInput(string serial, string hash)
    {
        await EnableAsync();

        var result = await _service.SubmitAsync(Submission(serial, hash), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Invalid);
    }

    [Fact]
    public async Task Submit_RecordsAlreadyRegistered_WithoutKeepingTheHash()
    {
        await EnableAsync();
        _graph.Setup(g => g.IsSerialRegisteredAsync("SN-001", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var response = await SubmitAsync();

        response.State.Should().Be(AutopilotRegistrationState.AlreadyRegistered);
        (await StoredAsync(response.RequestId)).HardwareHash.Should().BeNull();
    }

    [Fact]
    public async Task Submit_StillQueues_WhenTheRegisteredLookupFails()
    {
        await EnableAsync();
        _graph.Setup(g => g.IsSerialRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AutopilotGraphException("Forbidden", HttpStatusCode.Forbidden));

        var response = await SubmitAsync();

        response.State.Should().Be(AutopilotRegistrationState.PendingApproval);
    }

    [Fact]
    public async Task Resubmission_RefreshesTheOpenRequest_AndInvalidatesTheOldStatusToken()
    {
        await EnableAsync();
        var first = await SubmitAsync();

        var second = await SubmitAsync(Submission(hash: "bmV3IGhhc2g="));

        second.RequestId.Should().Be(first.RequestId);
        (await StoredAsync(first.RequestId)).HardwareHash.Should().Be("bmV3IGhhc2g=");
        (await _service.GetDeviceStatusAsync(first.RequestId, first.StatusToken, CancellationToken.None)).Should().BeNull();
        (await _service.GetDeviceStatusAsync(first.RequestId, second.StatusToken, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeviceStatus_RequiresTheMatchingToken()
    {
        await EnableAsync();
        var response = await SubmitAsync();

        (await _service.GetDeviceStatusAsync(response.RequestId, "wrong", CancellationToken.None)).Should().BeNull();
        var status = await _service.GetDeviceStatusAsync(response.RequestId, response.StatusToken, CancellationToken.None);
        status!.State.Should().Be(AutopilotRegistrationState.PendingApproval);
        status.ReferenceCode.Should().Be(response.ReferenceCode);
    }

    [Fact]
    public async Task Approve_RequiresAGroupTag_WhenConfigured()
    {
        await EnableAsync(groupTagRequired: true);
        var response = await SubmitAsync();

        var result = await _service.ApproveAsync(response.RequestId, Decision(), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Invalid);
        _graph.Verify(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Approve_ResolvesATemplateFromTheDeviceLocation_AndStartsTheImport()
    {
        await EnableAsync();
        var location = await _locations.CreateAsync(new Location { LocationId = Guid.NewGuid(), Name = "Stockholm HQ", Region = "EMEA", CountryCode = "SE" });
        var tag = new AutopilotGroupTagDefinition { Id = Guid.NewGuid(), Name = "Regional", Kind = AutopilotGroupTagKind.Template, Value = "{Region}-{CountryCode}-STD" };
        await _tags.AddAsync(tag);
        var response = await SubmitAsync(Submission(locationId: location.LocationId, locationName: location.Name));
        _graph.Setup(g => g.ImportAsync("SN-001", ValidHash, "EMEA-SE-STD", It.IsAny<CancellationToken>())).ReturnsAsync("import-1");

        var result = await _service.ApproveAsync(response.RequestId, Decision(tag.Id), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Ok);
        var stored = await StoredAsync(response.RequestId);
        stored.State.Should().Be(AutopilotRegistrationState.Importing);
        stored.GroupTag.Should().Be("EMEA-SE-STD");
        stored.ImportedIdentityId.Should().Be("import-1");
        stored.DecidedByUpn.Should().Be("approver@contoso.com");
    }

    [Fact]
    public async Task Approve_RefusesATemplate_TheDeviceLocationCannotResolve()
    {
        await EnableAsync();
        var tag = new AutopilotGroupTagDefinition { Id = Guid.NewGuid(), Name = "Regional", Kind = AutopilotGroupTagKind.Template, Value = "{Region}-STD" };
        await _tags.AddAsync(tag);
        var response = await SubmitAsync();

        var result = await _service.ApproveAsync(response.RequestId, Decision(tag.Id), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Invalid);
        result.Error.Should().Contain("no location");
    }

    [Fact]
    public async Task Approve_Twice_ReturnsConflict()
    {
        await EnableAsync();
        var response = await SubmitAsync();
        ImportReturns("import-1");
        await _service.ApproveAsync(response.RequestId, Decision(), CancellationToken.None);

        var second = await _service.ApproveAsync(response.RequestId, Decision(), CancellationToken.None);

        second.Outcome.Should().Be(AutopilotOutcome.Conflict);
        _graph.Verify(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_RecordsTheDecision_AndAFailedImport_WhenGraphRefuses()
    {
        await EnableAsync();
        var response = await SubmitAsync();
        _graph.Setup(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AutopilotGraphException("Grant DeviceManagementServiceConfig.ReadWrite.All", HttpStatusCode.Forbidden));

        var result = await _service.ApproveAsync(response.RequestId, Decision(), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.GraphFailed);
        var stored = await StoredAsync(response.RequestId);
        stored.State.Should().Be(AutopilotRegistrationState.ImportFailed);
        stored.ImportErrorName.Should().Contain("ReadWrite.All");
        stored.DecidedByUpn.Should().Be("approver@contoso.com");
        stored.HardwareHash.Should().Be(ValidHash, "a failed import can be retried");
    }

    [Fact]
    public async Task Reject_PurgesTheHash_AndKeepsTheReason()
    {
        await EnableAsync();
        var response = await SubmitAsync();

        var result = await _service.RejectAsync(response.RequestId, Decision(reason: "Not a company device"), CancellationToken.None);

        result.Outcome.Should().Be(AutopilotOutcome.Ok);
        var stored = await StoredAsync(response.RequestId);
        stored.State.Should().Be(AutopilotRegistrationState.Rejected);
        stored.HardwareHash.Should().BeNull();
        stored.RejectionReason.Should().Be("Not a company device");
    }

    private async Task<Guid> ImportingRequestAsync()
    {
        await EnableAsync();
        var response = await SubmitAsync();
        ImportReturns("import-1");
        await _service.ApproveAsync(response.RequestId, Decision(), CancellationToken.None);
        return response.RequestId;
    }

    [Fact]
    public async Task Process_CompletesAnImport_PurgesTheHash_AndCleansUp()
    {
        var id = await ImportingRequestAsync();
        _graph.Setup(g => g.GetImportStatusAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(new AutopilotImportStatus("complete", null, null));

        (await _service.ProcessAsync(CancellationToken.None)).Should().Be(1);

        var stored = await StoredAsync(id);
        stored.State.Should().Be(AutopilotRegistrationState.Imported);
        stored.HardwareHash.Should().BeNull();
        _graph.Verify(g => g.DeleteImportAsync("import-1", It.IsAny<CancellationToken>()), Times.Once);
        _graph.Verify(g => g.SyncAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_TreatsAlreadyAssignedAsAlreadyRegistered()
    {
        var id = await ImportingRequestAsync();
        _graph.Setup(g => g.GetImportStatusAsync("import-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AutopilotImportStatus("error", "806", "ZtdDeviceAlreadyAssigned"));

        await _service.ProcessAsync(CancellationToken.None);

        (await StoredAsync(id)).State.Should().Be(AutopilotRegistrationState.AlreadyRegistered);
    }

    [Fact]
    public async Task Process_RecordsAnIntuneError_AsARetryableFailure()
    {
        var id = await ImportingRequestAsync();
        _graph.Setup(g => g.GetImportStatusAsync("import-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AutopilotImportStatus("error", "808", "ZtdDeviceAssignedToOtherTenant"));

        await _service.ProcessAsync(CancellationToken.None);

        var stored = await StoredAsync(id);
        stored.State.Should().Be(AutopilotRegistrationState.ImportFailed);
        stored.ImportErrorName.Should().Be("ZtdDeviceAssignedToOtherTenant");
        stored.HardwareHash.Should().NotBeNull();

        ImportReturns("import-2");
        var retry = await _service.RetryAsync(id, Decision(), CancellationToken.None);
        retry.Outcome.Should().Be(AutopilotOutcome.Ok);
        (await StoredAsync(id)).ImportedIdentityId.Should().Be("import-2");
    }

    [Fact]
    public async Task Process_FailsAnImport_ThatNeverFinishes()
    {
        var id = await ImportingRequestAsync();
        _graph.Setup(g => g.GetImportStatusAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(new AutopilotImportStatus("pending", null, null));

        await _service.ProcessAsync(CancellationToken.None);
        (await StoredAsync(id)).State.Should().Be(AutopilotRegistrationState.Importing);

        _time.Advance(AutopilotRegistrationService.ImportTimeout + TimeSpan.FromMinutes(1));
        await _service.ProcessAsync(CancellationToken.None);
        (await StoredAsync(id)).State.Should().Be(AutopilotRegistrationState.ImportFailed);
    }

    [Fact]
    public async Task Process_ExpiresUndecidedRequests_AndPurgesTheHash()
    {
        await EnableAsync(expiryDays: 7);
        var response = await SubmitAsync();

        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await _service.ProcessAsync(CancellationToken.None);

        var stored = await StoredAsync(response.RequestId);
        stored.State.Should().Be(AutopilotRegistrationState.Expired);
        stored.HardwareHash.Should().BeNull();
    }

    [Fact]
    public async Task List_KeepsHandledRequestsOutOfTheQueue()
    {
        await EnableAsync();
        var pending = await SubmitAsync();
        var rejected = await SubmitAsync(Submission("SN-002"));
        await _service.RejectAsync(rejected.RequestId, Decision(), CancellationToken.None);

        (await _service.ListOpenAsync(CancellationToken.None)).Select(r => r.RequestId).Should().Equal(pending.RequestId);
        (await _service.ListHandledAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None)).Select(r => r.RequestId).Should().Equal(rejected.RequestId);
    }

    [Fact]
    public async Task ListHandled_FiltersOnWhenTheRequestWasClosed()
    {
        await EnableAsync();
        var early = await SubmitAsync();
        await _service.RejectAsync(early.RequestId, Decision(), CancellationToken.None);
        var closedEarly = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromDays(10));
        var late = await SubmitAsync(Submission("SN-002"));
        await _service.RejectAsync(late.RequestId, Decision(), CancellationToken.None);

        (await _service.ListHandledAsync(closedEarly.AddDays(1), _time.GetUtcNow(), CancellationToken.None)).Select(r => r.RequestId).Should().Equal(late.RequestId);
        (await _service.ListHandledAsync(closedEarly, _time.GetUtcNow(), CancellationToken.None)).Select(r => r.RequestId).Should().Equal(late.RequestId, early.RequestId);
    }

    [Fact]
    public async Task Process_DeletesHandledRequests_OnlyAfterTheRetentionPeriod()
    {
        await EnableAsync(retentionDays: 30);
        var rejected = await SubmitAsync();
        await _service.RejectAsync(rejected.RequestId, Decision(), CancellationToken.None);
        var stillPending = await SubmitAsync(Submission("SN-002"));

        _time.Advance(TimeSpan.FromDays(29));
        await _service.ProcessAsync(CancellationToken.None);
        (await _requests.GetAsync(rejected.RequestId)).Should().NotBeNull();

        _time.Advance(TimeSpan.FromDays(2));
        await _service.ProcessAsync(CancellationToken.None);
        (await _requests.GetAsync(rejected.RequestId)).Should().BeNull();
        (await _requests.GetAsync(stillPending.RequestId)).Should().NotBeNull("open requests expire first and are never deleted while open");
    }

    [Fact]
    public async Task Detail_OffersEveryDefinition_MarkingUnresolvableOnesUnavailable()
    {
        await EnableAsync(groupTagRequired: true);
        await _tags.AddAsync(new AutopilotGroupTagDefinition { Id = Guid.NewGuid(), Name = "Kiosk", Kind = AutopilotGroupTagKind.Static, Value = "Kiosk" });
        await _tags.AddAsync(new AutopilotGroupTagDefinition { Id = Guid.NewGuid(), Name = "Regional", Kind = AutopilotGroupTagKind.Template, Value = "{CountryCode}-STD" });
        var response = await SubmitAsync();

        var detail = await _service.GetDetailAsync(response.RequestId, CancellationToken.None);

        detail!.GroupTagRequired.Should().BeTrue();
        detail.GroupTagOptions.Should().HaveCount(2);
        detail.GroupTagOptions.Single(o => o.Name == "Kiosk").ResolvedValue.Should().Be("Kiosk");
        detail.GroupTagOptions.Single(o => o.Name == "Regional").UnavailableReason.Should().NotBeNullOrEmpty();
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

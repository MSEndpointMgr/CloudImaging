using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverSetResolverTests
{
    private readonly DriverSyncHarness _h = new();
    private readonly DriverSetResolver _resolver;

    public DriverSetResolverTests()
    {
        _resolver = new DriverSetResolver(_h.Bed.Store, _h.Bed.Models, _h.Bed.Pointers, [new HpDriverVendorAdapter(), new DellDriverVendorAdapter()]);
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"));
    }

    internal static DeviceSession Device(string? systemId, string manufacturer = "HP", string? sku = null) => new()
    {
        SessionId = Guid.NewGuid(),
        State = SessionState.SessionAssigned,
        DeviceSerialNumber = "5CG1234",
        DeviceManufacturer = manufacturer,
        DeviceModel = "EliteBook",
        Architecture = MachineArchitecture.X64,
        HardwareMetadata = new DeviceHardwareMetadata { MotherboardModel = systemId, SystemSku = sku },
    };

    internal static OsImage Image(WindowsRelease? release = DriverSyncHarness.Release, MachineArchitecture architecture = MachineArchitecture.X64) => new()
    {
        ImageId = Guid.NewGuid(),
        Name = "Windows 11",
        Version = "24H2",
        StoragePath = "os-images/w11.wim",
        Sha256Hash = new string('a', 64),
        WindowsRelease = release,
        Architecture = architecture,
    };

    private async Task<DriverModelRecord> ActiveModelWithSetAsync()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        return model;
    }

    [Fact]
    public async Task Hp_MatchesTheSystemIdCaseInsensitively_AndPinsTheCurrentSet()
    {
        var model = await ActiveModelWithSetAsync();
        await _h.Bed.Store.CriticalCategories.UpsertAsync("Hp", "Driver - Network", new DriverCriticalCategoryRecord { ChangedAt = DriverTestBed.Start });

        var pin = await _resolver.ResolveAsync(Device("8c26"), Image());

        pin.Should().Be(new DriverPin(model.ModelId, "HP EliteBook 840 G11", DriverSyncHarness.Release, 1, null, "8C26", "[\"Driver - Network\"]"));
    }

    [Fact]
    public async Task NoWindowsRelease_IsPinnedWithTheMatchKey()
    {
        await ActiveModelWithSetAsync();

        var pin = await _resolver.ResolveAsync(Device("8C26"), Image(release: null));

        pin.SkipReason.Should().Be(DriverSkipReason.NoWindowsRelease);
        pin.MatchKey.Should().Be("8C26");
        pin.SetVersion.Should().BeNull();
    }

    [Fact]
    public async Task NoMatchingModel_IsPinnedWithTheDevicesKey()
    {
        await ActiveModelWithSetAsync();

        var pin = await _resolver.ResolveAsync(Device("8A3B"), Image());

        pin.SkipReason.Should().Be(DriverSkipReason.NoMatchingModel);
        pin.MatchKey.Should().Be("8A3B");
        pin.ModelId.Should().BeNull();
    }

    [Fact]
    public async Task DraftModel_MatchesNothing()
    {
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11", DriverModelState.Draft);

        (await _resolver.ResolveAsync(Device("8C26"), Image())).SkipReason.Should().Be(DriverSkipReason.NoMatchingModel);
    }

    [Fact]
    public async Task ArchitectureMismatch_IsPinned()
    {
        await ActiveModelWithSetAsync();

        var pin = await _resolver.ResolveAsync(Device("8C26"), Image(architecture: MachineArchitecture.Arm64));

        pin.SkipReason.Should().Be(DriverSkipReason.ArchitectureMismatch);
        pin.ModelName.Should().Be("HP EliteBook 840 G11");
        pin.MatchKey.Should().Be("8C26");
    }

    [Fact]
    public async Task ReleaseNotOnboarded_IsPinned()
    {
        await ActiveModelWithSetAsync();

        var pin = await _resolver.ResolveAsync(Device("8C26"), Image(WindowsRelease.Windows11Version25H2));

        pin.SkipReason.Should().Be(DriverSkipReason.ReleaseNotOnboarded);
        pin.Release.Should().Be(WindowsRelease.Windows11Version25H2);
    }

    [Fact]
    public async Task NoDriverSet_IsPinned_WhileTheFirstSetIsStillBeingPrepared()
    {
        await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");

        var pin = await _resolver.ResolveAsync(Device("8C26"), Image());

        pin.SkipReason.Should().Be(DriverSkipReason.NoDriverSet);
        pin.MatchKey.Should().Be("8C26");
    }

    [Fact]
    public async Task SetInTesting_IsNeverMatched_ButIsAnOption_AndATestingChoicePinsIt()
    {
        var model = await ActiveModelWithSetAsync();
        var row = await _h.Bed.Pointers.GetOrCreateAsync(model.ModelId, DriverSyncHarness.Release);
        var current = (await _h.Bed.SetDocuments.GetAsync(model.ModelId, DriverSyncHarness.Release, 1))!;
        await _h.Bed.SetDocuments.SaveAsync(current with { Version = 2, Fingerprint = "two" });
        await _h.Bed.Pointers.TryUpdateAsync(model.ModelId, DriverSyncHarness.Release, row.Value with { TestingVersion = 2, TestingFingerprint = "two", LastVersion = 2 }, row.ETag);
        var device = Device("8C26");

        (await _resolver.ResolveAsync(device, Image())).SetVersion.Should().Be(1);
        var options = await _resolver.OptionsAsync(Image(), _h.Bed.SetDocuments);
        options.Select(o => (o.ModelId, o.Testing)).Should().Equal((model.ModelId, false), (model.ModelId, true));
        var (pin, reason) = await _resolver.ResolveChoiceAsync(device, Image(), new DriverChoice { Kind = DriverChoiceKind.Testing, ModelId = model.ModelId });
        reason.Should().BeNull();
        pin!.SetVersion.Should().Be(2);
    }

    [Fact]
    public async Task TestingChoice_WithoutASetInTesting_IsRefused()
    {
        var model = await ActiveModelWithSetAsync();

        var (pin, reason) = await _resolver.ResolveChoiceAsync(Device("8C26"), Image(), new DriverChoice { Kind = DriverChoiceKind.Testing, ModelId = model.ModelId });

        pin.Should().BeNull();
        reason.Should().Be("HP EliteBook 840 G11 has no driver set in testing for Windows 11, version 24H2.");
    }

    [Theory]
    [InlineData("0cb4")]
    [InlineData("0CB2")]
    public async Task Dell_MatchesTheSystemSku_OfAnySkuOfTheModel(string sku)
    {
        var model = new DriverModelRecord
        {
            ModelId = Guid.NewGuid(),
            Vendor = DriverVendor.Dell,
            Architecture = MachineArchitecture.X64,
            DisplayName = "Latitude 5450",
            CatalogModelKey = "LATITUDE_5450",
            MatchKeys = ["0CB2", "0CB4"],
            Releases = [DriverSyncHarness.Release],
            State = DriverModelState.Active,
        };
        await _h.Bed.Models.TryAddAsync(model);
        await _h.Bed.Models.TryReserveKeysAsync(DriverVendor.Dell, model.MatchKeys, model.ModelId);
        var pointer = await _h.Bed.Pointers.GetOrCreateAsync(model.ModelId, DriverSyncHarness.Release);
        await _h.Bed.Pointers.TryUpdateAsync(model.ModelId, DriverSyncHarness.Release, pointer.Value with { CurrentVersion = 3, LastVersion = 3 }, pointer.ETag);

        var pin = await _resolver.ResolveAsync(Device("0B1F", "Dell Inc.", sku), Image());

        pin.ModelId.Should().Be(model.ModelId);
        pin.SetVersion.Should().Be(3);
        pin.MatchKey.Should().Be(sku.ToUpperInvariant());
        pin.CriticalCategories.Should().Be("[]");
    }
}

/// <summary>The driver pin through the real assignment paths.</summary>
public sealed class DriverAssignmentIntegrationTests
{
    private readonly DriverSyncHarness _h = new();
    private readonly SetupProfileTestBed _bed = new();
    private readonly OsImageRepository _images;
    private readonly OsImage _image = DriverSetResolverTests.Image();

    public DriverAssignmentIntegrationTests()
    {
        _images = new OsImageRepository(_bed.Tables);
        _images.CreateAsync(_image).GetAwaiter().GetResult();
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"));
    }

    private DriverSetResolver Resolver => new(_h.Bed.Store, _h.Bed.Models, _h.Bed.Pointers, [_h.Adapter]);

    private async Task<DeviceSession> AddDeviceAsync(string systemId)
    {
        var session = DriverSetResolverTests.Device(systemId);
        await _bed.Sessions.CreateAsync(session);
        return session;
    }

    private async Task<DriverModelRecord> ActiveModelWithSetAsync()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        await _h.Bed.Store.CriticalCategories.UpsertAsync("Hp", "Driver - Network", new DriverCriticalCategoryRecord { ChangedAt = DriverTestBed.Start });
        return model;
    }

    [Fact]
    public async Task SingleAssign_PinsModelReleaseSetAndCriticalCategories_AndLaterChangesLeaveThePin()
    {
        var model = await ActiveModelWithSetAsync();
        var session = await AddDeviceAsync("8C26");
        var function = new AssignSessionFunction(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, Resolver, NullLogger<AssignSessionFunction>.Instance);

        var response = await function.Run(FunctionTestHost.Request("POST", $"https://core/api/internal/sessions/{session.SessionId}/assign", new { osImageId = _image.ImageId }), session.SessionId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var pinned = (await _bed.Sessions.GetByIdAsync(session.SessionId))!;
        pinned.DriverModelId.Should().Be(model.ModelId);
        pinned.DriverModelName.Should().Be("HP EliteBook 840 G11");
        pinned.DriverRelease.Should().Be(DriverSyncHarness.Release);
        pinned.DriverSetVersion.Should().Be(1);
        pinned.DriverMatchKey.Should().Be("8C26");
        pinned.DriverCriticalCategories.Should().Be("[\"Driver - Network\"]");

        // A newer set, a critical category change and removal of the model change nothing on the session.
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();
        await _h.Bed.Store.CriticalCategories.DeleteAsync("Hp", "Driver - Network");
        await _h.Bed.Models.DeleteAsync(model.ModelId);

        var later = (await _bed.Sessions.GetByIdAsync(session.SessionId))!;
        later.DriverSetVersion.Should().Be(1);
        later.DriverCriticalCategories.Should().Be("[\"Driver - Network\"]");
        later.DriverModelName.Should().Be("HP EliteBook 840 G11");
    }

    [Fact]
    public async Task BulkAssign_PinsEverySession_WithItsOwnMatchOrSkipReason()
    {
        var model = await ActiveModelWithSetAsync();
        var matched = await AddDeviceAsync("8C26");
        var unmatched = await AddDeviceAsync("8A3B");
        var service = new BulkAssignmentService(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, Resolver, NullLogger<BulkAssignmentService>.Instance);

        await service.AssignAsync([matched.SessionId, unmatched.SessionId], _image.ImageId);

        var first = (await _bed.Sessions.GetByIdAsync(matched.SessionId))!;
        first.DriverModelId.Should().Be(model.ModelId);
        first.DriverSetVersion.Should().Be(1);
        var second = (await _bed.Sessions.GetByIdAsync(unmatched.SessionId))!;
        second.DriverSkipReason.Should().Be(DriverSkipReason.NoMatchingModel);
        second.DriverMatchKey.Should().Be("8A3B");
        second.DriverSetVersion.Should().BeNull();
    }

    private BulkAssignFunction BulkFunction() =>
        new(new BulkAssignmentService(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, Resolver, NullLogger<BulkAssignmentService>.Instance), NullLogger<BulkAssignFunction>.Instance);

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> BulkAsync(object body) =>
        BulkFunction().Run(FunctionTestHost.Request("POST", "https://core/api/internal/sessions/bulk-assign", body), FunctionTestHost.Context);

    [Fact]
    public async Task BulkAssign_AModelChoice_PinsTheSetCurrentAtAssignment_AndNoneChosenPinsNothing_BothRecordWhoChose()
    {
        var model = await ActiveModelWithSetAsync();
        var unmatched = await AddDeviceAsync("8A3B");
        var declined = await AddDeviceAsync("8C26");
        var automatic = await AddDeviceAsync("8C26");

        // A sync between the preview and Start imaging: the set current at assignment is pinned.
        _h.PublishComponents("8C26", DriverSyncHarness.Component("sp100", "sp300", "1.1", "Intel Wireless LAN Driver"));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();

        var response = await BulkAsync(new
        {
            sessionIds = new[] { unmatched.SessionId, declined.SessionId, automatic.SessionId },
            osImageId = _image.ImageId,
            driverChoices = new object[] { new { sessionId = unmatched.SessionId, kind = "Model", modelId = model.ModelId }, new { sessionId = declined.SessionId, kind = "None" } },
            actor = new { upn = "tech@contoso.com", objectId = "oid" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var chosen = (await _bed.Sessions.GetByIdAsync(unmatched.SessionId))!;
        chosen.DriverModelId.Should().Be(model.ModelId);
        chosen.DriverSetVersion.Should().Be(2);
        chosen.DriverMatchKey.Should().Be("8A3B");
        chosen.DriverSkipReason.Should().BeNull();
        chosen.DriverChosenManually.Should().BeTrue();
        chosen.DriverChosenBy.Should().Be("tech@contoso.com");
        var none = (await _bed.Sessions.GetByIdAsync(declined.SessionId))!;
        none.DriverSkipReason.Should().Be(DriverSkipReason.NoneChosen);
        none.DriverSetVersion.Should().BeNull();
        none.DriverChosenManually.Should().BeTrue();
        var matched = (await _bed.Sessions.GetByIdAsync(automatic.SessionId))!;
        matched.DriverSetVersion.Should().Be(2);
        matched.DriverChosenManually.Should().BeFalse();
        matched.DriverChosenBy.Should().BeNull();
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("noSet")]
    [InlineData("architecture")]
    public async Task BulkAssign_AChoiceThatCanNoLongerBeUsed_Is409PerSession_AndAssignsNothing(string problem)
    {
        var model = await ActiveModelWithSetAsync();
        var first = await AddDeviceAsync("8A3B");
        var second = await AddDeviceAsync("8C26");
        switch (problem)
        {
            case "removed":
                await _h.Lifecycle.RemoveModelAsync(model.ModelId, "operator@contoso.com");
                break;
            case "noSet":
                await _h.Bed.Pointers.DeleteAsync(model.ModelId, DriverSyncHarness.Release);
                break;
            default:
                var row = (await _h.Bed.Models.GetAsync(model.ModelId))!;
                await _h.Bed.Models.TryUpdateAsync(row.Value with { Architecture = MachineArchitecture.Arm64 }, row.ETag);
                break;
        }

        var response = await BulkAsync(new
        {
            sessionIds = new[] { first.SessionId, second.SessionId },
            osImageId = _image.ImageId,
            driverChoices = new[] { new { sessionId = first.SessionId, kind = "Model", modelId = model.ModelId } },
            actor = new { upn = "tech@contoso.com" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var json = await response.ReadJsonAsync();
        json.GetProperty("detail").GetString().Should().Be("The chosen driver model is no longer available for 1 device. Choose again.");
        json.GetProperty("errors").EnumerateArray().Single().GetProperty("sessionId").GetGuid().Should().Be(first.SessionId);
        (await _bed.Sessions.GetByIdAsync(first.SessionId))!.State.Should().Be(SessionState.SessionAssigned);
        (await _bed.Sessions.GetByIdAsync(second.SessionId))!.State.Should().Be(SessionState.SessionAssigned, "nothing is assigned");
    }

    [Fact]
    public async Task BulkAssign_BadChoices_Are400()
    {
        var model = await ActiveModelWithSetAsync();
        var session = await AddDeviceAsync("8C26");
        var other = Guid.NewGuid();

        var notInRequest = await BulkAsync(new { sessionIds = new[] { session.SessionId }, osImageId = _image.ImageId, driverChoices = new[] { new { sessionId = other, kind = "None" } }, actor = new { upn = "t@contoso.com" } });
        var twice = await BulkAsync(new { sessionIds = new[] { session.SessionId }, osImageId = _image.ImageId, driverChoices = new[] { new { sessionId = session.SessionId, kind = "None" }, new { sessionId = session.SessionId, kind = "None" } }, actor = new { upn = "t@contoso.com" } });
        var noModel = await BulkAsync(new { sessionIds = new[] { session.SessionId }, osImageId = _image.ImageId, driverChoices = new[] { new { sessionId = session.SessionId, kind = "Model" } }, actor = new { upn = "t@contoso.com" } });
        var noActor = await BulkAsync(new { sessionIds = new[] { session.SessionId }, osImageId = _image.ImageId, driverChoices = new[] { new { sessionId = session.SessionId, kind = "Model", modelId = model.ModelId } } });

        notInRequest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        twice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noModel.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noActor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _bed.Sessions.GetByIdAsync(session.SessionId))!.State.Should().Be(SessionState.SessionAssigned);
    }

    [Fact]
    public async Task SingleAssign_AppliesTheSameChoiceRules()
    {
        var model = await ActiveModelWithSetAsync();
        var session = await AddDeviceAsync("8A3B");
        var function = new AssignSessionFunction(_bed.Sessions, _images, _bed.Config, _bed.BlobService, _bed.Resolver, Resolver, NullLogger<AssignSessionFunction>.Instance);
        Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> AssignAsync(object body) =>
            function.Run(FunctionTestHost.Request("POST", $"https://core/api/internal/sessions/{session.SessionId}/assign", body), session.SessionId.ToString(), FunctionTestHost.Context);

        var noActor = await AssignAsync(new { osImageId = _image.ImageId, driverChoice = new { kind = "Model", modelId = model.ModelId } });
        var unknown = await AssignAsync(new { osImageId = _image.ImageId, driverChoice = new { kind = "Model", modelId = Guid.NewGuid() }, actor = new { upn = "tech@contoso.com" } });
        var ok = await AssignAsync(new { osImageId = _image.ImageId, driverChoice = new { kind = "Model", modelId = model.ModelId }, actor = new { upn = "tech@contoso.com" } });

        noActor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var pinned = (await _bed.Sessions.GetByIdAsync(session.SessionId))!;
        pinned.DriverModelId.Should().Be(model.ModelId);
        pinned.DriverSetVersion.Should().Be(1);
        pinned.DriverChosenManually.Should().BeTrue();
        pinned.DriverChosenBy.Should().Be("tech@contoso.com");
    }
}

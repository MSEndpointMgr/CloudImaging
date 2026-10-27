using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Repositories;

public sealed class DriverRepositoriesTests
{
    private readonly DriverTestBed _bed = new();

    [Fact]
    public async Task TryReserveKeys_KeyOwnedByAnotherModel_ReturnsTheOwnerAndReservesNothing()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        (await _bed.Models.TryReserveKeysAsync(DriverVendor.Hp, ["8c26"], first)).Should().BeNull();

        var conflict = await _bed.Models.TryReserveKeysAsync(DriverVendor.Hp, ["8C27", "8C26"], second);

        conflict.Should().Be(("8C26", first));
        (await _bed.Models.GetOwnerAsync(DriverVendor.Hp, "8C27")).Should().BeNull();
    }

    [Fact]
    public async Task TryReserveKeys_SameKeyOtherVendor_DoesNotConflict()
    {
        await _bed.Models.TryReserveKeysAsync(DriverVendor.Hp, ["0CB2"], Guid.NewGuid());

        var conflict = await _bed.Models.TryReserveKeysAsync(DriverVendor.Dell, ["0CB2"], Guid.NewGuid());

        conflict.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseKeys_AfterRemoval_KeyCanBeReservedAgain()
    {
        var first = Guid.NewGuid();
        await _bed.Models.TryReserveKeysAsync(DriverVendor.Dell, ["0CB2", "0CB4"], first);

        await _bed.Models.ReleaseKeysAsync(DriverVendor.Dell, ["0CB2", "0CB4"], first);

        (await _bed.Models.TryReserveKeysAsync(DriverVendor.Dell, ["0CB4"], Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task DraftModel_ReservesItsKeys_LikeAnActiveModel()
    {
        var draft = new DriverModelRecord
        {
            ModelId = Guid.NewGuid(),
            Vendor = DriverVendor.Hp,
            DisplayName = "HP EliteBook 840 G11",
            CatalogModelKey = "8C26_X64",
            MatchKeys = ["8C26"],
            State = DriverModelState.Draft,
            Architecture = MachineArchitecture.X64,
        };
        await _bed.Models.TryAddAsync(draft);
        await _bed.Models.TryReserveKeysAsync(DriverVendor.Hp, draft.MatchKeys, draft.ModelId);

        var conflict = await _bed.Models.TryReserveKeysAsync(DriverVendor.Hp, ["8C26"], Guid.NewGuid());

        conflict.Should().Be(("8C26", draft.ModelId));
    }

    [Fact]
    public async Task SetPointerUpdate_StaleETag_IsRefused()
    {
        var modelId = Guid.NewGuid();
        var read = await _bed.Pointers.GetOrCreateAsync(modelId, WindowsRelease.Windows11Version24H2);
        (await _bed.Pointers.TryUpdateAsync(modelId, WindowsRelease.Windows11Version24H2, read.Value with { LastVersion = 1 }, read.ETag)).Should().BeTrue();

        var stale = await _bed.Pointers.TryUpdateAsync(modelId, WindowsRelease.Windows11Version24H2, read.Value with { LastVersion = 2 }, read.ETag);

        stale.Should().BeFalse();
        (await _bed.Pointers.GetAsync(modelId, WindowsRelease.Windows11Version24H2))!.Value.LastVersion.Should().Be(1);
    }

    [Fact]
    public async Task ActiveRunLock_SecondRun_IsRefusedUntilReleased()
    {
        var first = Guid.NewGuid();
        (await _bed.Runs.TryAcquireAsync(first, DriverTestBed.Start)).Should().BeTrue();

        (await _bed.Runs.TryAcquireAsync(Guid.NewGuid(), DriverTestBed.Start)).Should().BeFalse();
        await _bed.Runs.ReleaseAsync(first);

        (await _bed.Runs.TryAcquireAsync(Guid.NewGuid(), DriverTestBed.Start)).Should().BeTrue();
    }

    [Fact]
    public async Task ActiveRunLock_ReleaseByAnotherRun_KeepsTheLock()
    {
        var holder = Guid.NewGuid();
        await _bed.Runs.TryAcquireAsync(holder, DriverTestBed.Start);

        await _bed.Runs.ReleaseAsync(Guid.NewGuid());

        (await _bed.Runs.GetActiveAsync())!.RunId.Should().Be(holder);
    }

    [Theory]
    [InlineData(DriverWorkKind.ScheduledSync)]
    [InlineData(DriverWorkKind.FetchVendorCatalog)]
    [InlineData(DriverWorkKind.ResolveModel)]
    [InlineData(DriverWorkKind.DownloadFile)]
    [InlineData(DriverWorkKind.BuildSet)]
    [InlineData(DriverWorkKind.FinalizeRun)]
    public async Task Enqueue_EachKind_IsSentToDriverWorkWithItsDelay(DriverWorkKind kind)
    {
        await _bed.Work.EnqueueAsync(new DriverWorkMessage { Kind = kind, ModelId = Guid.NewGuid() }, TimeSpan.FromMinutes(2));

        var sent = _bed.Queues.Sent.Should().ContainSingle().Subject;
        sent.QueueName.Should().Be(DriverWorkSender.QueueName);
        sent.Delay.Should().Be(TimeSpan.FromMinutes(2));
        sent.Work.Kind.Should().Be(kind);
        sent.Work.NotBefore.Should().BeNull();
    }

    [Fact]
    public async Task Enqueue_DelayOverSevenDays_IsCappedAndCarriesNotBefore()
    {
        await _bed.Work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.ScheduledSync }, TimeSpan.FromDays(20));

        var sent = _bed.Queues.Sent.Should().ContainSingle().Subject;
        sent.Delay.Should().Be(DriverWorkSender.MaxDelay);
        sent.Work.NotBefore.Should().Be(DriverTestBed.Start + TimeSpan.FromDays(20));
    }

    [Fact]
    public async Task Enqueue_NoDelay_SendsImmediatelyAndNeverExpires()
    {
        await _bed.Work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.DownloadFile }, TimeSpan.Zero);

        var sent = _bed.Queues.Sent.Should().ContainSingle().Subject;
        sent.Delay.Should().BeNull();
        sent.TimeToLive.Should().Be(TimeSpan.FromSeconds(-1));
    }

    [Fact]
    public async Task EnsureQueuesExist_CreatesWorkAndPoisonQueues()
    {
        await _bed.Work.EnsureQueuesExistAsync();

        _bed.Queues.Created.Should().BeEquivalentTo(DriverWorkSender.QueueName, DriverWorkSender.PoisonQueueName);
    }
}

using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

/// <summary>
/// The sync schedule is one delayed ScheduledSync message per slot: computed from the stored
/// schedule in its time zone, claimed once per slot with the settings ETag, and never a timer.
/// The clock starts on Sunday 2026-10-25 09:00 UTC.
/// </summary>
public sealed class DriverSyncSchedulerTests
{
    private const int Monday = 1;
    private const int MondayToFriday = 0x1F;
    private readonly DriverSyncHarness _h = new();

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private async Task SaveAsync(DriverSyncSchedule schedule, int weekdays = MondayToFriday, int repeatWeeks = 1, string localTime = "02:00", string zone = "Etc/UTC")
    {
        var result = await _h.Scheduler.SetSettingsAsync(new Contracts.Models.Drivers.SetDriverSyncSettingsRequest
        {
            Schedule = schedule,
            Weekdays = weekdays,
            RepeatWeeks = repeatWeeks,
            LocalTime = localTime,
            TimeZone = zone,
            Actor = new Contracts.Models.Drivers.DriverActor { Upn = "admin@contoso.com" },
        });
        result.Status.Should().Be(System.Net.HttpStatusCode.OK, result.Detail);
    }

    private List<DateTimeOffset> ScheduledSlots() =>
        _h.Bed.Queues.Sent.Where(m => m.Work.Kind == DriverWorkKind.ScheduledSync).Select(m => m.Work.SlotUtc!.Value).ToList();

    private static List<DateTimeOffset> Slots(DriverSyncSettingsRecord settings, DateTimeOffset from, int count)
    {
        var slots = new List<DateTimeOffset>();
        var after = from;
        for (var i = 0; i < count && DriverSyncScheduler.NextSlot(settings, after) is { } next; i++)
        {
            slots.Add(next);
            after = next;
        }

        return slots;
    }

    [Fact]
    public async Task Default_IsDailyAt0200_AndCoreStartEnqueuesTheNextSlot()
    {
        var slot = await _h.Scheduler.EnqueueNextAsync();

        slot.Should().Be(Utc(10, 26, 2));
        var sent = _h.Bed.Queues.Sent.Should().ContainSingle().Subject;
        sent.Work.Kind.Should().Be(DriverWorkKind.ScheduledSync);
        sent.Delay.Should().Be(Utc(10, 26, 2) - DriverTestBed.Start);
    }

    [Fact]
    public async Task SavingTheSchedule_EnqueuesOneMessage_ForTheSlotItReturnsAsNextRunAt()
    {
        await SaveAsync(DriverSyncSchedule.SelectedDays, MondayToFriday, zone: "Europe/Berlin");

        var settings = await _h.Scheduler.GetSettingsAsync(false);
        ScheduledSlots().Should().Equal(settings.NextRunAt!.Value);
        settings.NextRunAt.Should().Be(Utc(10, 26, 1), "02:00 in Berlin on Monday 2026-10-26 is 01:00 UTC after the clocks went back");
    }

    [Fact]
    public async Task Manual_EnqueuesNothing_AndHasNoNextRun()
    {
        await SaveAsync(DriverSyncSchedule.Manual);

        ScheduledSlots().Should().BeEmpty();
        (await _h.Scheduler.GetSettingsAsync(false)).NextRunAt.Should().BeNull();
    }

    [Fact]
    public void SelectedDays_RunsOnlyOnSelectedDays()
    {
        var settings = new DriverSyncSettingsRecord { Schedule = DriverSyncSchedule.SelectedDays, Weekdays = 0b0010101 };

        Slots(settings, DriverTestBed.Start, 4).Should().Equal(Utc(10, 26, 2), Utc(10, 28, 2), Utc(10, 30, 2), Utc(11, 2, 2));
    }

    [Fact]
    public async Task EveryTwoWeeks_SavedOnSunday_RunsTheNextMondayThenTwoWeeksLater()
    {
        await SaveAsync(DriverSyncSchedule.SelectedDays, Monday, repeatWeeks: 2);

        var stored = (await _h.Bed.Store.GetSyncSettingsAsync()).Value;
        stored.AnchorWeek.Should().Be("2026-10-26");
        Slots(stored, DriverTestBed.Start, 3).Should().Equal(Utc(10, 26, 2), Utc(11, 9, 2), Utc(11, 23, 2));
        (await _h.Scheduler.GetSettingsAsync(false)).NextRunAt.Should().Be(Utc(10, 26, 2));
    }

    [Theory]
    [InlineData(3, 21)]
    [InlineData(4, 28)]
    public async Task EveryNWeeks_RunsOnlyInWeeksAMultipleAwayFromTheAnchor(int repeat, int gapDays)
    {
        await SaveAsync(DriverSyncSchedule.SelectedDays, Monday, repeatWeeks: repeat);

        var slots = Slots((await _h.Bed.Store.GetSyncSettingsAsync()).Value, DriverTestBed.Start, 3);

        slots[0].Should().Be(Utc(10, 26, 2));
        (slots[1] - slots[0]).Should().Be(TimeSpan.FromDays(gapDays));
        (slots[2] - slots[1]).Should().Be(TimeSpan.FromDays(gapDays));
    }

    [Fact]
    public async Task OfSeveralCopiesForOneSlot_OnlyTheClaimingOneStartsARunAndEnqueuesTheNextSlot()
    {
        await _h.Scheduler.EnqueueNextAsync();
        await _h.Scheduler.EnqueueNextAsync();
        var copies = _h.Bed.Queues.Drain().Select(m => m.Work).ToList();
        _h.Bed.Time.Advance(Utc(10, 26, 2) - DriverTestBed.Start);

        foreach (var copy in copies)
        {
            await _h.Scheduler.HandleAsync(copy);
        }

        (await _h.Bed.Runs.ListAsync()).Should().ContainSingle(r => r.Trigger == DriverSyncTrigger.Scheduled);
        ScheduledSlots().Should().Equal(Utc(10, 27, 2));
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.LastScheduledSlotUtc.Should().Be(Utc(10, 26, 2));
    }

    [Fact]
    public async Task AnEarlyCopy_PutsItselfBackUntilItsSlot()
    {
        await _h.Scheduler.EnqueueNextAsync();
        var message = _h.Bed.Queues.Drain().Single().Work;
        _h.Bed.Time.Advance(TimeSpan.FromHours(1));

        await _h.Scheduler.HandleAsync(message);

        var sent = _h.Bed.Queues.Sent.Should().ContainSingle().Subject;
        sent.Work.SlotUtc.Should().Be(Utc(10, 26, 2));
        sent.Delay.Should().Be(Utc(10, 26, 2) - _h.Bed.Time.GetUtcNow());
        (await _h.Bed.Runs.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AMessageForAChangedSchedule_StartsNoRun()
    {
        await _h.Scheduler.EnqueueNextAsync();
        var stale = _h.Bed.Queues.Drain().Single().Work;
        await SaveAsync(DriverSyncSchedule.Daily, localTime: "04:00");
        _h.Bed.Queues.Sent.Clear();
        _h.Bed.Time.Advance(Utc(10, 26, 2) - DriverTestBed.Start);

        await _h.Scheduler.HandleAsync(stale);

        (await _h.Bed.Runs.ListAsync()).Should().BeEmpty();
        _h.Bed.Queues.Sent.Should().BeEmpty("the save already enqueued the 04:00 slot");
    }

    [Fact]
    public async Task ASlotMissedByMoreThanSixHours_StartsNoRun_ButEnqueuesTheNextSlot()
    {
        await _h.Scheduler.EnqueueNextAsync();
        var message = _h.Bed.Queues.Drain().Single().Work;
        _h.Bed.Time.Advance(Utc(10, 26, 9) - DriverTestBed.Start);

        await _h.Scheduler.HandleAsync(message);

        (await _h.Bed.Runs.ListAsync()).Should().BeEmpty();
        ScheduledSlots().Should().Equal(Utc(10, 27, 2));
    }

    [Fact]
    public async Task ASlotThatPassesWhileARunIsActive_StartsNoSecondRun()
    {
        await _h.Sync.StartRunAsync(DriverSyncTrigger.Manual, "operator@contoso.com");
        await _h.Scheduler.EnqueueNextAsync();
        var message = _h.Bed.Queues.Sent.Single(m => m.Work.Kind == DriverWorkKind.ScheduledSync).Work;
        _h.Bed.Queues.Sent.Clear();
        _h.Bed.Time.Advance(Utc(10, 26, 2) - DriverTestBed.Start);

        await _h.Scheduler.HandleAsync(message);

        (await _h.Bed.Runs.ListAsync()).Should().ContainSingle(r => r.Trigger == DriverSyncTrigger.Manual);
        ScheduledSlots().Should().Equal(Utc(10, 27, 2));
    }

    [Fact]
    public void DstGap_RunsAtTheNextValidMinute()
    {
        var settings = new DriverSyncSettingsRecord { Schedule = DriverSyncSchedule.Daily, LocalTime = "02:30", TimeZone = "Europe/Berlin" };

        DriverSyncScheduler.NextSlot(settings, new DateTimeOffset(2027, 3, 27, 12, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), "02:30 does not exist that night; 03:00 CEST is 01:00 UTC");
    }

    [Fact]
    public void DstOverlap_RunsOnce()
    {
        var settings = new DriverSyncSettingsRecord { Schedule = DriverSyncSchedule.Daily, LocalTime = "02:30", TimeZone = "Europe/Berlin" };

        Slots(settings, Utc(10, 24, 12), 2).Should().Equal(Utc(10, 25, 0, 30), Utc(10, 26, 1, 30));
    }

    [Fact]
    public void TheTimeZoneList_Has142UniqueEntries_ThatAllResolveOnThisHost()
    {
        DriverTimeZoneCatalog.All.Should().HaveCount(142);
        DriverTimeZoneCatalog.All.Select(z => z.IanaId).Should().OnlyHaveUniqueItems();
        DriverTimeZoneCatalog.Offered.Should().HaveCount(142, "every ianaId must resolve on Windows and on the Linux CI runner");
        DriverTimeZoneCatalog.All.Should().Contain(z => z.IanaId == "Etc/UTC" && z.DisplayName == "(UTC) Coordinated Universal Time");
        DriverTimeZoneCatalog.All.Single(z => z.IanaId == "Europe/Berlin").Covers.Should().Contain("Europe/Stockholm");
    }
}

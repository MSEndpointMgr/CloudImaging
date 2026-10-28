using System.Globalization;
using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// The sync schedule (research R1, R6): there is no timer. One <c>ScheduledSync</c> message waits
/// on the queue for the next slot. Saving the schedule and every Core start enqueue one; the copy
/// that claims the slot with the settings row's ETag starts the run and enqueues the next slot,
/// and every other copy is dropped, so the number of messages never grows.
/// </summary>
public sealed partial class DriverSyncScheduler
{
    /// <summary>A slot that arrives later than this (an outage) starts no run.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromHours(6);

    private const int MaxConcurrencyRetries = 20;
    private const int AllDays = 0x7F;
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private readonly DriverStore _store;
    private readonly DriverSyncService _sync;
    private readonly DriverSyncRunRepository _runs;
    private readonly IDriverWorkSender _work;
    private readonly TimeProvider _time;
    private readonly ILogger<DriverSyncScheduler> _logger;

    /// <param name="store">Driver tables.</param>
    /// <param name="sync">Starts runs.</param>
    /// <param name="runs">Runs, for the active run.</param>
    /// <param name="work">Queue.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public DriverSyncScheduler(DriverStore store, DriverSyncService sync, DriverSyncRunRepository runs, IDriverWorkSender work, TimeProvider time, ILogger<DriverSyncScheduler> logger)
    {
        _store = store;
        _sync = sync;
        _runs = runs;
        _work = work;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The first slot of the schedule strictly after <paramref name="afterUtc"/>, or null for
    /// Manual. A local time a DST change skips runs at the next valid minute; a repeated hour runs
    /// once, at its first occurrence.
    /// </summary>
    public static DateTimeOffset? NextSlot(DriverSyncSettingsRecord settings, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Schedule == DriverSyncSchedule.Manual || (settings.Schedule == DriverSyncSchedule.SelectedDays && (settings.Weekdays & AllDays) == 0))
        {
            return null;
        }

        var zone = DriverTimeZoneCatalog.TryGet(settings.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var time = TryParseTime(settings.LocalTime, out var parsed) ? parsed : new TimeOnly(2, 0);
        var anchor = settings.AnchorWeek is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var week) ? week : (DateOnly?)null;
        var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime).AddDays(-1);

        // Four weeks cover every repeat; one extra week covers the first week after the anchor.
        for (var offset = 0; offset < 7 * 5 + 2; offset++)
        {
            var day = startDay.AddDays(offset);
            if (!IsDue(settings, anchor, day))
            {
                continue;
            }

            var slot = ToUtc(day.ToDateTime(time), zone);
            if (slot > afterUtc)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>The Monday, in the schedule's zone, of the week that holds the next selected day after <paramref name="nowUtc"/>.</summary>
    public static string AnchorFor(DriverSyncSettingsRecord settings, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var zone = DriverTimeZoneCatalog.TryGet(settings.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var next = NextSlot(settings with { RepeatWeeks = 1, AnchorWeek = null }, nowUtc) ?? nowUtc;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, zone).DateTime);
        return Monday(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Enqueues the message for the next slot of the stored schedule; returns the slot, or null for Manual.</summary>
    public async Task<DateTimeOffset?> EnqueueNextAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        var slot = NextSlot(settings, now);
        if (slot is { } next)
        {
            await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.ScheduledSync, SlotUtc = next }, next - now, ct);
        }

        return slot;
    }

    /// <summary>Handles a <c>ScheduledSync</c> message.</summary>
    public async Task HandleAsync(DriverWorkMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SlotUtc is not { } slot)
        {
            return;
        }

        var now = _time.GetUtcNow();
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _store.GetSyncSettingsAsync(ct);
            var settings = row.Value;

            // A slot the stored schedule no longer has belongs to a schedule that was changed.
            if (NextSlot(settings, slot - TimeSpan.FromTicks(1)) != slot)
            {
                LogStale(_logger, slot);
                return;
            }

            if (now < slot)
            {
                await _work.EnqueueAsync(message with { NotBefore = null }, slot - now, ct);
                return;
            }

            if (settings.LastScheduledSlotUtc >= slot)
            {
                return;
            }

            var claimed = settings with { LastScheduledSlotUtc = slot };
            var saved = row.ETag == default
                ? await _store.SyncSettings.TryAddAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, claimed, ct)
                : await _store.SyncSettings.TryUpdateAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, claimed, row.ETag, ct);
            if (!saved)
            {
                continue;
            }

            if (NextSlot(claimed, slot) is { } next)
            {
                await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.ScheduledSync, SlotUtc = next }, next - now, ct);
            }

            if (now - slot > MissedAfter)
            {
                LogMissed(_logger, slot);
                return;
            }

            var start = await _sync.StartRunAsync(DriverSyncTrigger.Scheduled, null, null, ct);
            if (start.Status != DriverRunStartStatus.Started)
            {
                LogNotStarted(_logger, slot, start.Status);
            }

            return;
        }
    }

    /// <summary>The settings as the Sync tab shows them, with the next slot and when Sync now is available.</summary>
    public async Task<DriverSyncSettings> GetSettingsAsync(bool withTimeZones, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;

        // While a run is active, Sync now waits for it; the watchdog bounds how long that can be.
        var available = settings.LastRunStartedAt + DriverSyncService.RunLimit;
        if (await _runs.GetActiveAsync(ct) is { } active && (available is null || active.StartedAt + DriverSyncService.Watchdog > available))
        {
            available = active.StartedAt + DriverSyncService.Watchdog;
        }

        return new DriverSyncSettings
        {
            Schedule = settings.Schedule,
            Weekdays = settings.Weekdays,
            RepeatWeeks = settings.RepeatWeeks,
            LocalTime = settings.LocalTime,
            TimeZone = settings.TimeZone,
            WaitForApprovalOfNewComponents = settings.WaitForApprovalOfNewComponents,
            NextRunAt = NextSlot(settings, now),
            SyncNowAvailableAt = available > now ? available : null,
            LastRunStartedAt = settings.LastRunStartedAt,
            ChangedAt = settings.ChangedAt,
            TimeZones = withTimeZones ? DriverTimeZoneCatalog.Offered : [],
        };
    }

    /// <summary>Saves the schedule and the approval setting; the next slot is enqueued at once.</summary>
    public async Task<DriverResult> SetSettingsAsync(SetDriverSyncSettingsRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var now = _time.GetUtcNow();
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _store.GetSyncSettingsAsync(ct);
            var before = row.Value;
            var next = before with
            {
                Schedule = request.Schedule,
                Weekdays = request.Weekdays & AllDays,
                RepeatWeeks = request.RepeatWeeks,
                LocalTime = request.LocalTime!,
                TimeZone = request.TimeZone!,
                WaitForApprovalOfNewComponents = request.WaitForApprovalOfNewComponents,
                ChangedAt = now,
                ChangedBy = upn,
            };
            next = next with { AnchorWeek = AnchorFor(next, now) };
            var saved = row.ETag == default
                ? await _store.SyncSettings.TryAddAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, ct)
                : await _store.SyncSettings.TryUpdateAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, row.ETag, ct);
            if (!saved)
            {
                continue;
            }

            if (Describe(before) != Describe(next))
            {
                await _store.LogAsync(now, upn, request.Actor.ObjectId, "Sync", "Schedule", $"{Describe(before)} to {Describe(next)}", ct);
            }

            if (before.WaitForApprovalOfNewComponents != next.WaitForApprovalOfNewComponents)
            {
                await _store.LogAsync(now, upn, request.Actor.ObjectId, "Sync", "New components", next.WaitForApprovalOfNewComponents ? "Wait for approval" : "Use automatically", ct);
            }

            await EnqueueNextAsync(ct);
            return DriverResult.Ok(await GetSettingsAsync(false, ct));
        }

        return DriverResult.Conflict("The sync settings changed while they were being saved. Try again.");
    }

    private static DriverResult? Validate(SetDriverSyncSettingsRequest request)
    {
        var errors = new List<(string Field, string Detail)>();
        if (!Enum.IsDefined(request.Schedule))
        {
            errors.Add(("schedule", "Choose a schedule from the list."));
        }

        if (!TryParseTime(request.LocalTime, out _))
        {
            errors.Add(("localTime", "Enter a time as HH:mm."));
        }

        if (!DriverTimeZoneCatalog.TryGet(request.TimeZone, out _))
        {
            errors.Add(("timeZone", "Choose a time zone from the list."));
        }

        if (request.Schedule == DriverSyncSchedule.SelectedDays && (request.Weekdays & AllDays) == 0)
        {
            errors.Add(("weekdays", "Choose at least one day."));
        }

        if (request.RepeatWeeks is < 1 or > 4)
        {
            errors.Add(("repeatWeeks", "Choose a repeat from the list."));
        }

        return errors.Count == 0
            ? null
            : new DriverResult(HttpStatusCode.BadRequest, Detail: errors[0].Detail, Extra: new Dictionary<string, object?>
            {
                ["errors"] = errors.Select(e => new { field = e.Field, detail = e.Detail }).ToList(),
            });
    }

    private static bool IsDue(DriverSyncSettingsRecord settings, DateOnly? anchor, DateOnly day)
    {
        if (settings.Schedule == DriverSyncSchedule.Daily)
        {
            return true;
        }

        var bit = 1 << (((int)day.DayOfWeek + 6) % 7);
        if ((settings.Weekdays & bit) == 0)
        {
            return false;
        }

        // Selected days every N weeks run only in weeks a multiple of N away from the anchor week.
        if (settings.RepeatWeeks <= 1 || anchor is not { } start)
        {
            return true;
        }

        var weeks = (Monday(day).DayNumber - Monday(start).DayNumber) / 7;
        return weeks >= 0 && weeks % settings.RepeatWeeks == 0;
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var minutes = 0; minutes < 24 * 60 && zone.IsInvalidTime(local); minutes++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value ?? string.Empty, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Describe(DriverSyncSettingsRecord settings)
    {
        var when = settings.Schedule switch
        {
            DriverSyncSchedule.Manual => "Manually only",
            DriverSyncSchedule.SelectedDays => $"Selected days ({Days(settings.Weekdays)}) every {(settings.RepeatWeeks == 1 ? "week" : $"{settings.RepeatWeeks} weeks")}",
            _ => "Every day",
        };
        return settings.Schedule == DriverSyncSchedule.Manual ? when : $"{when} at {settings.LocalTime} {settings.TimeZone}";
    }

    private static string Days(int mask) =>
        string.Join(", ", DayNames.Where((_, i) => (mask & (1 << i)) != 0));

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled driver sync for {Slot} was dropped: the schedule changed.")]
    private static partial void LogStale(ILogger logger, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled driver sync for {Slot} was missed by more than 6 hours and did not run.")]
    private static partial void LogMissed(ILogger logger, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled driver sync for {Slot} did not start: {Status}.")]
    private static partial void LogNotStarted(ILogger logger, DateTimeOffset slot, DriverRunStartStatus status);
}

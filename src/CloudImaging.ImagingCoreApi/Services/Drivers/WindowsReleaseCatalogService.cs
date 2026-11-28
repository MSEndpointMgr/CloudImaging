using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// The Windows releases Cloud Imaging offers for OS images and models: the releases it ships
/// with, plus every feature update Microsoft has listed, so a new release can be chosen the day
/// Microsoft ships it rather than when a vendor publishes drivers for it.
/// </summary>
public sealed partial class WindowsReleaseCatalogService
{
    /// <summary>Microsoft's list is read again once the stored one is this old.</summary>
    public static readonly TimeSpan RefreshAge = TimeSpan.FromHours(12);

    /// <summary>A failed read is tried again after this long, so a permission granted later shows soon.</summary>
    public static readonly TimeSpan RetryAge = TimeSpan.FromHours(1);

    private readonly DriverStore _store;
    private readonly IWindowsReleaseSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger<WindowsReleaseCatalogService> _logger;

    /// <param name="store">Driver tables, which hold the catalog row.</param>
    /// <param name="source">Microsoft's feature update catalog.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public WindowsReleaseCatalogService(DriverStore store, IWindowsReleaseSource source, TimeProvider time, ILogger<WindowsReleaseCatalogService> logger)
    {
        _store = store;
        _source = source;
        _time = time;
        _logger = logger;
    }

    /// <summary>Offered even when Microsoft's list cannot be read.</summary>
    public static IReadOnlyList<WindowsRelease> BuiltIn { get; } =
        [WindowsRelease.Windows10Version22H2, WindowsRelease.Windows11Version23H2, WindowsRelease.Windows11Version24H2, WindowsRelease.Windows11Version25H2];

    /// <summary>The releases offered, newest first, reading Microsoft's list first when the stored one is due.</summary>
    public async Task<WindowsReleaseList> GetAsync(CancellationToken ct = default)
    {
        var record = await ReadStoredAsync(ct);
        if (IsDue(record))
        {
            record = await RefreshAsync(record, ct);
        }

        return new WindowsReleaseList
        {
            Releases = [.. BuiltIn.Concat(record.Releases.Select(e => e.Release)).Distinct().OrderDescending()],
            RefreshedAt = record.RefreshedAt,
            Problem = record.LastError,
        };
    }

    private async Task<WindowsReleaseCatalogRecord> ReadStoredAsync(CancellationToken ct) =>
        (await _store.WindowsReleases.GetAsync(DriverStore.SettingsPartition, DriverStore.WindowsReleasesRow, ct))?.Value ?? new WindowsReleaseCatalogRecord();

    private bool IsDue(WindowsReleaseCatalogRecord record) =>
        record.CheckedAt is not { } checkedAt || _time.GetUtcNow() - checkedAt >= (record.LastError is null ? RefreshAge : RetryAge);

    private async Task<WindowsReleaseCatalogRecord> RefreshAsync(WindowsReleaseCatalogRecord record, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var read = await _source.ReadAsync(ct);
        if (read.Problem is { } problem)
        {
            LogReadFailed(_logger, problem);
            record = record with { CheckedAt = now, LastError = problem };
        }
        else
        {
            // Microsoft drops a release once it is no longer deployable; OS images and models still use it.
            var releases = record.Releases.ToDictionary(e => e.Release);
            foreach (var entry in read.Releases)
            {
                releases[entry.Release] = entry;
            }

            record = record with { Releases = [.. releases.Values.OrderBy(e => e.Release)], CheckedAt = now, RefreshedAt = now, LastError = null };
        }

        await _store.WindowsReleases.UpsertAsync(DriverStore.SettingsPartition, DriverStore.WindowsReleasesRow, record, ct);
        return record;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows releases could not be read from Microsoft: {Problem}")]
    private static partial void LogReadFailed(ILogger logger, string problem);
}

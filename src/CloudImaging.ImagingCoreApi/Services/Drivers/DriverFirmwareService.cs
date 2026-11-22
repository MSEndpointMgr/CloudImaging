using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>The BIOS a device is updated to, decided at registration.</summary>
/// <param name="ModelId">Matched model.</param>
/// <param name="PlatformId">HP platform ID.</param>
/// <param name="Version">Version the device is updated to.</param>
/// <param name="InstalledVersion">Version the device reported, if it could be read.</param>
public sealed record DriverFirmwarePin(Guid ModelId, string PlatformId, string Version, string? InstalledVersion)
{
    /// <summary>Writes the pin onto a session.</summary>
    public DeviceSession ApplyTo(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session with { FirmwareModelId = ModelId, FirmwarePlatformId = PlatformId, FirmwareVersion = Version, FirmwareInstalledVersion = InstalledVersion };
    }
}

/// <summary>What a sync did to the BIOS of one platform.</summary>
public enum DriverFirmwareMergeChange
{
    /// <summary>Nothing new.</summary>
    None,

    /// <summary>The platform was read for the first time.</summary>
    New,

    /// <summary>A newer version is what every location gets.</summary>
    Released,

    /// <summary>A newer version went into pre-production.</summary>
    PreProduction,

    /// <summary>A newer version was found while a roll back holds an earlier one.</summary>
    Held,
}

/// <summary>
/// HP BIOS updates per model (the Firmware tab): one row per platform ID of the model, read from
/// HP's BIOS catalog by the driver sync. The action is Skip unless chosen on the model page.
/// With "Pre-production first", a newer version first goes only to devices of the pre-production
/// location, until someone releases it. Devices are pinned to a version at registration.
/// </summary>
public sealed class DriverFirmwareService
{
    /// <summary>Name of the component.</summary>
    public const string ComponentName = "System BIOS";

    /// <summary>Category shown on the row.</summary>
    public const string Category = "Firmware";

    /// <summary>Change log area.</summary>
    public const string LogArea = "Firmware";

    /// <summary>Newest catalog releases kept per platform, for roll backs.</summary>
    public const int KeptReleases = 30;

    private const int MaxConcurrencyRetries = 20;

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverStorageService _storage;
    private readonly IDriverWorkSender _work;
    private readonly LocationRepository _locations;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="storage">Stored files.</param>
    /// <param name="work">Queue, for downloads.</param>
    /// <param name="locations">Locations, for the pre-production location's name.</param>
    /// <param name="time">Clock.</param>
    public DriverFirmwareService(DriverStore store, DriverModelRepository models, DriverStorageService storage, IDriverWorkSender work, LocationRepository locations, TimeProvider time)
    {
        _store = store;
        _models = models;
        _storage = storage;
        _work = work;
        _locations = locations;
        _time = time;
    }

    /// <summary>True for models whose BIOS Cloud Imaging can update: HP x64.</summary>
    public static bool Supports(DriverModelRecord model) =>
        model is { Vendor: DriverVendor.Hp, Architecture: MachineArchitecture.X64 };

    /// <summary>The component id of a platform's BIOS, as stored files name it.</summary>
    public static string ComponentId(string platformId) => $"firmware-{platformId}";

    /// <summary>Row key of a BIOS file in the files table.</summary>
    public static string FileKey(string platformId, string version) => DriverSyncService.FileKey(ComponentId(platformId), version);

    /// <summary>Where a BIOS file is stored.</summary>
    public static string BlobName(string platformId, string version, string fileName) => $"files/{DriverVendor.Hp}/{ComponentId(platformId)}/{version}/{fileName}";

    /// <summary>Partition of a platform's version history.</summary>
    public static string HistoryPartition(Guid modelId, string platformId) => $"{modelId:D}_{platformId}";

    /// <summary>Versions whose files devices may be given.</summary>
    public static IEnumerable<string> WantedVersions(DriverFirmwareRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.EffectiveAction == DriverFirmwareAction.Skip
            ? []
            : new[] { record.ReleasedVersion, record.PreProductionVersion }.OfType<string>().Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Merges a platform's catalog into its record. A newer version is what every location gets,
    /// except while a roll back holds an earlier one, or when the model updates its BIOS and
    /// newer versions go to pre-production first.
    /// </summary>
    public static (DriverFirmwareRecord Record, DriverFirmwareMergeChange Change, bool RollBackEnded) Merge(DriverFirmwareRecord? old, string platformId, IReadOnlyList<DriverFirmwareRelease> releases, DriverSyncSettingsRecord settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(settings);
        var latest = releases[0];
        var kept = releases.Take(KeptReleases).ToList();
        if (old is null)
        {
            return (new DriverFirmwareRecord { PlatformId = platformId, Latest = latest, Releases = kept, ReleasedVersion = latest.Version, AddedAt = now, UpdatedAt = now }, DriverFirmwareMergeChange.New, false);
        }

        var refreshed = old with { Latest = latest, Releases = kept };
        if (HpBiosCatalog.CompareVersions(latest.Version, old.Latest.Version) <= 0)
        {
            // HP can pull a version; one waiting in pre-production that is gone waits no more.
            return (refreshed.PreProductionVersion is { } pre && refreshed.Find(pre) is null ? refreshed with { PreProductionVersion = null, PreProductionSince = null } : refreshed, DriverFirmwareMergeChange.None, false);
        }

        refreshed = refreshed with { UpdatedAt = now };
        var rollBackEnded = false;
        if (old.RollBack is { Until: DriverRollBackUntil.NewerVersion } rollBack && latest.Version != rollBack.SkippedVersion && latest.Version != rollBack.Version)
        {
            refreshed = refreshed with { RollBack = null };
            rollBackEnded = true;
        }

        if (refreshed.RollBack is not null)
        {
            return (refreshed, DriverFirmwareMergeChange.Held, false);
        }

        if (old.EffectiveAction == DriverFirmwareAction.UpdateAtRestart && settings is { FirmwareNewVersions: DriverFirmwareNewVersions.PreProductionFirst, FirmwarePreProductionLocationId: not null })
        {
            // After a roll back ended, every location is back on the newest version it was given.
            return (refreshed with { ReleasedVersion = rollBackEnded ? old.RollBack!.SkippedVersion ?? old.ReleasedVersion : old.ReleasedVersion, PreProductionVersion = latest.Version, PreProductionSince = now }, DriverFirmwareMergeChange.PreProduction, rollBackEnded);
        }

        return (refreshed with { ReleasedVersion = latest.Version, PreProductionVersion = null, PreProductionSince = null }, DriverFirmwareMergeChange.Released, rollBackEnded);
    }

    /// <summary>Changes a platform's record under its ETag; <paramref name="change"/> returns null to leave it as it is.</summary>
    public static async Task<(DriverFirmwareRecord? Before, DriverFirmwareRecord? After)> UpdateAsync(DriverStore store, Guid modelId, string platformId, Func<DriverFirmwareRecord?, DriverFirmwareRecord?> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(change);
        var partition = modelId.ToString("D");
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var stored = await store.Firmware.GetAsync(partition, platformId, ct);
            if (change(stored?.Value) is not { } next)
            {
                return (stored?.Value, stored?.Value);
            }

            var saved = stored is null
                ? await store.Firmware.TryAddAsync(partition, platformId, next, ct)
                : await store.Firmware.TryUpdateAsync(partition, platformId, next, stored.ETag, ct);
            if (saved)
            {
                return (stored?.Value, next);
            }
        }

        throw new InvalidOperationException("The BIOS record kept changing while it was being saved.");
    }

    /// <summary>Starts a new history row for the version every location gets; the open row ends now.</summary>
    public static async Task RecordVersionAsync(DriverStore store, Guid modelId, DriverFirmwareRecord record, DriverVersionChange change, string? changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        var partition = HistoryPartition(modelId, record.PlatformId);
        var rows = await store.FirmwareVersions.ListPartitionAsync(partition, ct);
        var open = rows.Where(r => r.Value.To is null).ToList();
        if (open.Count == 1 && open[0].Value.Version == record.ReleasedVersion)
        {
            return;
        }

        foreach (var row in open)
        {
            await store.FirmwareVersions.UpsertAsync(partition, row.RowKey, row.Value with { To = now }, ct);
        }

        var release = record.Find(record.ReleasedVersion);
        var previous = rows.FirstOrDefault(r => r.Value.Version == record.ReleasedVersion)?.Value;
        await store.FirmwareVersions.UpsertAsync(partition, DriverTables.InvertedTicks(now), new DriverFirmwareVersionRecord
        {
            Version = record.ReleasedVersion,
            From = now,
            Change = change,
            ChangedBy = changedBy,
            FileName = release?.FileName ?? previous?.FileName ?? string.Empty,
            Sha256 = release?.Sha256 ?? previous?.Sha256 ?? string.Empty,
            Url = release?.Url ?? previous?.Url,
        }, ct);
    }

    /// <summary>
    /// Writes a file row for every version devices may be given that is not stored yet, and returns
    /// their downloads. A failed file is tried again when it failed before <paramref name="retryFailedBefore"/>.
    /// </summary>
    public static async Task<List<DriverWorkMessage>> EnsureFilesAsync(DriverStore store, Guid modelId, DriverFirmwareRecord record, DateTimeOffset? retryFailedBefore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        var downloads = new List<DriverWorkMessage>();
        var vendor = DriverVendor.Hp.ToString();
        foreach (var version in WantedVersions(record).Concat(record.RollBack is { } held && record.EffectiveAction != DriverFirmwareAction.Skip ? [held.Version] : []).Distinct(StringComparer.Ordinal))
        {
            if (await ReleaseOfAsync(store, modelId, record, version, ct) is not { } release)
            {
                continue;
            }

            var key = FileKey(record.PlatformId, version);
            var existing = (await store.Files.GetAsync(vendor, key, ct))?.Value;
            var retry = existing is { Status: DriverFileStatus.Failed } && retryFailedBefore is { } before && (existing.AttemptedAt ?? DateTimeOffset.MinValue) < before;
            if (existing is not null && !retry)
            {
                continue;
            }

            await store.Files.UpsertAsync(vendor, key, new DriverFileRecord
            {
                BlobName = BlobName(record.PlatformId, version, release.FileName),
                Sha256 = release.Sha256,
                Status = DriverFileStatus.Downloading,
                SourceUrl = release.Url,
                FileName = release.FileName,
                ComponentName = $"{ComponentName} ({record.PlatformId})",
                Version = version,
                Category = Category,
                AttemptedAt = existing?.AttemptedAt,
            }, ct);
            downloads.Add(new DriverWorkMessage { Kind = DriverWorkKind.DownloadFile, Vendor = DriverVendor.Hp, FileKey = key });
        }

        return downloads;
    }

    /// <summary>The catalog entry of a version, or its history row when HP no longer lists it.</summary>
    public static async Task<DriverFirmwareRelease?> ReleaseOfAsync(DriverStore store, Guid modelId, DriverFirmwareRecord record, string version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Find(version) is { } listed)
        {
            return listed;
        }

        var row = (await store.FirmwareVersions.ListPartitionAsync(HistoryPartition(modelId, record.PlatformId), ct))
            .Select(r => r.Value)
            .FirstOrDefault(v => v.Version == version && v.FileName.Length > 0 && v.Sha256.Length > 0);
        return row is null ? null : new DriverFirmwareRelease { Version = row.Version, FileName = row.FileName, Sha256 = row.Sha256, Url = row.Url ?? HpBiosCatalog.FileUrl(record.PlatformId, row.FileName) };
    }

    /// <summary>
    /// Stored BIOS files clean-up keeps: every version devices may be given, a held roll back, the
    /// <paramref name="keepPrevious"/> versions before the current one, and the version pinned to
    /// an active session.
    /// </summary>
    public static async Task<HashSet<string>> KeptBlobsAsync(DriverStore store, DeviceSessionRepository sessions, int keepPrevious, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (partition, row) in await store.Firmware.ListAllAsync(ct))
        {
            if (!Guid.TryParse(partition, out var modelId))
            {
                continue;
            }

            var history = (await store.FirmwareVersions.ListPartitionAsync(HistoryPartition(modelId, row.RowKey), ct)).Select(r => r.Value).ToList();
            var versions = WantedVersions(row.Value)
                .Concat(row.Value.RollBack is { } held ? [held.Version] : [])
                .Concat(history.Where(h => h.To is not null).OrderByDescending(h => h.From).Take(Math.Max(keepPrevious, 0)).Select(h => h.Version));
            foreach (var version in versions)
            {
                if ((row.Value.Find(version)?.FileName ?? history.FirstOrDefault(h => h.Version == version)?.FileName) is { Length: > 0 } fileName)
                {
                    kept.Add(BlobName(row.RowKey, version, fileName));
                }
            }
        }

        // Read from the file row, not the model: a session keeps its BIOS when the model is removed.
        await foreach (var session in sessions.QueryActiveAsync(ct))
        {
            if (session is { FirmwarePlatformId: { } platformId, FirmwareVersion: { } version }
                && (await store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(platformId, version), ct))?.Value is { } file)
            {
                kept.Add(file.BlobName);
            }
        }

        return kept;
    }

    /// <summary>BIOS versions waiting in pre-production, across every model.</summary>
    public static async Task<int> CountPreProductionAsync(DriverStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (await store.Firmware.ListAllAsync(ct)).Count(r => r.Row.Value.PreProductionVersion is not null);
    }

    /// <summary>Releases every BIOS waiting in pre-production; returns the platforms and versions released.</summary>
    public static async Task<IReadOnlyList<(Guid ModelId, string PlatformId, string Version)>> ReleaseAllPreProductionAsync(DriverStore store, string changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var released = new List<(Guid, string, string)>();
        foreach (var (partition, row) in await store.Firmware.ListAllAsync(ct))
        {
            if (row.Value.PreProductionVersion is null || !Guid.TryParse(partition, out var modelId))
            {
                continue;
            }

            var (_, after) = await UpdateAsync(store, modelId, row.RowKey, r => r?.PreProductionVersion is { } pre ? r with { ReleasedVersion = pre, PreProductionVersion = null, PreProductionSince = null } : null, ct);
            if (after is not null && after.ReleasedVersion != row.Value.ReleasedVersion)
            {
                await RecordVersionAsync(store, modelId, after, DriverVersionChange.Release, changedBy, now, ct);
                released.Add((modelId, row.RowKey, after.ReleasedVersion));
            }
        }

        return released;
    }

    /// <summary>Deletes a model's BIOS records and history.</summary>
    public static async Task DeleteModelAsync(DriverStore store, Guid modelId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var partition = modelId.ToString("D");
        foreach (var row in await store.Firmware.ListPartitionAsync(partition, ct))
        {
            var history = HistoryPartition(modelId, row.RowKey);
            await store.FirmwareVersions.DeleteManyAsync(history, (await store.FirmwareVersions.ListPartitionAsync(history, ct)).Select(r => r.RowKey), ct);
            await store.Firmware.DeleteAsync(partition, row.RowKey, ct: ct);
        }
    }

    /// <summary>The Firmware tab of a model, or null when the model does not exist.</summary>
    public async Task<DriverFirmwarePage?> GetPageAsync(Guid modelId, CancellationToken ct = default)
    {
        if ((await _models.GetAsync(modelId, ct))?.Value is not { } model)
        {
            return null;
        }

        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        var location = settings.FirmwarePreProductionLocationId is { } locationId ? await _locations.GetByIdAsync(locationId, ct) : null;
        if (!Supports(model))
        {
            return new DriverFirmwarePage { Supported = false, NewVersions = settings.FirmwareNewVersions, PreProductionLocationName = location?.Name };
        }

        var notes = await ReleaseNotesAsync(model, ct);
        var rows = new List<DriverFirmwareRow>();
        var preProduction = new List<DriverFirmwarePreProduction>();
        foreach (var stored in (await _store.Firmware.ListPartitionAsync(modelId.ToString("D"), ct)).OrderBy(r => r.RowKey, StringComparer.Ordinal))
        {
            var record = stored.Value;
            string? failure = null;
            var downloading = false;
            foreach (var version in WantedVersions(record))
            {
                var file = (await _store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(record.PlatformId, version), ct))?.Value;
                downloading |= file?.Status == DriverFileStatus.Downloading;
                failure ??= file?.Status == DriverFileStatus.Failed ? file.FailureReason ?? "The file could not be downloaded." : null;
            }

            var latestFile = (await _store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(record.PlatformId, record.Latest.Version), ct))?.Value;
            rows.Add(new DriverFirmwareRow
            {
                PlatformId = record.PlatformId,
                Name = ComponentName,
                Version = record.Latest.Version,
                LastUpdated = record.Latest.ReleasedAt,
                FileName = record.Latest.FileName,
                Critical = record.Latest.Critical,
                Action = record.EffectiveAction,
                Source = record.RollBack is not null ? DriverActionSource.RolledBack : record.Action is null ? DriverActionSource.Default : DriverActionSource.Override,
                ReleasedVersion = record.ReleasedVersion,
                RollBack = record.RollBack is { } held ? new DriverComponentRollBackInfo { Version = held.Version, Until = held.Until, SkippedVersion = held.SkippedVersion, ChangedBy = held.ChangedBy, ChangedAt = held.ChangedAt } : null,
                SizeBytes = latestFile?.Status == DriverFileStatus.Verified ? latestFile.Size : 0,
                DescriptionUrl = notes.FirstOrDefault(n => n.Version.StartsWith(record.Latest.Version, StringComparison.Ordinal)).Url,
                DownloadFailure = failure,
                Downloading = downloading,
            });
            if (record is { PreProductionVersion: { } pre } && record.EffectiveAction == DriverFirmwareAction.UpdateAtRestart)
            {
                preProduction.Add(new DriverFirmwarePreProduction { PlatformId = record.PlatformId, Version = pre, ReleasedAt = record.Find(pre)?.ReleasedAt, ReleasedVersion = record.ReleasedVersion, Since = record.PreProductionSince });
            }
        }

        return new DriverFirmwarePage { Supported = true, NewVersions = settings.FirmwareNewVersions, PreProductionLocationName = location?.Name, Rows = rows, PreProduction = preProduction };
    }

    /// <summary>Chooses a platform's action. Choosing Update at restart gives every location the newest version.</summary>
    public async Task<DriverResult> SetActionAsync(Guid modelId, string platformId, DriverFirmwareActionRequest? request, CancellationToken ct = default)
    {
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(request is null ? "The request body is not valid." : DriverHttpText.MissingActor);
        }

        if (!Enum.IsDefined(request.Action))
        {
            return DriverResult.BadRequest("Choose Update at restart or Skip.");
        }

        if ((await _models.GetAsync(modelId, ct))?.Value is not { } model || !Supports(model))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var now = _time.GetUtcNow();
        var platform = DriverTables.NormalizeKey(platformId);
        var (before, after) = await UpdateAsync(_store, modelId, platform, r => r is null || r.EffectiveAction == request.Action ? null : r with
        {
            Action = request.Action == DriverFirmwareAction.Skip ? null : request.Action,
            ActionChangedAt = now,
            ActionChangedBy = upn,
            ReleasedVersion = request.Action == DriverFirmwareAction.UpdateAtRestart ? r.RollBack?.Version ?? r.Latest.Version : r.ReleasedVersion,
            PreProductionVersion = null,
            PreProductionSince = null,
        }, ct);
        if (after is null)
        {
            return DriverResult.NotFound("This model has no BIOS for that platform.");
        }

        if (!ReferenceEquals(before, after))
        {
            await _store.LogAsync(now, upn, request.Actor.ObjectId, LogArea, Subject(model, platform), $"Action: {ActionLabel(before!.EffectiveAction)} to {ActionLabel(after.EffectiveAction)}", ct);
            await RecordVersionAsync(_store, modelId, after, DriverVersionChange.Release, upn, now, ct);
            await QueueDownloadsAsync(modelId, after, now, ct);
        }

        return DriverResult.Ok((await GetPageAsync(modelId, ct))!);
    }

    /// <summary>Gives the version in pre-production to every location.</summary>
    public async Task<DriverResult> ReleaseAsync(Guid modelId, string platformId, DriverActorRequest? request, CancellationToken ct = default)
    {
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if ((await _models.GetAsync(modelId, ct))?.Value is not { } model)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var now = _time.GetUtcNow();
        var platform = DriverTables.NormalizeKey(platformId);
        var (before, after) = await UpdateAsync(_store, modelId, platform, r => r?.PreProductionVersion is { } pre ? r with { ReleasedVersion = pre, PreProductionVersion = null, PreProductionSince = null } : null, ct);
        if (after is null || ReferenceEquals(before, after))
        {
            return DriverResult.Conflict("No BIOS of this model is in pre-production.");
        }

        await RecordVersionAsync(_store, modelId, after, DriverVersionChange.Release, upn, now, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, LogArea, Subject(model, platform), $"Released {after.ReleasedVersion} to every location", ct);
        return DriverResult.Ok((await GetPageAsync(modelId, ct))!);
    }

    /// <summary>The versions every location got, newest first, with whether each file is stored.</summary>
    public async Task<DriverResult> VersionsAsync(Guid modelId, string platformId, CancellationToken ct = default)
    {
        var platform = DriverTables.NormalizeKey(platformId);
        if ((await _store.Firmware.GetAsync(modelId.ToString("D"), platform, ct))?.Value is not { } record)
        {
            return DriverResult.NotFound("This model has no BIOS for that platform.");
        }

        var versions = new List<DriverComponentVersion>();
        foreach (var row in (await _store.FirmwareVersions.ListPartitionAsync(HistoryPartition(modelId, platform), ct)).Select(r => r.Value).OrderByDescending(v => v.From))
        {
            var file = (await _store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(platform, row.Version), ct))?.Value;
            versions.Add(new DriverComponentVersion
            {
                ComponentId = ComponentId(platform),
                Version = row.Version,
                From = row.From,
                To = row.To,
                Change = row.Change,
                ChangedBy = row.ChangedBy,
                Origin = row.Change == DriverVersionChange.RollBack ? DriverComponentOrigin.RolledBack : DriverComponentOrigin.Vendor,
                Status = file?.Status == DriverFileStatus.Verified ? DriverVersionStatus.Stored : record.Find(row.Version) is not null ? DriverVersionStatus.DownloadsOnRollBack : DriverVersionStatus.Unavailable,
                SizeBytes = file?.Size ?? 0,
            });
        }

        return DriverResult.Ok(versions);
    }

    /// <summary>Holds an earlier version for every location; it is downloaded again when not stored.</summary>
    public async Task<DriverResult> RollBackAsync(Guid modelId, string platformId, DriverComponentRollBackRequest? request, CancellationToken ct = default)
    {
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(request is null ? "The request body is not valid." : DriverHttpText.MissingActor);
        }

        if ((await _models.GetAsync(modelId, ct))?.Value is not { } model)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var now = _time.GetUtcNow();
        var platform = DriverTables.NormalizeKey(platformId);
        var current = (await _store.Firmware.GetAsync(modelId.ToString("D"), platform, ct))?.Value;
        if (current is null)
        {
            return DriverResult.NotFound("This model has no BIOS for that platform.");
        }

        if (string.IsNullOrWhiteSpace(request.Version) || request.Version == current.ReleasedVersion
            || await ReleaseOfAsync(_store, modelId, current, request.Version, ct) is null)
        {
            return DriverResult.BadRequest("Choose an earlier version from the version history.");
        }

        var (_, after) = await UpdateAsync(_store, modelId, platform, r => r is null ? null : r with
        {
            RollBack = new DriverRollBackRecord { Version = request.Version, Until = request.Until, SkippedVersion = r.RollBack?.SkippedVersion ?? r.ReleasedVersion, ChangedAt = now, ChangedBy = upn },
            ReleasedVersion = request.Version,
            PreProductionVersion = null,
            PreProductionSince = null,
        }, ct);
        await RecordVersionAsync(_store, modelId, after!, DriverVersionChange.RollBack, upn, now, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, LogArea, Subject(model, platform), $"Rolled back to {request.Version}", ct);
        await QueueDownloadsAsync(modelId, after!, now, ct);
        return DriverResult.Ok((await GetPageAsync(modelId, ct))!);
    }

    /// <summary>Ends a roll back: every location gets the newest version again.</summary>
    public async Task<DriverResult> UndoRollBackAsync(Guid modelId, string platformId, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if ((await _models.GetAsync(modelId, ct))?.Value is not { } model)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var now = _time.GetUtcNow();
        var platform = DriverTables.NormalizeKey(platformId);
        var (before, after) = await UpdateAsync(_store, modelId, platform, r => r?.RollBack is null ? null : r with { RollBack = null, ReleasedVersion = r.Latest.Version }, ct);
        if (after is null || ReferenceEquals(before, after))
        {
            return DriverResult.Conflict("The BIOS is not rolled back.");
        }

        await RecordVersionAsync(_store, modelId, after, DriverVersionChange.Release, upn, now, ct);
        await _store.LogAsync(now, upn, null, LogArea, Subject(model, platform), "Roll back undone", ct);
        await QueueDownloadsAsync(modelId, after, now, ct);
        return DriverResult.Ok((await GetPageAsync(modelId, ct))!);
    }

    /// <summary>
    /// The BIOS a registering device is updated to, or null: its Client must report its power
    /// source (older Clients cannot stage a BIOS), its model must be an active HP x64 model that
    /// updates the platform's BIOS, the file must be stored, and the device must report an older
    /// version (or one that cannot be read; the device compares again before it flashes).
    /// </summary>
    public async Task<DriverFirmwarePin?> PinAsync(DeviceRegistrationPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hardware = payload.Hardware;
        if (payload.SecurityPosture?.PowerSource is null || string.IsNullOrWhiteSpace(hardware?.MotherboardModel) || payload.Architecture == MachineArchitecture.Arm64)
        {
            return null;
        }

        var platform = DriverTables.NormalizeKey(hardware.MotherboardModel);
        if (await _models.GetOwnerAsync(DriverVendor.Hp, platform, ct) is not { } modelId
            || (await _models.GetAsync(modelId, ct))?.Value is not { State: DriverModelState.Active } model
            || !Supports(model)
            || (await _store.Firmware.GetAsync(modelId.ToString("D"), platform, ct))?.Value is not { } record
            || record.EffectiveAction != DriverFirmwareAction.UpdateAtRestart)
        {
            return null;
        }

        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        var target = record.PreProductionVersion is { } pre && payload.LocationId is { } locationId && locationId == settings.FirmwarePreProductionLocationId
            ? pre
            : record.ReleasedVersion;
        var installed = HpBiosCatalog.InstalledVersion(hardware.BiosVersion);
        if (installed is not null && HpBiosCatalog.CompareVersions(installed, target) >= 0)
        {
            return null;
        }

        var file = (await _store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(platform, target), ct))?.Value;
        return file?.Status == DriverFileStatus.Verified ? new DriverFirmwarePin(modelId, platform, target, installed) : null;
    }

    /// <summary>
    /// The pinned BIOS of a session with a read link, or null when its file is not stored. Read from
    /// the file row, so a running deployment keeps its BIOS when the model is removed.
    /// </summary>
    public async Task<SessionFirmwareDescriptor?> DescriptorAsync(DeviceSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not { FirmwarePlatformId: { } platform, FirmwareVersion: { } version }
            || (await _store.Files.GetAsync(DriverVendor.Hp.ToString(), FileKey(platform, version), ct))?.Value is not { Status: DriverFileStatus.Verified } file)
        {
            return null;
        }

        var (url, expiresAt) = await _storage.GetReadLinkAsync(file.BlobName, ct);
        return new SessionFirmwareDescriptor
        {
            PlatformId = platform,
            Version = version,
            InstalledVersion = session.FirmwareInstalledVersion,
            FileName = file.FileName ?? Path.GetFileName(file.BlobName),
            Sha256 = file.Sha256,
            SizeBytes = file.Size,
            DownloadUrl = url,
            DownloadUrlExpiresAt = expiresAt,
        };
    }

    /// <summary>Display label of an action.</summary>
    public static string ActionLabel(DriverFirmwareAction action) => action == DriverFirmwareAction.UpdateAtRestart ? "Update at restart" : "Skip";

    private static string Subject(DriverModelRecord model, string platform) => $"{model.DisplayName}: {ComponentName} ({platform})";

    private async Task QueueDownloadsAsync(Guid modelId, DriverFirmwareRecord record, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var download in await EnsureFilesAsync(_store, modelId, record, now, ct))
        {
            await _work.EnqueueAsync(download, TimeSpan.Zero, ct);
        }
    }

    // HP lists release notes on the BIOS SoftPaq of its driver catalog; matched by version.
    private async Task<List<(string Version, string Url)>> ReleaseNotesAsync(DriverModelRecord model, CancellationToken ct)
    {
        var notes = new List<(string, string)>();
        foreach (var release in model.Releases)
        {
            foreach (var row in await _store.Components.ListPartitionAsync(DriverTables.Pair(model.ModelId, release), ct))
            {
                if (row.Value is { Version: { } version, DescriptionUrl: { Length: > 0 } url } && row.Value.Category?.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase) == true)
                {
                    notes.Add((version.Trim(), url));
                }
            }
        }

        return notes;
    }
}

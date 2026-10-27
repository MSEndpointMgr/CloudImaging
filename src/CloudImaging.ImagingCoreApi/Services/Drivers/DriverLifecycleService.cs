using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// Storage, downloads, roll back and removal (FR-029, FR-030, FR-042 to FR-047, FR-078, FR-102):
/// what each model stores, what is being downloaded, rolling a set back, and removing models,
/// releases and withdrawn components. Removal takes effect for new assignments at once; files go
/// at the next clean-up, and sets pinned to running deployments stay until those end.
/// </summary>
public sealed class DriverLifecycleService
{
    /// <summary>How long a finished set stays on the Downloads tab.</summary>
    public static readonly TimeSpan FinishedWindow = TimeSpan.FromDays(7);

    private const int MaxConcurrencyRetries = 20;

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly DriverSetDocumentRepository _sets;
    private readonly DeviceSessionRepository _sessions;
    private readonly DriverSyncService _sync;
    private readonly DriverModelService _modelService;
    private readonly DriverPolicyService _policy;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="pointers">Set pointers.</param>
    /// <param name="sets">Set documents.</param>
    /// <param name="sessions">Sessions, for running deployments.</param>
    /// <param name="sync">Clean-up rules.</param>
    /// <param name="modelService">Model pages.</param>
    /// <param name="policy">Marks pairs dirty.</param>
    /// <param name="time">Clock.</param>
    public DriverLifecycleService(DriverStore store, DriverModelRepository models, DriverSetPointerRepository pointers, DriverSetDocumentRepository sets, DeviceSessionRepository sessions, DriverSyncService sync, DriverModelService modelService, DriverPolicyService policy, TimeProvider time)
    {
        _store = store;
        _models = models;
        _pointers = pointers;
        _sets = sets;
        _sessions = sessions;
        _sync = sync;
        _modelService = modelService;
        _policy = policy;
        _time = time;
    }

    /// <summary>Makes the previous set current again; 409 without a previous set.</summary>
    public async Task<DriverResult> RollBackSetAsync(Guid modelId, WindowsRelease release, DriverActor? actor, CancellationToken ct = default)
    {
        if (actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || !model.Releases.Contains(release))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _pointers.GetAsync(modelId, release, ct);
            if (row?.Value is not { PreviousVersion: { } previous, CurrentVersion: { } current } pointer
                || await _sets.GetAsync(modelId, release, previous, ct) is not { } document)
            {
                return DriverResult.Conflict("No previous driver set to roll back to.");
            }

            var now = _time.GetUtcNow();
            if (await _pointers.TryUpdateAsync(modelId, release, DriverSetBuilder.RollBack(pointer, document.Fingerprint, now), row.ETag, ct))
            {
                await _store.LogAsync(now, upn, actor.ObjectId, "RollBack", model.DisplayName, $"Driver set {current} to {previous} for {DriverModelService.ReleaseLabel(release)}", ct);
                return await _modelService.GetModelPageAsync(modelId, release, ct);
            }
        }

        return DriverResult.Conflict("The driver set changed while it was being rolled back. Try again.");
    }

    /// <summary>What removing a model, or one of its releases, deletes and frees.</summary>
    public async Task<DriverResult> RemovalImpactAsync(Guid modelId, WindowsRelease? release, CancellationToken ct = default)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || (release is { } r && !model.Releases.Contains(r)))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        IReadOnlyList<WindowsRelease> releases = release is { } only ? [only] : model.Releases;
        var inScope = (Guid id, WindowsRelease rel) => id == modelId && releases.Contains(rel);
        var refs = await ReferencesAsync(ct);
        var mine = refs.ByPair.Where(p => inScope(p.Key.ModelId, p.Key.Release)).SelectMany(p => p.Value).ToHashSet(StringComparer.Ordinal);
        var others = refs.ByPair.Where(p => !inScope(p.Key.ModelId, p.Key.Release)).SelectMany(p => p.Value).Concat(refs.Pinned).ToHashSet(StringComparer.Ordinal);
        var files = await FilesByBlobAsync(ct);

        var sets = 0;
        var overrides = 0;
        foreach (var rel in releases)
        {
            var pointer = (await _pointers.GetAsync(modelId, rel, ct))?.Value;
            sets += new[] { pointer?.CurrentVersion, pointer?.PreviousVersion, pointer?.PendingVersion }.Count(v => v is not null);
            overrides += (await _store.Overrides.ListPartitionAsync(DriverTables.Pair(modelId, rel), ct)).Count;
        }

        var running = 0;
        await foreach (var session in _sessions.QueryActiveAsync(ct))
        {
            if (session.DriverModelId == modelId && session.DriverRelease is { } pinned && releases.Contains(pinned) && session.DriverSetVersion is not null)
            {
                running++;
            }
        }

        return DriverResult.Ok(new DriverRemovalImpact
        {
            FreedBytes = mine.Where(b => !others.Contains(b) && files.ContainsKey(b)).Sum(b => files[b].Value.Size),
            Sets = sets,
            Overrides = overrides,
            RunningSessions = running,
        });
    }

    /// <summary>Removes a model: it matches no device from now on; rules and run history stay.</summary>
    public async Task<DriverResult> RemoveModelAsync(Guid modelId, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        // The model row and its keys go first, so no new assignment can match it while the rest is deleted.
        await _models.DeleteAsync(modelId, ct);
        await _models.ReleaseKeysAsync(model.Vendor, model.MatchKeys, modelId, ct);
        foreach (var release in model.Releases)
        {
            await DeletePairAsync(modelId, release, ct);
        }

        await _store.LogAsync(_time.GetUtcNow(), changedBy, null, "Model", model.DisplayName, "Removed", ct);
        return new DriverResult(HttpStatusCode.NoContent);
    }

    /// <summary>Removes one release of a model; 409 for its last release.</summary>
    public async Task<DriverResult> RemoveReleaseAsync(Guid modelId, WindowsRelease release, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _models.GetAsync(modelId, ct);
            if (row is null || !row.Value.Releases.Contains(release))
            {
                return DriverResult.NotFound("Driver model not found.");
            }

            if (row.Value.Releases.Count == 1)
            {
                return DriverResult.Conflict("Remove the model instead.");
            }

            var updated = row.Value with { Releases = row.Value.Releases.Where(r => r != release).ToList() };
            if (await _models.TryUpdateAsync(updated, row.ETag, ct))
            {
                await DeletePairAsync(modelId, release, ct);
                await _store.LogAsync(_time.GetUtcNow(), changedBy, null, "Release", updated.DisplayName, $"Removed {DriverModelService.ReleaseLabel(release)}", ct);
                return new DriverResult(HttpStatusCode.NoContent);
            }
        }

        return DriverResult.Conflict("The model changed while the release was being removed. Try again.");
    }

    /// <summary>Removes a component the vendor withdrew; 409 for any other component.</summary>
    public async Task<DriverResult> RemoveWithdrawnComponentAsync(Guid modelId, WindowsRelease release, string lineageKey, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || !model.Releases.Contains(release))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var pair = DriverTables.Pair(modelId, release);
        var component = (await _store.Components.GetAsync(pair, lineageKey, ct))?.Value;
        if (component is null)
        {
            return DriverResult.NotFound("Component not found.");
        }

        if (component.State != DriverComponentState.Withdrawn)
        {
            return DriverResult.Conflict("Only a component the vendor no longer publishes can be removed.");
        }

        var now = _time.GetUtcNow();
        await _store.Components.DeleteAsync(pair, lineageKey, ct: ct);
        await _store.Overrides.DeleteAsync(pair, lineageKey, ct: ct);
        await _store.LogAsync(now, changedBy, null, "Component", $"{model.DisplayName}: {component.Name}", $"Removed withdrawn component from {DriverModelService.ReleaseLabel(release)}", ct);
        await _policy.MarkDirtyAsync(model, release, now, ct);
        return new DriverResult(HttpStatusCode.NoContent);
    }

    /// <summary>The Storage tab: every file counted once, per vendor and per model.</summary>
    public async Task<DriverStorage> GetStorageAsync(CancellationToken ct = default)
    {
        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        var files = await FilesByBlobAsync(ct);
        var custom = await CustomPackagesAsync(ct);
        var refs = await ReferencesAsync(ct);
        var models = await _models.ListAsync(ct);
        var rows = new List<DriverStorageModelRow>();
        foreach (var model in models.OrderBy(m => m.Vendor).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var blobs = refs.ByPair.Where(p => p.Key.ModelId == model.ModelId).SelectMany(p => p.Value).Concat(refs.PreviousByModel.GetValueOrDefault(model.ModelId) ?? []).ToHashSet(StringComparer.Ordinal);
            var current = 0;
            var rollBack = 0;
            foreach (var release in model.Releases)
            {
                var pointer = (await _pointers.GetAsync(model.ModelId, release, ct))?.Value;
                current += pointer?.CurrentVersion is null ? 0 : 1;
                rollBack += pointer?.PreviousVersion is null ? 0 : 1;
            }

            rows.Add(new DriverStorageModelRow
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                Vendor = model.Vendor,
                Releases = model.Releases,
                SizeBytes = blobs.Where(files.ContainsKey).Sum(b => files[b].Value.Size) + blobs.Where(custom.ContainsKey).Sum(b => custom[b]),
                Sets = new DriverStorageSetCounts { Current = current, RollBack = rollBack },
            });
        }

        // Bytes kept only because of the setting: previous versions no set, session or other reference uses.
        var otherwise = refs.ByPair.Values.SelectMany(b => b).Concat(refs.Pinned).ToHashSet(StringComparer.Ordinal);
        var previousOnly = refs.PreviousByModel.Values.SelectMany(b => b).Where(b => !otherwise.Contains(b) && files.ContainsKey(b)).ToHashSet(StringComparer.Ordinal);
        var stored = files.Values.Where(f => f.Value.Status == DriverFileStatus.Verified).ToList();
        return new DriverStorage
        {
            TotalBytes = stored.Sum(f => f.Value.Size) + custom.Values.Sum(),
            Vendors = stored.GroupBy(f => f.Vendor).Select(g => new DriverStorageVendor { Vendor = g.Key, Bytes = g.Sum(f => f.Value.Size) }).OrderBy(v => v.Vendor).ToList(),
            Models = rows,
            PreviousVersionsToKeep = settings.PreviousVersionsToKeep,
            PreviousVersionsBytes = previousOnly.Sum(b => files[b].Value.Size),
        };
    }

    /// <summary>Changes how many previous versions are kept; downloads nothing, and a lower number frees files at the next clean-up.</summary>
    public async Task<DriverResult> SetStorageSettingsAsync(SetDriverStorageSettingsRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if (request.PreviousVersionsToKeep is < 0 or > 3)
        {
            return DriverResult.BadRequest("Choose a number from the list.");
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _store.GetSyncSettingsAsync(ct);
            var before = row.Value.PreviousVersionsToKeep;
            var next = row.Value with { PreviousVersionsToKeep = request.PreviousVersionsToKeep };
            var saved = row.ETag == default
                ? await _store.SyncSettings.TryAddAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, ct)
                : await _store.SyncSettings.TryUpdateAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, row.ETag, ct);
            if (!saved)
            {
                continue;
            }

            if (before != request.PreviousVersionsToKeep)
            {
                await _store.LogAsync(_time.GetUtcNow(), upn, request.Actor.ObjectId, "Storage", "Previous versions", $"{before} to {request.PreviousVersionsToKeep}", ct);
            }

            return DriverResult.Ok(await GetStorageAsync(ct));
        }

        return DriverResult.Conflict("The storage settings changed while they were being saved. Try again.");
    }

    /// <summary>One model on the Storage tab: per release its sets and every stored component.</summary>
    public async Task<DriverResult> GetStorageModelAsync(Guid modelId, CancellationToken ct = default)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var files = await FilesByBlobAsync(ct);
        var refs = await ReferencesAsync(ct);
        var releases = new List<DriverStorageRelease>();
        foreach (var release in model.Releases)
        {
            var pair = DriverTables.Pair(modelId, release);
            var pointer = (await _pointers.GetAsync(modelId, release, ct))?.Value;
            var components = new Dictionary<string, (DriverSetItem Item, List<string> Sets)>(StringComparer.Ordinal);
            foreach (var (version, label) in new[] { (pointer?.CurrentVersion, (string?)null), (pointer?.PreviousVersion, null), (pointer?.PendingVersion, "next") })
            {
                if (version is not { } v || await _sets.GetAsync(modelId, release, v, ct) is not { } document)
                {
                    continue;
                }

                foreach (var item in document.Items)
                {
                    var entry = components.TryGetValue(item.BlobName, out var known) ? known : (item, new List<string>());
                    entry.Item2.Add(label ?? v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    components[item.BlobName] = entry;
                }
            }

            // Kept previous versions that no set lists are shown as "previous".
            foreach (var version in await PreviousVersionRowsAsync(model, release, ct))
            {
                if (!components.ContainsKey(version.Blob))
                {
                    components[version.Blob] = (version.Item, ["previous"]);
                }
            }

            var current = pointer?.CurrentVersion is { } c ? await _sets.GetAsync(modelId, release, c, ct) : null;
            releases.Add(new DriverStorageRelease
            {
                Release = release,
                Current = pointer?.CurrentVersion is { } cv ? new DriverStorageSetRef { Version = cv, BuiltAt = await BuiltAtAsync(pair, cv, ct) ?? pointer.BuiltAt } : null,
                RollBack = pointer?.PreviousVersion is { } pv ? new DriverStorageSetRef { Version = pv, BuiltAt = await BuiltAtAsync(pair, pv, ct) } : null,
                ComponentCount = current?.Items.Count ?? 0,
                Bytes = current?.Items.Sum(i => i.Size) ?? 0,
                Components = components
                    .OrderBy(c => c.Value.Item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c =>
                    {
                        var file = files.TryGetValue(c.Key, out var found) ? found.Value : null;
                        var isCustom = c.Value.Item.Origin == DriverComponentOrigin.Custom;
                        var shared = refs.ByPair.Where(p => p.Key.ModelId != modelId && p.Value.Contains(c.Key)).Select(p => p.Key.ModelId).Distinct().Count();
                        return new DriverStorageComponent
                        {
                            LineageKey = c.Value.Item.LineageKey,
                            ComponentId = c.Value.Item.ComponentId,
                            Name = c.Value.Item.Name,
                            Version = c.Value.Item.Version,
                            SizeBytes = c.Value.Item.Size,
                            Sets = c.Value.Sets,
                            SharedWithModels = shared,
                            // A custom package is stored when it is uploaded; it has no vendor download.
                            Status = isCustom ? "Stored" : file?.Status switch
                            {
                                DriverFileStatus.Verified => "Stored",
                                DriverFileStatus.Failed => "Failed",
                                _ => "Downloading",
                            },
                            Reason = file?.Status == DriverFileStatus.Failed ? file.FailureReason : null,
                            DownloadedAt = file?.VerifiedAt,
                        };
                    })
                    .ToList(),
            });
        }

        return DriverResult.Ok(new DriverStorageModel { ModelId = modelId, Releases = releases });
    }

    /// <summary>The Downloads tab: sets being prepared, and builds that became ready in the last 7 days.</summary>
    public async Task<DriverDownloads> GetDownloadsAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var models = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId);
        var inProgress = new List<DriverDownloadInProgress>();
        foreach (var (pair, row) in await _pointers.ListAllAsync(ct))
        {
            if (row.Value.PendingVersion is not { } pending
                || !DriverSyncService.TryParsePair(pair, out var modelId, out var release)
                || !models.TryGetValue(modelId, out var model)
                || await _sets.GetAsync(modelId, release, pending, ct) is not { } document)
            {
                continue;
            }

            var statuses = new List<(DriverSetItem Item, DriverFileRecord? File)>();
            foreach (var item in document.Items)
            {
                statuses.Add((item, item.Origin == DriverComponentOrigin.Custom
                    ? new DriverFileRecord { BlobName = item.BlobName, Sha256 = item.Sha256, Size = item.Size, Status = DriverFileStatus.Verified }
                    : (await _store.Files.GetAsync(model.Vendor.ToString(), item.FileKey, ct))?.Value));
            }

            var failed = statuses.Where(s => s.File?.Status == DriverFileStatus.Failed).ToList();
            var downloading = statuses.Any(s => s.File?.Status == DriverFileStatus.Downloading);
            inProgress.Add(new DriverDownloadInProgress
            {
                ModelId = modelId,
                ModelName = model.DisplayName,
                Release = release,
                SetVersion = pending,
                Components = statuses.Count,
                ComponentsDone = statuses.Count(s => s.File?.Status is DriverFileStatus.Verified or DriverFileStatus.Failed),
                Bytes = statuses.Sum(s => s.Item.Size),
                BytesDone = statuses.Where(s => s.File?.Status == DriverFileStatus.Verified).Sum(s => s.Item.Size),
                Status = failed.Count > 0 ? "Failed" : downloading ? "Downloading" : "Waiting",
                Failed = failed.Count,
                Reasons = failed.Select(f => $"{f.Item.Name}: {f.File!.FailureReason}").ToList(),
                StartedAt = await BuiltAtAsync(pair, pending, ct) ?? document.BuiltAt,
            });
        }

        var finished = new List<DriverDownloadFinished>();
        foreach (var (pair, build) in await _store.SetBuilds.ListAllAsync(ct))
        {
            if (build.Value.ReadyAt is not { } ready || now - ready > FinishedWindow
                || !DriverSyncService.TryParsePair(pair, out var modelId, out var release)
                || !models.TryGetValue(modelId, out var model)
                || !int.TryParse(build.RowKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
            {
                continue;
            }

            finished.Add(new DriverDownloadFinished
            {
                ModelId = modelId,
                ModelName = model.DisplayName,
                Release = release,
                SetVersion = version,
                NewComponents = build.Value.NewComponents,
                ReusedComponents = build.Value.ReusedComponents,
                SizeBytes = build.Value.SizeBytes,
                ReadyAt = ready,
            });
        }

        return new DriverDownloads
        {
            InProgress = inProgress.OrderBy(d => d.StartedAt).ToList(),
            Finished = finished.OrderByDescending(d => d.ReadyAt).ToList(),
        };
    }

    // Everything a removed pair leaves behind, except set documents pinned to running deployments,
    // which clean-up keeps until those end. Version history and set builds go with the pair.
    private async Task DeletePairAsync(Guid modelId, WindowsRelease release, CancellationToken ct)
    {
        var pair = DriverTables.Pair(modelId, release);
        await _pointers.DeleteAsync(modelId, release, ct);
        await _store.Overrides.DeleteManyAsync(pair, (await _store.Overrides.ListPartitionAsync(pair, ct)).Select(r => r.RowKey), ct);
        await _store.Components.DeleteManyAsync(pair, (await _store.Components.ListPartitionAsync(pair, ct)).Select(r => r.RowKey), ct);
        await _store.ComponentVersions.DeleteManyAsync(pair, (await _store.ComponentVersions.ListPartitionAsync(pair, ct)).Select(r => r.RowKey), ct);
        await _store.RollBacks.DeleteManyAsync(pair, (await _store.RollBacks.ListPartitionAsync(pair, ct)).Select(r => r.RowKey), ct);
        await _store.SetBuilds.DeleteManyAsync(pair, (await _store.SetBuilds.ListPartitionAsync(pair, ct)).Select(r => r.RowKey), ct);
        var pinned = await _sync.KeptSetsAsync(ct);
        foreach (var document in (await _sets.ListAsync(ct)).Where(d => d.ModelId == modelId && d.Release == release && !pinned.Contains(d)))
        {
            await _sets.DeleteAsync(document.ModelId, document.Release, document.Version, ct);
        }
    }

    private async Task<DateTimeOffset?> BuiltAtAsync(string pair, int version, CancellationToken ct) =>
        (await _store.SetBuilds.GetAsync(pair, version.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), ct))?.Value.BuiltAt;

    // Custom packages by blob name with their size; they have no vendor file record.
    private async Task<Dictionary<string, long>> CustomPackagesAsync(CancellationToken ct) =>
        (await _store.CustomComponents.ListPartitionAsync(DriverStore.CustomPartition, ct))
            .GroupBy(r => r.Value.BlobName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value.Size, StringComparer.Ordinal);

    private async Task<Dictionary<string, (DriverVendor Vendor, DriverFileRecord Value)>> FilesByBlobAsync(CancellationToken ct)
    {
        var files = new Dictionary<string, (DriverVendor, DriverFileRecord)>(StringComparer.Ordinal);
        foreach (var (vendor, row) in await _store.Files.ListAllAsync(ct))
        {
            if (Enum.TryParse<DriverVendor>(vendor, out var parsed))
            {
                files[row.Value.BlobName] = (parsed, row.Value);
            }
        }

        return files;
    }

    private async Task<List<(string Blob, DriverSetItem Item)>> PreviousVersionRowsAsync(DriverModelRecord model, WindowsRelease release, CancellationToken ct)
    {
        var keep = (await _store.GetSyncSettingsAsync(ct)).Value.PreviousVersionsToKeep;
        if (keep <= 0)
        {
            return [];
        }

        var rows = new List<(string, DriverSetItem)>();
        var pair = DriverTables.Pair(model.ModelId, release);
        foreach (var lineage in (await _store.ComponentVersions.ListPartitionAsync(pair, ct)).Select(r => r.Value).GroupBy(v => v.LineageKey, StringComparer.Ordinal))
        {
            foreach (var version in lineage.Where(v => v.LastSet is not null && v.Origin != DriverComponentOrigin.Custom).OrderByDescending(v => v.From).Take(keep))
            {
                var fileName = version.FileName ?? $"{version.ComponentId}.exe";
                var blob = $"files/{model.Vendor}/{version.ComponentId}/{version.Version}/{fileName}";
                var name = (await _store.Components.GetAsync(pair, version.LineageKey, ct))?.Value.Name ?? version.ComponentId;
                rows.Add((blob, new DriverSetItem
                {
                    LineageKey = version.LineageKey,
                    ComponentId = version.ComponentId,
                    Name = name,
                    Version = version.Version,
                    FileKey = DriverSyncService.FileKey(version.ComponentId, version.Version),
                    BlobName = blob,
                    Sha256 = version.Sha256 ?? string.Empty,
                    Size = version.Size,
                    FileName = fileName,
                }));
            }
        }

        return rows;
    }

    // Which files each pair's kept sets list, the files of sets pinned to running deployments, and
    // the kept previous versions per model.
    private async Task<(Dictionary<(Guid ModelId, WindowsRelease Release), HashSet<string>> ByPair, HashSet<string> Pinned, Dictionary<Guid, HashSet<string>> PreviousByModel)> ReferencesAsync(CancellationToken ct)
    {
        var byPair = new Dictionary<(Guid, WindowsRelease), HashSet<string>>();
        foreach (var (pair, row) in await _pointers.ListAllAsync(ct))
        {
            if (!DriverSyncService.TryParsePair(pair, out var modelId, out var release))
            {
                continue;
            }

            var blobs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var version in new[] { row.Value.CurrentVersion, row.Value.PreviousVersion, row.Value.PendingVersion })
            {
                if (version is { } v && await _sets.GetAsync(modelId, release, v, ct) is { } document)
                {
                    blobs.UnionWith(document.Items.Select(i => i.BlobName));
                }
            }

            byPair[(modelId, release)] = blobs;
        }

        var pinned = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var session in _sessions.QueryActiveAsync(ct))
        {
            if (session is { DriverModelId: { } modelId, DriverRelease: { } release, DriverSetVersion: { } version }
                && await _sets.GetAsync(modelId, release, version, ct) is { } document)
            {
                pinned.UnionWith(document.Items.Select(i => i.BlobName));
            }
        }

        var previous = new Dictionary<Guid, HashSet<string>>();
        var keep = (await _store.GetSyncSettingsAsync(ct)).Value.PreviousVersionsToKeep;
        foreach (var (pair, blobs) in await _sync.KeptPreviousVersionsAsync(keep, ct))
        {
            if (DriverSyncService.TryParsePair(pair, out var modelId, out _))
            {
                previous.TryAdd(modelId, new HashSet<string>(StringComparer.Ordinal));
                previous[modelId].UnionWith(blobs);
            }
        }

        return (byPair, pinned, previous);
    }
}

using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Result of a driver endpoint: a body, or a problem with its detail and extra fields.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="Body">Response body for a success.</param>
/// <param name="Detail">Problem detail.</param>
/// <param name="Extra">Extra problem fields (<c>availableAt</c>, <c>catalogUrl</c>, <c>errors</c>).</param>
public sealed record DriverResult(HttpStatusCode Status, object? Body = null, string? Detail = null, IReadOnlyDictionary<string, object?>? Extra = null)
{
    /// <summary>200 with a body.</summary>
    public static DriverResult Ok(object body) => new(HttpStatusCode.OK, body);

    /// <summary>400.</summary>
    public static DriverResult BadRequest(string detail) => new(HttpStatusCode.BadRequest, Detail: detail);

    /// <summary>404.</summary>
    public static DriverResult NotFound(string detail) => new(HttpStatusCode.NotFound, Detail: detail);

    /// <summary>409.</summary>
    public static DriverResult Conflict(string detail, IReadOnlyDictionary<string, object?>? extra = null) => new(HttpStatusCode.Conflict, Detail: detail, Extra: extra);
}

/// <summary>One component of a model and release with its resolved action.</summary>
/// <param name="LineageKey">Lineage key.</param>
/// <param name="Component">Component row.</param>
/// <param name="Decision">Resolved action and its source.</param>
public sealed record DriverResolvedComponent(string LineageKey, DriverComponentRecord Component, DriverPolicyDecision Decision);

/// <summary>
/// Onboarding and the model pages (FR-004 to FR-010, FR-064 to FR-066): the vendor model list,
/// preview, drafts, Complete onboarding, adding a release, the Models tab and the model page.
/// </summary>
public sealed class DriverModelService
{
    /// <summary>At most this many models are previewed or onboarded at once.</summary>
    public const int MaxModelsPerRequest = 20;

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly DriverSyncRunRepository _runs;
    private readonly DriverSetDocumentRepository _sets;
    private readonly DriverCatalogService _catalogs;
    private readonly DriverSyncService _sync;
    private readonly IDriverWorkSender _work;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="pointers">Set pointers.</param>
    /// <param name="runs">Runs.</param>
    /// <param name="sets">Set documents.</param>
    /// <param name="catalogs">Vendor catalogs.</param>
    /// <param name="sync">Sync engine (component merge).</param>
    /// <param name="work">Queue.</param>
    /// <param name="time">Clock.</param>
    public DriverModelService(DriverStore store, DriverModelRepository models, DriverSetPointerRepository pointers, DriverSyncRunRepository runs, DriverSetDocumentRepository sets, DriverCatalogService catalogs, DriverSyncService sync, IDriverWorkSender work, TimeProvider time)
    {
        _store = store;
        _models = models;
        _pointers = pointers;
        _runs = runs;
        _sets = sets;
        _catalogs = catalogs;
        _sync = sync;
        _work = work;
        _time = time;
    }

    /// <summary>Every vendor with its Sync switch and onboarded model count; unsupported vendors are listed but never onboardable.</summary>
    public async Task<IReadOnlyList<DriverVendorRow>> ListVendorsAsync(CancellationToken ct = default)
    {
        var models = await _models.ListAsync(ct);
        var rows = new List<DriverVendorRow>();
        foreach (var vendor in Enum.GetValues<DriverVendor>())
        {
            var adapter = _catalogs.Adapter(vendor);
            rows.Add(new DriverVendorRow
            {
                Vendor = vendor,
                Supported = adapter is not null,
                SyncEnabled = adapter is not null && await _store.IsSyncEnabledAsync(vendor, ct),
                OnboardedModels = models.Count(m => m.Vendor == vendor),
                MatchKeyType = adapter?.MatchKeyType,
            });
        }

        return rows;
    }

    /// <summary>Turns a vendor's Sync on or off; a run already reading its catalogs finishes.</summary>
    public async Task<DriverResult> SetVendorAsync(DriverVendor vendor, SetDriverVendorRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if (_catalogs.Adapter(vendor) is null)
        {
            return DriverResult.BadRequest($"{DriverSyncService.VendorName(vendor)} is not supported yet.");
        }

        var now = _time.GetUtcNow();
        var before = await _store.IsSyncEnabledAsync(vendor, ct);
        await _store.VendorSettings.UpsertAsync(DriverStore.VendorPartition, vendor.ToString(), new DriverVendorSettingRecord { SyncEnabled = request.SyncEnabled, ChangedAt = now, ChangedBy = upn }, ct);
        if (before != request.SyncEnabled)
        {
            await _store.LogAsync(now, upn, request.Actor.ObjectId, "Vendor", DriverSyncService.VendorName(vendor), request.SyncEnabled ? "Sync turned on" : "Sync turned off", ct);
        }

        return DriverResult.Ok((await ListVendorsAsync(ct)).Single(v => v.Vendor == vendor));
    }

    /// <summary>Label of a Windows release, as the portal shows it.</summary>
    public static string ReleaseLabel(WindowsRelease release) => release switch
    {
        WindowsRelease.Windows10Version22H2 => "Windows 10, version 22H2",
        WindowsRelease.Windows11Version23H2 => "Windows 11, version 23H2",
        WindowsRelease.Windows11Version24H2 => "Windows 11, version 24H2",
        WindowsRelease.Windows11Version25H2 => "Windows 11, version 25H2",
        _ => release.ToString(),
    };

    /// <summary>Reads a release from its wire value (<c>Windows11_24H2</c>).</summary>
    public static WindowsRelease? ParseRelease(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WindowsRelease>(JsonSerializer.Serialize(value), DriverJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The vendor's model list. A list that is missing or older than 7 days is read first,
    /// whatever the vendor's Sync switch; this is not a run and moves no limit.
    /// </summary>
    public async Task<DriverResult> GetCatalogModelsAsync(DriverVendor vendor, string? search, CancellationToken ct = default)
    {
        if (_catalogs.Adapter(vendor) is not { } adapter)
        {
            return DriverResult.BadRequest($"{DriverSyncService.VendorName(vendor)} is not supported.");
        }

        var now = _time.GetUtcNow();
        var list = (await _store.CatalogLists.GetAsync(DriverStore.ListPartition, vendor.ToString(), ct))?.Value;
        string? warning = null;
        if (list?.FetchedAt is not { } fetchedAt || now - fetchedAt >= DriverCatalogService.ModelListFreshness)
        {
            var refresh = await _catalogs.RefreshModelListAsync(adapter, ct);
            if (refresh.Failure is not null)
            {
                if (list?.FetchedAt is not { } older)
                {
                    return new DriverResult(
                        HttpStatusCode.ServiceUnavailable,
                        Detail: $"{DriverCatalogService.CannotReach(adapter.ModelListUrl)[..^1]}. Make sure Imaging Core can reach it, then try again.",
                        Extra: new Dictionary<string, object?> { ["catalogUrl"] = adapter.ModelListUrl });
                }

                warning = $"Showing the model list from {older.UtcDateTime:yyyy-MM-dd HH:mm} UTC. {DriverCatalogService.CannotReach(adapter.ModelListUrl)}";
            }

            list = (await _store.CatalogLists.GetAsync(DriverStore.ListPartition, vendor.ToString(), ct))?.Value;
        }

        var onboarded = (await _models.ListAsync(ct)).Where(m => m.Vendor == vendor).ToDictionary(m => m.CatalogModelKey, m => m.ModelId, StringComparer.Ordinal);
        var models = (await _store.CatalogModels.ListPartitionAsync(vendor.ToString(), ct))
            .Where(row => Matches(row.Value, search))
            .OrderBy(row => row.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => new DriverCatalogModel
            {
                CatalogModelKey = row.RowKey,
                Vendor = vendor,
                DisplayName = row.Value.Name,
                MatchKeys = row.Value.MatchKeys,
                Architecture = row.Value.Architecture,
                Releases = row.Value.Releases,
                OnboardedModelId = onboarded.TryGetValue(row.RowKey, out var id) ? id : null,
            })
            .ToList();
        return DriverResult.Ok(new DriverCatalogModelList
        {
            FetchedAt = list?.FetchedAt,
            CatalogUrl = adapter.ModelListUrl,
            Stale = warning is not null,
            Warning = warning,
            Models = models,
        });
    }

    /// <summary>Counts per action and download size per model, from component catalogs cached for 24 hours.</summary>
    public async Task<DriverResult> PreviewAsync(DriverOnboardRequest request, CancellationToken ct = default)
    {
        if (ValidateSelection(request) is { } invalid)
        {
            return invalid;
        }

        var adapter = _catalogs.Adapter(request.Vendor)!;
        var rules = await RulesAsync(ct);
        var previews = new List<DriverModelPreview>();
        var errors = new List<DriverPreviewError>();
        foreach (var key in request.CatalogModelKeys.Distinct(StringComparer.Ordinal))
        {
            var catalogModel = (await _store.CatalogModels.GetAsync(request.Vendor.ToString(), key, ct))?.Value;
            if (catalogModel is null)
            {
                errors.Add(new DriverPreviewError { CatalogModelKey = key, Detail = "The model is not in the vendor's model list." });
                continue;
            }

            DriverModelPreview? best = null;
            string? failure = null;
            foreach (var release in request.Releases.Where(catalogModel.Releases.Contains))
            {
                var (components, error) = await _catalogs.ReadComponentsAsync(adapter, catalogModel, release, DriverCatalogService.PreviewCacheAge, ct);
                if (components is null)
                {
                    failure = error;
                    break;
                }

                var decisions = components.Select(c => (c.Component, Decision: Resolve(request.Vendor, c.Component, null, rules))).ToList();
                var preview = new DriverModelPreview
                {
                    CatalogModelKey = key,
                    DisplayName = catalogModel.Name,
                    Offline = decisions.Count(d => d.Decision.Action == DriverAction.Offline),
                    OfflineAndSpecialize = decisions.Count(d => d.Decision.Action == DriverAction.OfflineAndSpecialize),
                    Specialize = decisions.Count(d => d.Decision.Action == DriverAction.Specialize),
                    Skip = decisions.Count(d => d.Decision.Action == DriverAction.Skip),
                    SizeBytes = decisions.Where(d => d.Decision.Action != DriverAction.Skip).Sum(d => d.Component.Size),
                };
                if (best is null || preview.SizeBytes > best.SizeBytes)
                {
                    best = preview;
                }
            }

            if (failure is not null)
            {
                errors.Add(new DriverPreviewError { CatalogModelKey = key, Detail = failure });
            }
            else if (best is null)
            {
                errors.Add(new DriverPreviewError { CatalogModelKey = key, Detail = $"{catalogModel.Name} has no drivers for the chosen Windows releases." });
            }
            else
            {
                previews.Add(best);
            }
        }

        return DriverResult.Ok(new DriverPreviewResult { Models = previews, Errors = errors });
    }

    /// <summary>
    /// Creates each model as a draft with its components resolved. Every match key is reserved
    /// first; when one belongs to another model nothing is created. Nothing is downloaded.
    /// </summary>
    public async Task<DriverResult> OnboardAsync(DriverOnboardRequest request, CancellationToken ct = default)
    {
        if (ValidateSelection(request) is { } invalid)
        {
            return invalid;
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var adapter = _catalogs.Adapter(request.Vendor)!;
        var keys = request.CatalogModelKeys.Distinct(StringComparer.Ordinal).ToList();
        var plans = new List<(Guid ModelId, string Key, DriverCatalogModelRecord Catalog, List<WindowsRelease> Releases)>();
        foreach (var key in keys)
        {
            var catalogModel = (await _store.CatalogModels.GetAsync(request.Vendor.ToString(), key, ct))?.Value;
            if (catalogModel is null)
            {
                return DriverResult.BadRequest($"The model {key} is not in the vendor's model list.");
            }

            var releases = request.Releases.Distinct().Where(catalogModel.Releases.Contains).ToList();
            if (releases.Count == 0)
            {
                return DriverResult.BadRequest($"{catalogModel.Name} has no drivers for the chosen Windows releases.");
            }

            plans.Add((Guid.NewGuid(), key, catalogModel, releases));
        }

        // Reserve every key before creating anything, so a conflict leaves no partial onboarding.
        var reserved = new List<(Guid ModelId, IReadOnlyList<string> Keys)>();
        foreach (var plan in plans)
        {
            if (await _models.TryReserveKeysAsync(request.Vendor, plan.Catalog.MatchKeys, plan.ModelId, ct) is { } conflict)
            {
                foreach (var (modelId, matchKeys) in reserved)
                {
                    await _models.ReleaseKeysAsync(request.Vendor, matchKeys, modelId, ct);
                }

                var owner = (await _models.GetAsync(conflict.Owner, ct))?.Value.DisplayName ?? "another model";
                return DriverResult.Conflict($"{conflict.Key} already belongs to {owner}.", new Dictionary<string, object?> { ["matchKey"] = conflict.Key, ["modelId"] = conflict.Owner });
            }

            reserved.Add((plan.ModelId, plan.Catalog.MatchKeys));
        }

        var now = _time.GetUtcNow();
        foreach (var plan in plans)
        {
            var model = new DriverModelRecord
            {
                ModelId = plan.ModelId,
                Vendor = request.Vendor,
                Architecture = plan.Catalog.Architecture,
                DisplayName = plan.Catalog.Name.Length > 100 ? plan.Catalog.Name[..100] : plan.Catalog.Name,
                CatalogModelKey = plan.Key,
                MatchKeys = plan.Catalog.MatchKeys.Select(DriverTables.NormalizeKey).ToList(),
                Releases = plan.Releases,
                State = DriverModelState.Draft,
                OnboardedAt = now,
                OnboardedBy = upn,
            };
            await _models.TryAddAsync(model, ct);
            foreach (var release in plan.Releases)
            {
                var (components, failure) = await _catalogs.ReadComponentsAsync(adapter, plan.Catalog, release, DriverCatalogService.PreviewCacheAge, ct);
                if (components is not null)
                {
                    await _sync.MergeComponentsAsync(model, release, components, false, ct);
                }
                else
                {
                    // The draft keeps the release; Complete onboarding reads the catalog again.
                    await _store.LogAsync(now, upn, request.Actor.ObjectId, "Model", model.DisplayName, $"Components for {ReleaseLabel(release)} could not be read: {failure}", ct);
                }
            }

            await _store.LogAsync(now, upn, request.Actor.ObjectId, "Model", model.DisplayName, $"Onboarded as a draft for {string.Join(", ", plan.Releases.Select(ReleaseLabel))}", ct);
        }

        return new DriverResult(HttpStatusCode.Created, new DriverOnboardResult { ModelIds = plans.Select(p => p.ModelId).ToList() });
    }

    /// <summary>Makes a draft active: the vendor's sync is turned on and downloads start at once.</summary>
    public async Task<DriverResult> CompleteAsync(Guid modelId, DriverActor? actor, CancellationToken ct = default)
    {
        if (actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var row = await _models.GetAsync(modelId, ct);
        if (row is null)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        if (row.Value.State != DriverModelState.Draft)
        {
            return DriverResult.Conflict("Onboarding of this model is already complete.");
        }

        var now = _time.GetUtcNow();
        var model = row.Value with { State = DriverModelState.Active, CompletedAt = now, CompletedBy = upn };
        if (!await _models.TryUpdateAsync(model, row.ETag, ct))
        {
            return DriverResult.Conflict("The model changed while onboarding was being completed. Try again.");
        }

        if (!await _store.IsSyncEnabledAsync(model.Vendor, ct))
        {
            await _store.VendorSettings.UpsertAsync(DriverStore.VendorPartition, model.Vendor.ToString(), new DriverVendorSettingRecord { SyncEnabled = true, ChangedAt = now, ChangedBy = upn }, ct);
            await _store.LogAsync(now, upn, actor.ObjectId, "Vendor", DriverSyncService.VendorName(model.Vendor), "Sync turned on by onboarding", ct);
        }

        foreach (var release in model.Releases)
        {
            await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.ResolveModel, ModelId = modelId, Release = release }, TimeSpan.Zero, ct);
        }

        await _store.LogAsync(now, upn, actor.ObjectId, "Model", model.DisplayName, "Onboarding completed", ct);
        return await GetModelPageAsync(modelId, null, ct);
    }

    /// <summary>Adds a Windows release the catalog lists; its components are resolved at once.</summary>
    public async Task<DriverResult> AddReleaseAsync(Guid modelId, DriverAddReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var row = await _models.GetAsync(modelId, ct);
        if (row is null)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var model = row.Value;
        if (model.Releases.Contains(request.Release))
        {
            return DriverResult.BadRequest($"{model.DisplayName} already has {ReleaseLabel(request.Release)}.");
        }

        var catalogModel = (await _store.CatalogModels.GetAsync(model.Vendor.ToString(), model.CatalogModelKey, ct))?.Value;
        if (catalogModel is null || !catalogModel.Releases.Contains(request.Release))
        {
            return DriverResult.BadRequest($"{DriverSyncService.VendorName(model.Vendor)} publishes no drivers for {model.DisplayName} on {ReleaseLabel(request.Release)}.");
        }

        var (components, failure) = await _catalogs.ReadComponentsAsync(_catalogs.Adapter(model.Vendor)!, catalogModel, request.Release, DriverCatalogService.PreviewCacheAge, ct);
        if (components is null)
        {
            return new DriverResult(HttpStatusCode.BadGateway, Detail: failure);
        }

        var updated = model with { Releases = [.. model.Releases, request.Release] };
        if (!await _models.TryUpdateAsync(updated, row.ETag, ct))
        {
            return DriverResult.Conflict("The model changed while the release was being added. Try again.");
        }

        await _sync.MergeComponentsAsync(updated, request.Release, components, false, ct);
        if (updated.State == DriverModelState.Active)
        {
            var pointer = await _pointers.GetOrCreateAsync(modelId, request.Release, ct);
            await _pointers.TryUpdateAsync(modelId, request.Release, pointer.Value with { Dirty = true }, pointer.ETag, ct);
            await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = modelId, Release = request.Release }, TimeSpan.Zero, ct);
        }

        await _store.LogAsync(_time.GetUtcNow(), upn, request.Actor.ObjectId, "Release", updated.DisplayName, $"Added {ReleaseLabel(request.Release)}", ct);
        return await GetModelPageAsync(modelId, request.Release, ct);
    }

    /// <summary>The Models tab.</summary>
    public async Task<IReadOnlyList<DriverModelRow>> ListModelsAsync(CancellationToken ct = default)
    {
        var rules = await RulesAsync(ct);
        var syncOn = new Dictionary<DriverVendor, bool>();
        var rows = new List<DriverModelRow>();
        foreach (var model in (await _models.ListAsync(ct)).OrderBy(m => m.Vendor).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!syncOn.TryGetValue(model.Vendor, out var on))
            {
                on = await _store.IsSyncEnabledAsync(model.Vendor, ct);
                syncOn[model.Vendor] = on;
            }

            rows.Add(await RowAsync(model, rules, on, ct));
        }

        return rows;
    }

    /// <summary>The model page for one release (the first one when none is given).</summary>
    public async Task<DriverResult> GetModelPageAsync(Guid modelId, WindowsRelease? release, CancellationToken ct = default)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null)
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        var selected = release ?? (model.Releases.Count > 0 ? model.Releases[0] : default);
        if (!model.Releases.Contains(selected))
        {
            return DriverResult.NotFound($"{model.DisplayName} has no {ReleaseLabel(selected)}.");
        }

        var now = _time.GetUtcNow();
        var rules = await RulesAsync(ct);
        var syncOn = await _store.IsSyncEnabledAsync(model.Vendor, ct);
        var resolved = await ResolveAsync(model, selected, rules, ct);
        var pointer = (await _pointers.GetAsync(modelId, selected, ct))?.Value;
        var included = resolved.Where(r => r.Decision.Action != DriverAction.Skip && r.Component.State == DriverComponentState.Active).ToList();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        DriverCheckBlockedReason? blocked = model.State == DriverModelState.Draft ? DriverCheckBlockedReason.Draft
            : !syncOn ? DriverCheckBlockedReason.SyncOff
            : await _runs.GetActiveAsync(ct) is not null ? DriverCheckBlockedReason.Running
            : null;
        var checkAvailableAt = model.LastCheckedAt + DriverSyncService.ModelCheckLimit;
        var rows = await RowsAsync(model, selected, resolved, today, ct);
        var counted = rows.Where(r => r.State == DriverComponentState.Active).ToList();

        return DriverResult.Ok(new DriverModelPage
        {
            Model = await RowAsync(model, rules, syncOn, ct),
            Release = selected,
            CheckAvailableAt = checkAvailableAt > now ? checkAvailableAt : null,
            CheckBlockedReason = blocked,
            SetVersion = pointer?.CurrentVersion,
            BuiltAt = pointer?.BuiltAt,
            HasPreviousSet = pointer?.PreviousVersion is not null,
            PreviousSetVersion = pointer?.PreviousVersion,
            PendingVersion = pointer?.PendingVersion,
            Pending = pointer is { Dirty: true, BuildAfter: { } buildAt } ? new DriverPendingChanges { Changes = pointer.PendingChanges, BuildAt = buildAt } : null,
            Preparing = model.State == DriverModelState.Active ? await PreparingAsync(model, selected, pointer, included.Count, ct) : null,
            Notices = await NoticesAsync(model, selected, ct),
            Tiles = new DriverModelTiles
            {
                Offline = rows.Count(r => r.Action == DriverAction.Offline),
                OfflineAndSpecialize = rows.Count(r => r.Action == DriverAction.OfflineAndSpecialize),
                Specialize = rows.Count(r => r.Action == DriverAction.Specialize),
                Skip = rows.Count(r => r.Action == DriverAction.Skip),
                DownloadPerDeviceBytes = counted.Where(r => r.Action != DriverAction.Skip).Sum(r => r.SizeBytes),
            },
            Components = rows,
        });
    }

    // Banners for custom components whose replaced vendor component has a new version nobody dismissed.
    private async Task<IReadOnlyList<DriverCustomNotice>> NoticesAsync(DriverModelRecord model, WindowsRelease release, CancellationToken ct)
    {
        var notices = new List<DriverCustomNotice>();
        var modelKey = model.ModelId.ToString("D");
        foreach (var (customKey, target) in await _store.CustomTargets.ListAllAsync(ct))
        {
            if (target.RowKey != modelKey || target.Value is not { NoticeVersion: { } version, ReplacesLineageKey: { } lineage } notice || notice.DismissedVersion == version
                || (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, customKey, ct))?.Value is not { } custom)
            {
                continue;
            }

            var vendorComponent = (await _store.Components.GetAsync(DriverTables.Pair(model.ModelId, release), lineage, ct))?.Value;
            notices.Add(new DriverCustomNotice
            {
                CustomId = custom.CustomId,
                Name = custom.Name,
                VendorComponentName = vendorComponent?.Name ?? lineage,
                ComponentId = notice.NoticeComponentId ?? lineage,
                Version = version,
                ReleasedAt = notice.NoticeReleasedAt is { } released ? DateOnly.FromDateTime(released.UtcDateTime) : null,
            });
        }

        return notices;
    }

    private async Task<List<DriverComponentRow>> RowsAsync(DriverModelRecord model, WindowsRelease release, IEnumerable<DriverResolvedComponent> resolved, DateTimeOffset today, CancellationToken ct)
    {
        var rollBacks = (await _store.RollBacks.ListPartitionAsync(DriverTables.Pair(model.ModelId, release), ct)).ToDictionary(r => r.RowKey, r => r.Value, StringComparer.Ordinal);
        var modelKey = model.ModelId.ToString("D");
        var customs = new List<DriverCustomComponentRecord>();
        foreach (var (customKey, target) in await _store.CustomTargets.ListAllAsync(ct))
        {
            if (target.RowKey == modelKey && (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, customKey, ct))?.Value is { } custom)
            {
                customs.Add(custom);
            }
        }

        var replacedBy = new Dictionary<string, DriverCustomComponentRecord>(StringComparer.Ordinal);
        foreach (var replacement in await _store.Replacements.ListPartitionAsync(modelKey, ct))
        {
            if (customs.FirstOrDefault(c => c.CustomId == replacement.Value.CustomId) is { } custom)
            {
                replacedBy[replacement.RowKey] = custom;
            }
        }

        var rows = resolved.Select(r => replacedBy.TryGetValue(r.LineageKey, out var custom)
                ? ToRow(model.Vendor, r, today, null) with { Action = DriverAction.Skip, Source = DriverActionSource.ReplacedByCustom, ReplacedBy = new DriverReplacedByInfo { CustomId = custom.CustomId, Name = custom.Name } }
                : ToRow(model.Vendor, r, today, rollBacks.GetValueOrDefault(r.LineageKey)))
            .ToList();
        var vendorNames = rows.ToDictionary(r => r.LineageKey, r => r.Name, StringComparer.Ordinal);
        rows.AddRange(customs.Select(c =>
        {
            var replaces = replacedBy.FirstOrDefault(p => p.Value.CustomId == c.CustomId).Key;
            return CustomRow(c, replaces is null ? null : vendorNames.GetValueOrDefault(replaces));
        }));
        return rows;
    }

    private static DriverComponentRow CustomRow(DriverCustomComponentRecord custom, string? replacesName) => new()
    {
        LineageKey = DriverCustomComponentService.LineageKey(custom.CustomId),
        ComponentId = DriverCustomComponentService.LineageKey(custom.CustomId),
        Name = custom.Name,
        Category = custom.Category,
        Version = custom.Version,
        LastUpdated = DateOnly.FromDateTime((custom.ChangedAt ?? custom.UploadedAt).UtcDateTime),
        Action = custom.Action,
        AllowedActions = [custom.Action],
        Source = DriverActionSource.Custom,
        State = DriverComponentState.Active,
        Custom = new DriverCustomInfo { CustomId = custom.CustomId, ReplacesName = replacesName, UploadedBy = custom.UploadedBy },
        Details =
        [
            new DriverDetailField { Label = "Package", Value = custom.FileName },
            new DriverDetailField { Label = "Uploaded by", Value = custom.UploadedBy ?? "Unknown" },
            new DriverDetailField { Label = "Uploaded", Value = custom.UploadedAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
            new DriverDetailField { Label = "Replaces", Value = replacesName ?? "Nothing, added alongside" },
            new DriverDetailField { Label = "Download size", Value = FormatSize(custom.Size) },
        ],
        SizeBytes = custom.Size,
    };

    /// <summary>One component of a model and release as the model page shows it, or null when unknown.</summary>
    public async Task<DriverComponentRow?> GetComponentRowAsync(DriverModelRecord model, WindowsRelease release, string lineageKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var now = _time.GetUtcNow();
        var resolved = (await ResolveAsync(model, release, await RulesAsync(ct), ct)).Where(r => r.LineageKey == lineageKey).ToList();
        return resolved.Count == 0 ? null : (await RowsAsync(model, release, resolved, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), ct))[0];
    }

    // A roll back shows the held version, and Rolled back as the source unless the component is locked.
    private static DriverComponentRow ToRow(DriverVendor vendor, DriverResolvedComponent r, DateTimeOffset today, DriverRollBackRecord? rollBack) => new()
    {
        LineageKey = r.LineageKey,
        ComponentId = r.Component.ComponentId,
        Name = r.Component.Name,
        Category = r.Component.Category,
        Version = rollBack?.Version ?? r.Component.Version,
        // When a sync last brought in a new version, or added the component; the vendor's release date is in the details.
        LastUpdated = DateOnly.FromDateTime((r.Component.UpdatedAt > r.Component.AddedAt ? r.Component.UpdatedAt : r.Component.AddedAt).UtcDateTime),
        Action = r.Decision.Action,
        AllowedActions = r.Component.AllowedActions,
        Source = rollBack is not null && r.Decision.Source != DriverActionSource.Locked ? DriverActionSource.RolledBack : r.Decision.Source,
        RuleName = r.Decision.RuleName,
        State = r.Component.State,
        UpdatedByLastSync = r.Component.UpdatedAt >= today && r.Component.UpdatedAt > r.Component.AddedAt,
        RollBack = rollBack is null ? null : new DriverComponentRollBackInfo { Version = rollBack.Version, Until = rollBack.Until, SkippedVersion = rollBack.SkippedVersion, ChangedBy = rollBack.ChangedBy, ChangedAt = rollBack.ChangedAt },
        Details = Details(vendor, r.Component),
        DescriptionUrl = r.Component.DescriptionUrl,
        SizeBytes = r.Component.Size,
    };

    /// <summary>
    /// The expanded row's labelled fields: the vendor's first three, the default action and release
    /// date, the vendor's fourth, then when it was added, the previous version and the size.
    /// </summary>
    public static IReadOnlyList<DriverDetailField> Details(DriverVendor vendor, DriverComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var vendorFields = component.Details;
        var fields = new List<DriverDetailField>(vendorFields.Take(3))
        {
            new() { Label = "Default action", Value = DriverPolicyService.ActionLabel(component.DefaultAction) },
            new() { Label = $"Released by {DriverSyncService.VendorName(vendor)}", Value = component.ReleasedAt is { } released ? released.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "Not specified" },
        };
        fields.AddRange(vendorFields.Skip(3));
        fields.Add(new DriverDetailField { Label = "Added to this model", Value = component.AddedAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        fields.Add(new DriverDetailField { Label = "Previous version", Value = component.PreviousVersion ?? "None" });
        fields.Add(new DriverDetailField { Label = "Download size", Value = FormatSize(component.Size) });
        return fields;
    }

    /// <summary>"113 MB", "2.41 GB", as the portal writes sizes.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000d:0.00} GB"),
        >= 1_000_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1_000_000d)} MB"),
        >= 1_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1_000d)} KB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    /// <summary>Components of a model and release with their resolved actions, in set order.</summary>
    public async Task<IReadOnlyList<DriverResolvedComponent>> ResolveAsync(DriverModelRecord model, WindowsRelease release, IReadOnlyList<DriverRuleRecord> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var pair = DriverTables.Pair(model.ModelId, release);
        var overrides = (await _store.Overrides.ListPartitionAsync(pair, ct)).ToDictionary(r => r.RowKey, r => r.Value.Action, StringComparer.Ordinal);
        return (await _store.Components.ListPartitionAsync(pair, ct))
            .Select(r => new DriverResolvedComponent(r.RowKey, r.Value, Resolve(model.Vendor, r.Value, overrides.TryGetValue(r.RowKey, out var chosen) ? chosen : null, rules)))
            .OrderBy(r => r.Decision.Action == DriverAction.Skip ? 2 : r.Decision.Action == DriverAction.Specialize ? 1 : 0)
            .ThenBy(r => r.Component.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Component.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every rule.</summary>
    public async Task<IReadOnlyList<DriverRuleRecord>> RulesAsync(CancellationToken ct = default) =>
        (await _store.Rules.ListPartitionAsync(DriverStore.RulePartition, ct)).Select(r => r.Value).ToList();

    private static DriverPolicyDecision Resolve(DriverVendor vendor, DriverComponentRecord component, DriverAction? chosen, IReadOnlyList<DriverRuleRecord> rules) =>
        DriverPolicyResolver.Resolve(new DriverPolicyInput(vendor, component.ComponentId, component.Name, component.Category, component.AllowedActions, component.DefaultAction), chosen, rules);

    // The row shows the release with the largest download, since that is what one device can need.
    private async Task<DriverModelRow> RowAsync(DriverModelRecord model, IReadOnlyList<DriverRuleRecord> rules, bool syncOn, CancellationToken ct)
    {
        var included = 0;
        var skipped = 0;
        long maxBytes = -1;
        var waiting = 0;
        var failed = 0;
        string? failureReason = null;
        DriverProgressCount? downloading = null;
        foreach (var release in model.Releases)
        {
            var resolved = await ResolveAsync(model, release, rules, ct);
            var active = resolved.Where(r => r.Component.State == DriverComponentState.Active).ToList();
            var bytes = active.Where(r => r.Decision.Action != DriverAction.Skip).Sum(r => r.Component.Size);
            waiting += resolved.Count(r => r.Component.State == DriverComponentState.WaitingForApproval);
            if (bytes > maxBytes)
            {
                maxBytes = bytes;
                included = active.Count(r => r.Decision.Action != DriverAction.Skip);
                skipped = resolved.Count(r => r.Decision.Action == DriverAction.Skip);
            }

            if (model.State != DriverModelState.Active)
            {
                continue;
            }

            var pointer = (await _pointers.GetAsync(model.ModelId, release, ct))?.Value;
            if (await PreparingAsync(model, release, pointer, active.Count(r => r.Decision.Action != DriverAction.Skip), ct) is { } preparing)
            {
                downloading = new DriverProgressCount { Done = (downloading?.Done ?? 0) + preparing.Done, Total = (downloading?.Total ?? 0) + preparing.Total };
            }

            foreach (var item in active.Where(r => r.Decision.Action != DriverAction.Skip))
            {
                var file = (await _store.Files.GetAsync(model.Vendor.ToString(), DriverSyncService.FileKey(item.Component.ComponentId, item.Component.Version), ct))?.Value;
                if (file?.Status == DriverFileStatus.Failed)
                {
                    failed++;
                    failureReason ??= $"{item.Component.Name}: {file.FailureReason ?? "The download failed."}";
                }
            }
        }

        return new DriverModelRow
        {
            ModelId = model.ModelId,
            Vendor = model.Vendor,
            DisplayName = model.DisplayName,
            MatchKeys = model.MatchKeys,
            Architecture = model.Architecture,
            Releases = model.Releases,
            State = model.State,
            Included = included,
            Skipped = skipped,
            MaxDownloadBytes = Math.Max(0, maxBytes),
            Status = new DriverModelStatus
            {
                WaitingForApproval = waiting,
                FailedItems = failed,
                FailureReason = failureReason,
                SyncOff = model.State == DriverModelState.Active && !syncOn,
                Downloading = downloading,
            },
        };
    }

    // A set is being prepared while its files download: a pending set, or the first set of a
    // model whose onboarding was just completed.
    private async Task<DriverPreparingSet?> PreparingAsync(DriverModelRecord model, WindowsRelease release, DriverSetPointerRecord? pointer, int includedCount, CancellationToken ct)
    {
        if (pointer?.PendingVersion is { } pending && await _sets.GetAsync(model.ModelId, release, pending, ct) is { } document)
        {
            var done = 0;
            foreach (var item in document.Items)
            {
                var status = item.Origin == DriverComponentOrigin.Custom
                    ? DriverFileStatus.Verified
                    : (await _store.Files.GetAsync(model.Vendor.ToString(), item.FileKey, ct))?.Value.Status;
                if (status is DriverFileStatus.Verified or DriverFileStatus.Failed)
                {
                    done++;
                }
            }

            return new DriverPreparingSet { SetVersion = pending, Done = done, Total = document.Items.Count };
        }

        return pointer?.CurrentVersion is null
            ? new DriverPreparingSet { SetVersion = (pointer?.LastVersion ?? 0) + 1, Done = 0, Total = includedCount }
            : null;
    }

    private DriverResult? ValidateSelection(DriverOnboardRequest? request)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (_catalogs.Adapter(request.Vendor) is null)
        {
            return DriverResult.BadRequest($"{DriverSyncService.VendorName(request.Vendor)} is not supported.");
        }

        if (request.CatalogModelKeys is not { Count: > 0 } keys || keys.Any(string.IsNullOrWhiteSpace))
        {
            return DriverResult.BadRequest("Choose at least one model.");
        }

        if (keys.Count > MaxModelsPerRequest)
        {
            return DriverResult.BadRequest($"Choose at most {MaxModelsPerRequest} models at a time.");
        }

        return request.Releases is not { Count: > 0 } ? DriverResult.BadRequest("Choose at least one Windows release.") : null;
    }

    private static bool Matches(DriverCatalogModelRecord model, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || model.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || model.MatchKeys.Any(k => k.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Texts shared by the driver endpoints.</summary>
public static class DriverHttpText
{
    /// <summary>400 for a write without an actor.</summary>
    public const string MissingActor = "The acting user is missing.";
}

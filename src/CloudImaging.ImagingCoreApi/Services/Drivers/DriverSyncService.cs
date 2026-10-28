using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Outcome of asking for a run.</summary>
public enum DriverRunStartStatus
{
    /// <summary>The run started.</summary>
    Started,

    /// <summary>Another run is active.</summary>
    Running,

    /// <summary>The limit since the last start has not passed.</summary>
    TooSoon,

    /// <summary>The run cannot start for the model (draft, or its vendor's sync is off).</summary>
    NotAllowed,

    /// <summary>The model does not exist.</summary>
    NotFound,
}

/// <summary>Result of asking for a run.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="RunId">The new run.</param>
/// <param name="AvailableAt">When a run can be started again.</param>
/// <param name="Detail">Message for a refusal.</param>
/// <param name="BlockedReason">Why a model check cannot start.</param>
public sealed record DriverRunStart(DriverRunStartStatus Status, Guid? RunId = null, DateTimeOffset? AvailableAt = null, string? Detail = null, DriverCheckBlockedReason? BlockedReason = null);

/// <summary>
/// Driver sync (research R1, R7, R16): runs, and the handlers of the <c>driver-work</c> messages.
///
/// A run fans out as queue messages: <c>FetchVendorCatalog</c> per vendor, <c>ResolveModel</c> per
/// onboarded model and release, <c>BuildSet</c> per pair, <c>DownloadFile</c> per file a set
/// needs, and <c>BuildSet</c> again when a pending set's last file is done. The run's
/// <c>Outstanding</c> count holds the messages not yet done; each has a step marker from when it
/// is counted until it is done, so a second copy of a message changes nothing, and the message
/// that brings the count to zero enqueues <c>FinalizeRun</c>. Children are counted before their
/// markers are written and parents complete only after enqueuing them, so a crash can leave the
/// count too high (the 2-hour <c>FinalizeRun</c> then ends the run as Failed) but never too low.
/// </summary>
public sealed partial class DriverSyncService
{
    /// <summary>Limit shared by Sync now and Check for updates on the Models tab.</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(60);

    /// <summary>Limit of Check for updates on one model.</summary>
    public static readonly TimeSpan ModelCheckLimit = TimeSpan.FromMinutes(15);

    /// <summary>A run still running after this is ended as Failed.</summary>
    public static readonly TimeSpan Watchdog = TimeSpan.FromHours(2);

    /// <summary>An upload never used for a custom component is removed this long after it started.</summary>
    public static readonly TimeSpan StaleUploadAge = TimeSpan.FromDays(1);

    // Beyond the watchdog, how long a run lock may stay before it is treated as abandoned.
    private static readonly TimeSpan StaleLockMargin = TimeSpan.FromMinutes(15);

    /// <summary>Run history and set build rows are kept this long.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(90);

    private const int MaxConcurrencyRetries = 20;
    private const string TimedOutReason = "The sync did not finish within 2 hours.";
    private const string ModelCheckedMarker = "ModelChecked|";
    private const int ReportReadBatch = 8;
    private const int LatestItems = 3;
    private static readonly StepResult NoWork = new([]);

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly DriverSyncRunRepository _runs;
    private readonly DriverSetDocumentRepository _sets;
    private readonly DriverCatalogService _catalogs;
    private readonly DriverStorageService _storage;
    private readonly IDriverWorkSender _work;
    private readonly DeviceSessionRepository _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<DriverSyncService> _logger;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="pointers">Set pointers.</param>
    /// <param name="runs">Runs.</param>
    /// <param name="sets">Set documents.</param>
    /// <param name="catalogs">Vendor catalogs.</param>
    /// <param name="storage">Vendor downloads.</param>
    /// <param name="work">Queue.</param>
    /// <param name="sessions">Sessions, for pinned sets.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public DriverSyncService(
        DriverStore store,
        DriverModelRepository models,
        DriverSetPointerRepository pointers,
        DriverSyncRunRepository runs,
        DriverSetDocumentRepository sets,
        DriverCatalogService catalogs,
        DriverStorageService storage,
        IDriverWorkSender work,
        DeviceSessionRepository sessions,
        TimeProvider time,
        ILogger<DriverSyncService> logger)
    {
        _store = store;
        _models = models;
        _pointers = pointers;
        _runs = runs;
        _sets = sets;
        _catalogs = catalogs;
        _storage = storage;
        _work = work;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    /// <summary>Step name of a message: equal for copies of the same work within a run.</summary>
    public static string Step(DriverWorkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"{message.Kind}|{message.Vendor}|{message.ModelId}|{message.Release}|{message.FileKey}";
    }

    /// <summary>Row key of a stored file: component and version, with characters a row key may not hold replaced.</summary>
    public static string FileKey(string componentId, string? version) =>
        string.Concat($"{componentId}_{version}".Select(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '-' : c));

    /// <summary>
    /// Starts a run. <c>Manual</c> and <c>Check</c> share the 60-minute limit with <c>Scheduled</c>;
    /// <c>ModelCheck</c> has its own 15-minute limit per model. Only one run is ever active.
    /// </summary>
    public async Task<DriverRunStart> StartRunAsync(DriverSyncTrigger trigger, string? startedBy, Guid? modelId = null, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var settings = await _store.GetSyncSettingsAsync(ct);
        var sharedAvailableAt = settings.Value.LastRunStartedAt + RunLimit;
        DriverModelRecord? model = null;
        if (trigger == DriverSyncTrigger.ModelCheck)
        {
            model = modelId is { } id ? (await _models.GetAsync(id, ct))?.Value : null;
            if (model is null)
            {
                return new DriverRunStart(DriverRunStartStatus.NotFound, Detail: "Driver model not found.");
            }

            if (model.State == DriverModelState.Draft)
            {
                return new DriverRunStart(DriverRunStartStatus.NotAllowed, Detail: "Complete onboarding first.", BlockedReason: DriverCheckBlockedReason.Draft);
            }

            if (!await _store.IsSyncEnabledAsync(model.Vendor, ct))
            {
                return new DriverRunStart(DriverRunStartStatus.NotAllowed, Detail: $"Sync is off for {VendorName(model.Vendor)}.", BlockedReason: DriverCheckBlockedReason.SyncOff);
            }
        }

        if (await _runs.GetActiveAsync(ct) is { } active)
        {
            // A lock that outlived its watchdog belongs to a run that never got going (its start
            // failed before the watchdog was queued); it is ended so syncs are not blocked for good.
            if (now - active.StartedAt <= Watchdog + StaleLockMargin)
            {
                return new DriverRunStart(DriverRunStartStatus.Running, AvailableAt: Later(sharedAvailableAt, now), Detail: "A driver sync is running. Try again when it has finished.", BlockedReason: DriverCheckBlockedReason.Running);
            }

            LogStaleLockReleased(_logger, active.RunId);
            await EndRunAsync(active.RunId, DriverSyncResult.Failed, "The sync could not be finished.", ct);
            await _runs.ReleaseAsync(active.RunId, ct);
        }

        if (trigger is DriverSyncTrigger.Manual or DriverSyncTrigger.Check && sharedAvailableAt > now)
        {
            return new DriverRunStart(DriverRunStartStatus.TooSoon, AvailableAt: sharedAvailableAt, Detail: $"Drivers were checked less than an hour ago. Try again after {sharedAvailableAt:yyyy-MM-dd HH:mm} UTC.");
        }

        if (model?.LastCheckedAt + ModelCheckLimit is { } modelAvailableAt && modelAvailableAt > now)
        {
            return new DriverRunStart(DriverRunStartStatus.TooSoon, AvailableAt: modelAvailableAt, Detail: $"This model was checked less than 15 minutes ago. Try again after {modelAvailableAt:yyyy-MM-dd HH:mm} UTC.");
        }

        var vendors = new List<DriverVendor>();
        foreach (var adapter in _catalogs.Adapters)
        {
            if ((model is null || model.Vendor == adapter.Vendor) && await _store.IsSyncEnabledAsync(adapter.Vendor, ct))
            {
                vendors.Add(adapter.Vendor);
            }
        }

        var runId = Guid.NewGuid();
        if (!await _runs.TryAcquireAsync(runId, now, ct))
        {
            return new DriverRunStart(DriverRunStartStatus.Running, AvailableAt: Later(sharedAvailableAt, now), Detail: "A driver sync is running. Try again when it has finished.", BlockedReason: DriverCheckBlockedReason.Running);
        }

        var fetches = vendors.Select(vendor => new DriverWorkMessage { Kind = DriverWorkKind.FetchVendorCatalog, RunId = runId, Vendor = vendor, ModelId = model?.ModelId }).ToList();
        await _runs.TryAddAsync(new DriverSyncRunRecord
        {
            RunId = runId,
            Trigger = trigger,
            ModelId = model?.ModelId,
            ModelName = model?.DisplayName,
            Outstanding = fetches.Count,
            CatalogsTotal = fetches.Count,
            PlannedVendors = vendors,
            StartedAt = now,
            StartedBy = startedBy,
        }, ct);
        foreach (var fetch in fetches)
        {
            await _runs.TryAddStepAsync(runId, Step(fetch), ct);
        }

        if (model is null)
        {
            await UpdateSettingsAsync(s => s with { LastRunStartedAt = now }, ct);
        }
        else
        {
            await UpdateModelAsync(model.ModelId, m => m with { LastCheckedAt = now }, ct);
        }

        await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.FinalizeRun, RunId = runId }, Watchdog, ct);
        foreach (var fetch in fetches)
        {
            await _work.EnqueueAsync(fetch, TimeSpan.Zero, ct);
        }

        if (fetches.Count == 0)
        {
            await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.FinalizeRun, RunId = runId }, TimeSpan.Zero, ct);
        }

        LogRunStarted(_logger, runId, trigger, fetches.Count);
        return new DriverRunStart(DriverRunStartStatus.Started, runId);
    }

    /// <summary>The run as the portal sees it.</summary>
    public static DriverSyncRun ToContract(DriverSyncRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new DriverSyncRun
        {
            RunId = run.RunId,
            Trigger = run.Trigger,
            ModelId = run.ModelId,
            ModelName = run.ModelName,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Result = run.Result,
            StartedBy = run.StartedBy,
            Counts = run.Counts,
            Vendors = run.Vendors,
            PlannedVendors = run.PlannedVendors,
            ModelsTotal = run.ModelsTotal,
            ModelsChecked = run.ModelsChecked,
            DownloadedComponents = run.DownloadedComponents,
            DownloadedBytes = run.DownloadedBytes,
            NewSets = run.NewSets,
        };
    }

    /// <summary>
    /// How far a run got: each stage's done and total, what it found so far and its latest items.
    /// One read of the run row, whatever the size of the run. Null when the run is unknown.
    /// </summary>
    public async Task<DriverSyncRunProgress?> GetProgressAsync(Guid runId, CancellationToken ct = default)
    {
        if ((await _runs.GetAsync(runId, ct))?.Value is not { } run)
        {
            return null;
        }

        return new DriverSyncRunProgress
        {
            Run = ToContract(run),
            Catalogs = new DriverSyncStage { Done = run.Vendors.Count, Total = Math.Max(run.CatalogsTotal, run.Vendors.Count) },
            Models = new DriverSyncStage { Done = run.ModelsChecked, Total = Math.Max(run.ModelsTotal, run.ModelsChecked) },
            Components = new DriverSyncStage { Done = run.Downloads, Total = Math.Max(run.DownloadsQueued, run.Downloads) },
            DriverSets = new DriverSyncStage { Done = run.SetChecks, Total = Math.Max(run.SetChecksQueued, run.SetChecks) },

            // The running totals; once the run ends its counts are recounted from the stored items.
            Found = run.Result == DriverSyncResult.Running ? run.Found : run.Counts,
            Latest = run.Latest,
        };
    }

    /// <summary>Runs that started between <paramref name="from"/> and <paramref name="to"/>, newest first, with their items.</summary>
    public async Task<IReadOnlyList<DriverSyncReportRun>> GetReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // Items recorded before they carried a vendor take it from their model.
        var vendors = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId, m => m.Vendor);
        var report = new List<DriverSyncReportRun>();

        // Up to 90 days of runs, so their item partitions are read a few at a time rather than one by one.
        foreach (var batch in (await _runs.ListAsync(ct)).Where(r => r.StartedAt >= from && r.StartedAt <= to).Chunk(ReportReadBatch))
        {
            var reads = batch.Select(async run => new DriverSyncReportRun
            {
                Run = ToContract(run),
                Items = (await _runs.ListItemsAsync(run.RunId, ct))
                    .Select(i => i is { Vendor: null, ModelId: { } id } && vendors.TryGetValue(id, out var vendor) ? i with { Vendor = vendor } : i)
                    .ToList(),
            });
            report.AddRange(await Task.WhenAll(reads));
        }

        return report;
    }

    /// <summary>Handles one <c>driver-work</c> message.</summary>
    public async Task HandleAsync(DriverWorkMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message.Kind)
        {
            case DriverWorkKind.FetchVendorCatalog:
                await RunStepAsync(message, async () => new StepResult(await FetchVendorCatalogAsync(message, ct)), ct);
                break;
            case DriverWorkKind.ResolveModel:
                await RunStepAsync(message, () => ResolveModelAsync(message, ct), ct);
                break;
            case DriverWorkKind.DownloadFile:
                await RunStepAsync(message, () => DownloadFileAsync(message, ct), ct);
                break;
            case DriverWorkKind.BuildSet:
                await RunStepAsync(message, () => BuildSetAsync(message, ct), ct);
                break;
            case DriverWorkKind.FinalizeRun:
                await FinalizeRunAsync(message.RunId, ct);
                break;
            default:
                LogNoHandler(_logger, message.Kind);
                break;
        }
    }

    /// <summary>
    /// Handles a message the host gave up on after 3 attempts: its file or run item is recorded as
    /// failed and the run's count is brought down so the run still finishes.
    /// </summary>
    public async Task HandlePoisonAsync(DriverWorkMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _time.GetUtcNow();
        const string reason = "Failed 3 times. Imaging Core will try again at the next sync.";
        IReadOnlyList<DriverWorkMessage> builds = [];
        DriverFileRecord? poisonedFile = null;
        if (message is { Kind: DriverWorkKind.DownloadFile, Vendor: { } vendor, FileKey: { } fileKey }
            && await _store.Files.GetAsync(vendor.ToString(), fileKey, ct) is { } file
            && file.Value.Status != DriverFileStatus.Verified)
        {
            poisonedFile = file.Value;

            // Stamped as attempted now, so this run's rebuild does not queue the same download again.
            await _store.Files.UpsertAsync(vendor.ToString(), fileKey, file.Value with { Status = DriverFileStatus.Failed, FailureReason = reason, AttemptedAt = now }, ct);

            // Like a failed download: a pending set waiting on this file is built now, with the
            // last verified file or without the component, instead of waiting for the next sync.
            builds = await BuildsWaitingOnAsync(file.Value.BlobName, ct);
        }

        if (message.RunId is not { } runId)
        {
            await EnqueueChildrenAsync(null, builds, ct);
            return;
        }

        if (message.Kind == DriverWorkKind.FinalizeRun)
        {
            await EndRunAsync(runId, DriverSyncResult.Failed, "The sync could not be finished.", ct);
            return;
        }

        var model = message.ModelId is { } modelId ? (await _models.GetAsync(modelId, ct))?.Value : null;
        DriverSyncRunItem? failed = null;
        if (await _runs.IsStepOpenAsync(runId, Step(message), ct))
        {
            if (message is { Kind: DriverWorkKind.FetchVendorCatalog, Vendor: { } catalogVendor })
            {
                // A catalog that was never read shows as failed on the run, like one the vendor refused.
                await UpdateRunAsync(runId, r => r.Vendors.Any(v => v.Vendor == catalogVendor) ? r : r with { Vendors = [.. r.Vendors, new DriverSyncRunVendor { Vendor = catalogVendor, Processed = false, FailureReason = reason }] }, ct);
            }
            else if (poisonedFile is not null && message.Vendor is { } fileVendor)
            {
                failed = await _runs.AddItemAsync(runId, now, DownloadFailure(fileVendor, poisonedFile, reason), ct);
            }
            else
            {
                failed = await _runs.AddItemAsync(runId, now, new DriverSyncRunItem
                {
                    ModelId = message.ModelId,
                    ModelName = model?.DisplayName,
                    Release = message.Release,
                    Outcome = DriverSyncItemOutcome.Failed,
                    Reason = reason,
                    Vendor = message.Vendor ?? model?.Vendor,
                }, ct);
            }
        }

        // Children are counted before this step completes, so the run cannot finish under them.
        await EnqueueChildrenAsync(runId, builds, ct);
        await CompleteStepAsync(runId, message, new StepResult([], Items: failed is null ? [] : [failed]), ct);
    }

    /// <summary>
    /// Merges a vendor catalog into a model and release's components (data-model section 6): a
    /// known lineage takes the new version and keeps its state (a withdrawn one becomes active
    /// again), a new lineage is active or waits for approval, and a lineage the vendor no longer
    /// publishes is withdrawn. Returns the run items.
    /// </summary>
    public async Task<IReadOnlyList<DriverSyncRunItem>> MergeComponentsAsync(DriverModelRecord model, WindowsRelease release, IReadOnlyList<DriverVendorComponent> catalog, bool waitForApproval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(catalog);
        var now = _time.GetUtcNow();
        var pair = DriverTables.Pair(model.ModelId, release);
        var existing = (await _store.Components.ListPartitionAsync(pair, ct)).ToDictionary(row => row.RowKey, row => row.Value, StringComparer.Ordinal);

        // The first resolution of a pair reports nothing as new and holds nothing for approval.
        var firstResolution = existing.Count == 0;
        var rollBacks = (await _store.RollBacks.ListPartitionAsync(pair, ct)).ToDictionary(r => r.RowKey, r => r.Value, StringComparer.Ordinal);
        var replacements = (await _store.Replacements.ListPartitionAsync(model.ModelId.ToString("D"), ct)).ToDictionary(r => r.RowKey, r => r.Value.CustomId, StringComparer.Ordinal);
        var items = new List<DriverSyncRunItem>();
        var writes = new List<(string, DriverComponentRecord)>();
        foreach (var (lineage, component) in catalog)
        {
            if (existing.TryGetValue(lineage, out var old))
            {
                var changed = old.ComponentId != component.ComponentId || old.Version != component.Version;

                // A roll back until a newer version ends once the vendor publishes one after the skipped version.
                if (changed && rollBacks.TryGetValue(lineage, out var rollBack) && rollBack.Until == DriverRollBackUntil.NewerVersion
                    && component.Version != rollBack.SkippedVersion && component.Version != rollBack.Version)
                {
                    await _store.RollBacks.DeleteAsync(pair, lineage, ct: ct);
                    await _store.LogAsync(now, "Sync", null, "Component", $"{model.DisplayName}: {component.Name}", $"Roll back ended: {VendorName(model.Vendor)} published {component.Version}", ct);
                }

                // A new version of a lineage a custom component replaces shows the banner on the model page.
                if (changed && replacements.TryGetValue(lineage, out var customId)
                    && await _store.CustomTargets.GetAsync(customId.ToString("N"), model.ModelId.ToString("D"), ct) is { } target)
                {
                    await _store.CustomTargets.UpsertAsync(customId.ToString("N"), target.RowKey, target.Value with { NoticeVersion = component.Version, NoticeComponentId = component.ComponentId, NoticeReleasedAt = component.ReleasedAt }, ct);
                }
                writes.Add((lineage, component with
                {
                    State = old.State == DriverComponentState.Withdrawn ? DriverComponentState.Active : old.State,
                    AddedAt = old.AddedAt,
                    UpdatedAt = changed ? now : old.UpdatedAt,
                    PreviousVersion = changed ? old.Version : old.PreviousVersion,
                    VerifiedVersion = old.VerifiedVersion,
                    VerifiedFile = old.VerifiedFile,
                }));
                if (changed)
                {
                    items.Add(Item(model, release, lineage, component.Name, old.Version, component.Version, DriverSyncItemOutcome.Updated) with { Category = component.Category });
                }

                continue;
            }

            var waits = waitForApproval && !firstResolution;
            writes.Add((lineage, component with
            {
                State = waits ? DriverComponentState.WaitingForApproval : DriverComponentState.Active,
                AddedAt = now,
                UpdatedAt = now,
            }));
            if (!firstResolution)
            {
                items.Add(Item(model, release, lineage, component.Name, null, component.Version, waits ? DriverSyncItemOutcome.NewWaitingForApproval : DriverSyncItemOutcome.New) with { Category = component.Category });
            }
        }

        var published = catalog.Select(c => c.LineageKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (lineage, old) in existing.Where(e => !published.Contains(e.Key) && e.Value.State != DriverComponentState.Withdrawn))
        {
            writes.Add((lineage, old with { State = DriverComponentState.Withdrawn, UpdatedAt = now }));
            items.Add(Item(model, release, lineage, old.Name, old.Version, null, DriverSyncItemOutcome.Withdrawn) with { Category = old.Category });
        }

        await _store.Components.UpsertManyAsync(pair, writes, ct);
        return items;
    }

    /// <summary>The set item of a custom component.</summary>
    public static DriverSetItem CustomItem(DriverCustomComponentRecord custom)
    {
        ArgumentNullException.ThrowIfNull(custom);
        var lineage = DriverCustomComponentService.LineageKey(custom.CustomId);
        return new DriverSetItem
        {
            LineageKey = lineage,
            ComponentId = lineage,
            Name = custom.Name,
            Category = custom.Category,
            Version = custom.Version,
            Action = custom.Action,
            Origin = DriverComponentOrigin.Custom,
            FileKey = $"{lineage}_{custom.Version}",
            BlobName = custom.BlobName,
            Sha256 = custom.Sha256,
            Size = custom.Size,
            FileName = custom.FileName,
            Extract = custom.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? DriverExtractKind.Zip : DriverExtractKind.None,
            InstallCommand = custom.InstallCommand,
        };
    }

    /// <summary>The set item for a version from a component's history, held by a roll back.</summary>
    public static DriverSetItem HeldItem(DriverVendor vendor, DriverComponentRecord component, DriverComponentVersionRecord held, DriverAction action)
    {
        ArgumentNullException.ThrowIfNull(held);
        var fileName = held.FileName ?? $"{held.ComponentId}.exe";
        var current = ItemFor(vendor, component, action);

        // Install details come from the current catalog entry; a command that runs the package
        // itself (Dell) must run the held package, not the newer one it was published with.
        var command = current.InstallCommand is { } install && string.Equals(install.Executable, current.FileName, StringComparison.OrdinalIgnoreCase)
            ? install with { Executable = fileName }
            : current.InstallCommand;
        return current with
        {
            ComponentId = held.ComponentId,
            Version = held.Version,
            Origin = DriverComponentOrigin.RolledBack,
            FileKey = FileKey(held.ComponentId, held.Version),
            BlobName = HeldBlob(vendor, held),
            Sha256 = held.Sha256 ?? string.Empty,
            Size = held.Size,
            FileName = fileName,
            InstallCommand = command,
        };
    }

    /// <summary>Where a version from a component's history is stored.</summary>
    public static string HeldBlob(DriverVendor vendor, DriverComponentVersionRecord version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return $"files/{vendor}/{version.ComponentId}/{version.Version}/{version.FileName ?? $"{version.ComponentId}.exe"}";
    }

    /// <summary>The set item for a vendor component's current version.</summary>
    public static DriverSetItem ItemFor(DriverVendor vendor, DriverComponentRecord component, DriverAction action)
    {
        ArgumentNullException.ThrowIfNull(component);
        var fileName = component.FileName ?? $"{component.ComponentId}.exe";
        return new DriverSetItem
        {
            LineageKey = string.Empty,
            ComponentId = component.ComponentId,
            Name = component.Name,
            Category = component.Category,
            Version = component.Version,
            Action = action,
            Origin = DriverComponentOrigin.Vendor,
            FileKey = FileKey(component.ComponentId, component.Version),
            BlobName = $"files/{vendor}/{component.ComponentId}/{component.Version}/{fileName}",
            Sha256 = component.Sha256 ?? string.Empty,
            Size = component.Size,
            FileName = fileName,
            Extract = component.Extract,
            InstallCommand = component.InstallCommand,
            PciIds = component.PciIds,
        };
    }

    private async Task RunStepAsync(DriverWorkMessage message, Func<Task<StepResult>> work, CancellationToken ct)
    {
        if (message.RunId is { } runId && !await _runs.IsStepOpenAsync(runId, Step(message), ct))
        {
            return;
        }

        var result = await work();
        await EnqueueChildrenAsync(message.RunId, result.Children, ct);
        if (message.RunId is { } done)
        {
            await CompleteStepAsync(done, message, result, ct);
            if (message.Kind == DriverWorkKind.BuildSet)
            {
                await RebuildSettledPendingAsync(message, ct);
            }
        }
    }

    private async Task EnqueueChildrenAsync(Guid? runId, IReadOnlyList<DriverWorkMessage> children, CancellationToken ct)
    {
        if (runId is not { } id)
        {
            foreach (var child in children)
            {
                await _work.EnqueueAsync(child, TimeSpan.Zero, ct);
            }

            return;
        }

        var distinct = children.Select(c => c with { RunId = id }).DistinctBy(Step).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        // Count first, then mark; a step another message already counted is given back.
        await UpdateRunAsync(id, run => Queued(run, distinct, 1), ct);
        var toSend = new List<DriverWorkMessage>();
        foreach (var child in distinct)
        {
            if (await _runs.TryAddStepAsync(id, Step(child), ct))
            {
                toSend.Add(child);
            }
        }

        if (toSend.Count < distinct.Count)
        {
            var givenBack = distinct.Except(toSend).ToList();
            await UpdateRunAsync(id, run => Queued(run, givenBack, -1), ct);
        }

        foreach (var child in toSend)
        {
            await _work.EnqueueAsync(child, TimeSpan.Zero, ct);
        }
    }

    // Counters ride on the write that completes the step, so counting adds no write that could fail finished work.
    private async Task CompleteStepAsync(Guid runId, DriverWorkMessage message, StepResult? result, CancellationToken ct)
    {
        if (!await _runs.TryCompleteStepAsync(runId, Step(message), ct))
        {
            return;
        }

        var modelChecked = message.Kind == DriverWorkKind.ResolveModel && await LastReleaseCheckedAsync(runId, message, ct);
        var items = result?.Items ?? [];
        var left = (await UpdateRunAsync(runId, run => run with
        {
            Outstanding = run.Outstanding - 1,
            ModelsChecked = run.ModelsChecked + (modelChecked ? 1 : 0),
            SetChecks = run.SetChecks + (message.Kind == DriverWorkKind.BuildSet ? 1 : 0),
            NewSets = run.NewSets + (result?.NewSet == true ? 1 : 0),
            Downloads = run.Downloads + (message.Kind == DriverWorkKind.DownloadFile ? 1 : 0),
            DownloadedComponents = run.DownloadedComponents + (result?.DownloadedBytes is null ? 0 : 1),
            DownloadedBytes = run.DownloadedBytes + (result?.DownloadedBytes ?? 0),
            Found = items.Count == 0 ? run.Found : Tally(run.Found, items),
            Latest = items.Count == 0 ? run.Latest : [.. Enumerable.Reverse(items).Concat(run.Latest).Take(LatestItems)],
        }, ct))?.Outstanding;
        if (left == 0)
        {
            await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.FinalizeRun, RunId = runId }, TimeSpan.Zero, ct);
        }
    }

    // A model counts as checked once no release of it is still open; the marker makes two releases finishing together count it once.
    private async Task<bool> LastReleaseCheckedAsync(Guid runId, DriverWorkMessage message, CancellationToken ct)
    {
        if (message.ModelId is not { } modelId)
        {
            return false;
        }

        foreach (var release in (await _models.GetAsync(modelId, ct))?.Value.Releases ?? [])
        {
            if (release != message.Release && await _runs.IsStepOpenAsync(runId, Step(new DriverWorkMessage { Kind = DriverWorkKind.ResolveModel, ModelId = modelId, Release = release }), ct))
            {
                return false;
            }
        }

        return await _runs.TryAddStepAsync(runId, $"{ModelCheckedMarker}{modelId:N}", ct);
    }

    // Outstanding work, and the downloads and set builds it holds, for the progress stages.
    private static DriverSyncRunRecord Queued(DriverSyncRunRecord run, List<DriverWorkMessage> steps, int sign) => run with
    {
        Outstanding = run.Outstanding + (sign * steps.Count),
        DownloadsQueued = run.DownloadsQueued + (sign * steps.Count(s => s.Kind == DriverWorkKind.DownloadFile)),
        SetChecksQueued = run.SetChecksQueued + (sign * steps.Count(s => s.Kind == DriverWorkKind.BuildSet)),
    };

    private static Dictionary<DriverSyncItemOutcome, int> Tally(Dictionary<DriverSyncItemOutcome, int> found, IReadOnlyList<DriverSyncRunItem> items)
    {
        var next = new Dictionary<DriverSyncItemOutcome, int>(found);
        foreach (var item in items)
        {
            next[item.Outcome] = next.GetValueOrDefault(item.Outcome) + 1;
        }

        return next;
    }

    private async Task<IReadOnlyList<DriverWorkMessage>> FetchVendorCatalogAsync(DriverWorkMessage message, CancellationToken ct)
    {
        var run = message.RunId is { } runId ? (await _runs.GetAsync(runId, ct))?.Value : null;
        if (run is not { Result: DriverSyncResult.Running } || message.Vendor is not { } vendor)
        {
            return [];
        }

        var adapter = _catalogs.Adapter(vendor);
        var newModels = 0;
        string? failure = adapter is null ? $"{VendorName(vendor)} is not supported." : null;
        if (adapter is not null && run.Trigger is DriverSyncTrigger.Scheduled or DriverSyncTrigger.Manual)
        {
            var refresh = await _catalogs.RefreshModelListAsync(adapter, ct);
            newModels = refresh.NewModels;
            failure = refresh.Failure;
        }

        // Components of every onboarded model are checked, drafts included (they are refreshed
        // without downloads); a model check reads only its own model.
        var models = adapter is null
            ? []
            : (await _models.ListAsync(ct)).Where(m => m.Vendor == vendor && (run.ModelId is null || m.ModelId == run.ModelId)).ToList();
        await UpdateRunAsync(run.RunId, r => r with
        {
            Vendors = [.. r.Vendors.Where(v => v.Vendor != vendor), new DriverSyncRunVendor { Vendor = vendor, Processed = failure is null, FailureReason = failure, NewModels = newModels }],
            ModelsTotal = r.ModelsTotal + (r.Vendors.Any(v => v.Vendor == vendor) ? 0 : models.Count(m => m.Releases.Count > 0)),
        }, ct);
        return models
            .SelectMany(m => m.Releases.Select(release => new DriverWorkMessage { Kind = DriverWorkKind.ResolveModel, ModelId = m.ModelId, Release = release }))
            .ToList();
    }

    private async Task<StepResult> ResolveModelAsync(DriverWorkMessage message, CancellationToken ct)
    {
        if (message is not { ModelId: { } modelId, Release: { } release }
            || (await _models.GetAsync(modelId, ct))?.Value is not { } model
            || !model.Releases.Contains(release))
        {
            return NoWork;
        }

        var now = _time.GetUtcNow();
        var adapter = _catalogs.Adapter(model.Vendor);
        var catalogModel = (await _store.CatalogModels.GetAsync(model.Vendor.ToString(), model.CatalogModelKey, ct))?.Value;
        string? failure;
        IReadOnlyList<DriverVendorComponent>? catalog = null;
        if (adapter is null || catalogModel is null)
        {
            failure = $"{VendorName(model.Vendor)} no longer lists this model.";
        }
        else
        {
            (catalog, failure) = await _catalogs.ReadComponentsAsync(adapter, catalogModel, release, null, ct);
        }

        if (catalog is null)
        {
            LogCatalogFailed(_logger, modelId, release, failure);
            return message.RunId is { } failedRun
                ? new StepResult([], Items: [await _runs.AddItemAsync(failedRun, now, Item(model, release, null, null, null, null, DriverSyncItemOutcome.Failed) with { Reason = failure }, ct)])
                : NoWork;
        }

        var settings = await _store.GetSyncSettingsAsync(ct);
        var waitForApproval = settings.Value.WaitForApprovalOfNewComponents && message.RunId is not null && model.State == DriverModelState.Active;
        var items = await MergeComponentsAsync(model, release, catalog, waitForApproval, ct);
        var recorded = new List<DriverSyncRunItem>();
        if (message.RunId is { } runId)
        {
            foreach (var item in items)
            {
                recorded.Add(await _runs.AddItemAsync(runId, now, item, ct));
            }
        }

        // A draft matches no device and downloads nothing until onboarding is completed.
        return new StepResult(
            model.State == DriverModelState.Draft ? [] : [new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = modelId, Release = release }],
            Items: recorded);
    }

    private async Task<StepResult> DownloadFileAsync(DriverWorkMessage message, CancellationToken ct)
    {
        if (message is not { Vendor: { } vendor, FileKey: { } fileKey }
            || await _store.Files.GetAsync(vendor.ToString(), fileKey, ct) is not { } stored)
        {
            return NoWork;
        }

        var file = stored.Value;
        long? downloaded = null;
        DriverSyncRunItem? failed = null;
        if (file.Status != DriverFileStatus.Verified)
        {
            var now = _time.GetUtcNow();
            await _store.Files.UpsertAsync(vendor.ToString(), fileKey, file with { Status = DriverFileStatus.Downloading, AttemptedAt = now }, ct);
            var outcome = file.SourceUrl is null
                ? DriverDownloadOutcome.Fail("The vendor catalog lists no download address.")
                : await _storage.DownloadAsync(file.SourceUrl, file.BlobName, file.Sha256, file.Size > 0 ? file.Size : null, ct);
            file = file with
            {
                Status = outcome.Succeeded ? DriverFileStatus.Verified : DriverFileStatus.Failed,
                VerifiedAt = outcome.Succeeded ? _time.GetUtcNow() : null,
                FailureReason = outcome.Reason,
                AttemptedAt = now,
            };
            await _store.Files.UpsertAsync(vendor.ToString(), fileKey, file, ct);
            if (outcome.Succeeded)
            {
                downloaded = outcome.Size;
            }
            else
            {
                LogFileFailed(_logger, fileKey, outcome.Reason);
                if (message.RunId is { } runId)
                {
                    failed = await _runs.AddItemAsync(runId, now, DownloadFailure(vendor, file, outcome.Reason), ct);
                }
            }
        }

        return new StepResult(await BuildsWaitingOnAsync(file.BlobName, ct), DownloadedBytes: downloaded, Items: failed is null ? [] : [failed]);
    }

    // A download serves every model whose set lists the file, so its item names no model.
    private static DriverSyncRunItem DownloadFailure(DriverVendor vendor, DriverFileRecord file, string? reason) => new()
    {
        ComponentName = file.ComponentName,
        NewVersion = file.Version,
        Category = file.Category,
        Outcome = DriverSyncItemOutcome.Failed,
        Reason = reason,
        Vendor = vendor,
    };

    // A pending set is built again once none of its files is still downloading.
    private async Task<IReadOnlyList<DriverWorkMessage>> BuildsWaitingOnAsync(string blobName, CancellationToken ct)
    {
        var builds = new List<DriverWorkMessage>();
        foreach (var (pair, row) in await _pointers.ListAllAsync(ct))
        {
            if (row.Value.PendingVersion is not { } pending || !TryParsePair(pair, out var modelId, out var release))
            {
                continue;
            }

            var document = await _sets.GetAsync(modelId, release, pending, ct);
            if (document is null || !document.Items.Any(i => i.BlobName == blobName))
            {
                continue;
            }

            if (!await AnyDownloadingAsync(modelId, document, ct))
            {
                builds.Add(new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = modelId, Release = release });
            }
        }

        return builds;
    }

    private async Task<bool> AnyDownloadingAsync(Guid modelId, DriverSetDocument document, CancellationToken ct)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        foreach (var item in document.Items)
        {
            var vendor = item.Origin == DriverComponentOrigin.Custom || model is null ? null : model.Vendor.ToString();
            if (vendor is not null && (await _store.Files.GetAsync(vendor, item.FileKey, ct))?.Value.Status == DriverFileStatus.Downloading)
            {
                return true;
            }
        }

        return false;
    }

    // A download that finished while this build step was still open had its rebuild dropped as a
    // duplicate step, so the pending set is checked once more and built outside the run.
    private async Task RebuildSettledPendingAsync(DriverWorkMessage build, CancellationToken ct)
    {
        if (build is not { ModelId: { } modelId, Release: { } release }
            || (await _pointers.GetAsync(modelId, release, ct))?.Value.PendingVersion is not { } pending
            || await _sets.GetAsync(modelId, release, pending, ct) is not { } document
            || await AnyDownloadingAsync(modelId, document, ct))
        {
            return;
        }

        await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = modelId, Release = release }, TimeSpan.Zero, ct);
    }

    private async Task<StepResult> BuildSetAsync(DriverWorkMessage message, CancellationToken ct)
    {
        if (message is not { ModelId: { } modelId, Release: { } release }
            || (await _models.GetAsync(modelId, ct))?.Value is not { State: DriverModelState.Active } model
            || !model.Releases.Contains(release))
        {
            return NoWork;
        }

        var now = _time.GetUtcNow();
        var row = await _pointers.GetOrCreateAsync(modelId, release, ct);
        var pointer = row.Value;

        // The document this build may write is read now, so a concurrent build that writes it first wins and this one retries.
        var candidateVersion = pointer.PendingVersion ?? pointer.LastVersion + 1;
        var documentETag = await _sets.GetETagAsync(modelId, release, candidateVersion, ct);
        if (message.RunId is null)
        {
            // Edits wait 2 minutes so several changes make one set; Build now clears the wait.
            if (pointer.BuildAfter is { } after && after > now)
            {
                await _work.EnqueueAsync(message, after - now, ct);
                return NoWork;
            }

            if (!pointer.Dirty && pointer.PendingVersion is null && pointer.CurrentVersion is not null)
            {
                return NoWork;
            }
        }

        var run = message.RunId is { } runId ? (await _runs.GetAsync(runId, ct))?.Value : null;
        var pair = DriverTables.Pair(modelId, release);
        var vendor = model.Vendor.ToString();
        var components = await _store.Components.ListPartitionAsync(pair, ct);
        var overrides = (await _store.Overrides.ListPartitionAsync(pair, ct)).ToDictionary(r => r.RowKey, r => r.Value.Action, StringComparer.Ordinal);
        var rules = (await _store.Rules.ListPartitionAsync(DriverStore.RulePartition, ct)).Select(r => r.Value).ToList();
        var current = pointer.CurrentVersion is { } currentVersion ? await _sets.GetAsync(modelId, release, currentVersion, ct) : null;
        var currentItems = (current?.Items ?? []).ToDictionary(i => i.LineageKey, StringComparer.Ordinal);
        var rollBacks = (await _store.RollBacks.ListPartitionAsync(pair, ct)).ToDictionary(r => r.RowKey, r => r.Value, StringComparer.Ordinal);
        List<DriverComponentVersionRecord> history = rollBacks.Count == 0 ? [] : (await _store.ComponentVersions.ListPartitionAsync(pair, ct)).Select(r => r.Value).ToList();
        var rolled = new Dictionary<string, (string? ChangedBy, string? Url)>(StringComparer.Ordinal);
        var modelKey = modelId.ToString("D");
        var replaced = (await _store.Replacements.ListPartitionAsync(modelKey, ct)).Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);

        var candidates = new List<DriverSetCandidate>();
        var downloads = new List<DriverWorkMessage>();
        foreach (var (lineage, component) in components.Select(c => (c.RowKey, c.Value)))
        {
            // A vendor component replaced by a custom component on this model is left out.
            if (component.State == DriverComponentState.WaitingForApproval || replaced.Contains(lineage))
            {
                continue;
            }

            var input = new DriverPolicyInput(model.Vendor, component.ComponentId, component.Name, component.Category, component.AllowedActions, component.DefaultAction);
            var action = DriverPolicyResolver.Resolve(input, overrides.TryGetValue(lineage, out var chosen) ? chosen : null, rules).Action;
            if (action == DriverAction.Skip)
            {
                continue;
            }

            currentItems.TryGetValue(lineage, out var lastVerified);

            // A rolled back component uses the held version, whatever the vendor publishes now.
            if (rollBacks.TryGetValue(lineage, out var rollBack)
                && history.Where(v => v.LineageKey == lineage && v.Version == rollBack.Version && v.Origin != DriverComponentOrigin.Custom).MaxBy(v => v.From) is { } held)
            {
                var heldItem = HeldItem(model.Vendor, component, held, action) with { LineageKey = lineage };
                rolled[lineage] = ($"Roll back by {rollBack.ChangedBy}", held.Url);
                var heldStatus = await EnsureFileAsync(vendor, heldItem, held.Url, run, downloads, ct);
                candidates.Add(new DriverSetCandidate(lineage, action, new DriverSetFileCandidate(heldItem, heldStatus), lastVerified));
                continue;
            }

            if (component.State == DriverComponentState.Withdrawn)
            {
                candidates.Add(new DriverSetCandidate(lineage, action, null, lastVerified));
                continue;
            }

            var item = ItemFor(model.Vendor, component, action) with { LineageKey = lineage };
            var status = await EnsureFileAsync(vendor, item, component.Url, run, downloads, ct);
            candidates.Add(new DriverSetCandidate(lineage, action, new DriverSetFileCandidate(item, status), lastVerified));
        }

        // Custom components added to this model; their packages are stored when uploaded.
        foreach (var (customKey, target) in await _store.CustomTargets.ListAllAsync(ct))
        {
            if (target.RowKey == modelKey && (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, customKey, ct))?.Value is { } custom)
            {
                var customItem = CustomItem(custom);
                rolled[customItem.LineageKey] = ($"Custom component by {custom.UploadedBy}", null);
                currentItems.TryGetValue(customItem.LineageKey, out var lastCustom);
                candidates.Add(new DriverSetCandidate(customItem.LineageKey, customItem.Action, new DriverSetFileCandidate(customItem, DriverFileStatus.Verified), lastCustom));
            }
        }

        var plan = DriverSetBuilder.Plan(candidates);
        var decision = DriverSetBuilder.Decide(pointer, plan, now);
        var next = decision.Next with { Dirty = false, BuildAfter = null, PendingChanges = 0 };
        if (decision.Version is { } version)
        {
            if (version != candidateVersion
                || !await _sets.SaveAsync(new DriverSetDocument { ModelId = modelId, Release = release, Version = version, Fingerprint = plan.Fingerprint, BuiltAt = now, Items = plan.Items }, documentETag, ct))
            {
                throw new InvalidOperationException($"Driver set {version} of {pair} was written by another build.");
            }
        }
        else if (pointer.PendingVersion is { } abandoned)
        {
            await _sets.DeleteAsync(modelId, release, abandoned, ct);
        }

        if (!await _pointers.TryUpdateAsync(modelId, release, next, row.ETag, ct))
        {
            // Another build moved the pointer first; the host retries this message.
            throw new InvalidOperationException($"The driver set pointer of {pair} changed during the build.");
        }

        if (decision.Version is { } built)
        {
            await RecordBuildAsync(model, release, built, decision.Kind, plan, currentItems, pointer.CurrentVersion is null, run is not null, now, ct);
        }

        if (decision.Kind == DriverSetDecisionKind.NewCurrent)
        {
            await RecordVersionsAsync(model, release, decision.Version!.Value, pointer.CurrentVersion, plan.Items, components.ToDictionary(c => c.RowKey, c => c.Value, StringComparer.Ordinal), rolled, now, ct);
            LogSetCurrent(_logger, decision.Version!.Value, pair);
        }

        return new StepResult(downloads, NewSet: decision.Kind == DriverSetDecisionKind.NewCurrent);
    }

    // The file record is written before its download is queued, so the Downloads tab and the
    // build both see it as downloading. A sync tries again a file that failed before it started.
    private async Task<DriverFileStatus> EnsureFileAsync(string vendor, DriverSetItem item, string? url, DriverSyncRunRecord? run, List<DriverWorkMessage> downloads, CancellationToken ct)
    {
        var existing = (await _store.Files.GetAsync(vendor, item.FileKey, ct))?.Value;
        var retry = existing is { Status: DriverFileStatus.Failed } && run is not null && (existing.AttemptedAt ?? DateTimeOffset.MinValue) < run.StartedAt;
        if (existing is not null && !retry)
        {
            return existing.Status;
        }

        await _store.Files.UpsertAsync(vendor, item.FileKey, new DriverFileRecord
        {
            BlobName = item.BlobName,
            Size = item.Size,
            Sha256 = item.Sha256,
            Status = DriverFileStatus.Downloading,
            SourceUrl = url,
            FileName = item.FileName,
            ComponentName = item.Name,
            Version = item.Version,
            Category = item.Category,
            AttemptedAt = existing?.AttemptedAt,
        }, ct);
        downloads.Add(new DriverWorkMessage { Kind = DriverWorkKind.DownloadFile, Vendor = Enum.Parse<DriverVendor>(vendor), FileKey = item.FileKey });
        return DriverFileStatus.Downloading;
    }

    private async Task RecordBuildAsync(DriverModelRecord model, WindowsRelease release, int version, DriverSetDecisionKind kind, DriverSetBuildPlan plan, Dictionary<string, DriverSetItem> currentItems, bool firstSet, bool bySync, DateTimeOffset now, CancellationToken ct)
    {
        var pair = DriverTables.Pair(model.ModelId, release);
        var key = version.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var existing = (await _store.SetBuilds.GetAsync(pair, key, ct))?.Value;
        var reusedFiles = currentItems.Values.Select(i => i.BlobName).ToHashSet(StringComparer.Ordinal);
        var newItems = plan.Items.Where(i => !reusedFiles.Contains(i.BlobName)).ToList();
        await _store.SetBuilds.UpsertAsync(pair, key, new DriverSetBuildRecord
        {
            Trigger = existing?.Trigger ?? (firstSet ? "Onboarding" : bySync ? "Sync" : "Policy"),
            BuiltAt = existing?.BuiltAt ?? now,
            ReadyAt = kind == DriverSetDecisionKind.NewCurrent ? now : null,
            Components = plan.Items.Count,
            NewComponents = newItems.Count,
            ReusedComponents = plan.Items.Count - newItems.Count,
            SizeBytes = plan.Items.Sum(i => i.Size),
            DownloadedBytes = kind == DriverSetDecisionKind.NewCurrent ? newItems.Sum(i => i.Size) : 0,
            FailedComponents = plan.FellBack.Count,
        }, ct);
    }

    // One history row per version a component has had in the current set (FR-089): a version
    // new to the set opens a row, and a row whose version left the set is closed.
    private async Task RecordVersionsAsync(DriverModelRecord model, WindowsRelease release, int version, int? previousVersion, IReadOnlyList<DriverSetItem> items, Dictionary<string, DriverComponentRecord> components, Dictionary<string, (string? ChangedBy, string? Url)> rolled, DateTimeOffset now, CancellationToken ct)
    {
        var pair = DriverTables.Pair(model.ModelId, release);
        var open = (await _store.ComponentVersions.ListPartitionAsync(pair, ct))
            .Where(r => r.Value.LastSet is null)
            .GroupBy(r => r.Value.LineageKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var changedBy = previousVersion is null && model.CompletedBy is { } upn ? $"Onboarding by {upn}" : "Sync";
        var inSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            inSet.Add(item.LineageKey);
            var rows = open.GetValueOrDefault(item.LineageKey) ?? [];
            if (rows.Any(r => r.Value.ComponentId == item.ComponentId && r.Value.Version == item.Version))
            {
                continue;
            }

            foreach (var stale in rows)
            {
                await _store.ComponentVersions.UpsertAsync(pair, stale.RowKey, stale.Value with { LastSet = previousVersion, To = now }, ct);
            }

            components.TryGetValue(item.LineageKey, out var component);
            var heldBy = rolled.TryGetValue(item.LineageKey, out var held) ? held : default;
            await _store.ComponentVersions.UpsertAsync(pair, $"{item.LineageKey}_{DriverTables.InvertedTicks(now)}", new DriverComponentVersionRecord
            {
                LineageKey = item.LineageKey,
                ComponentId = item.ComponentId,
                Version = item.Version ?? string.Empty,
                FirstSet = version,
                From = now,
                ChangedBy = item.Origin is DriverComponentOrigin.RolledBack or DriverComponentOrigin.Custom ? heldBy.ChangedBy ?? changedBy : changedBy,
                Url = item.Origin switch
                {
                    DriverComponentOrigin.Custom => null,
                    DriverComponentOrigin.RolledBack => heldBy.Url,
                    _ => component?.Url,
                },
                Sha256 = item.Sha256,
                Size = item.Size,
                FileName = item.FileName,
                Origin = item.Origin,
            }, ct);
            if (component is not null && item.Origin == DriverComponentOrigin.Vendor)
            {
                await _store.Components.UpsertAsync(pair, item.LineageKey, component with { VerifiedVersion = item.Version, VerifiedFile = item.FileKey }, ct);
            }
        }

        foreach (var (lineage, rows) in open.Where(o => !inSet.Contains(o.Key)))
        {
            foreach (var stale in rows)
            {
                await _store.ComponentVersions.UpsertAsync(pair, stale.RowKey, stale.Value with { LastSet = previousVersion, To = now }, ct);
            }
        }
    }

    private async Task FinalizeRunAsync(Guid? runId, CancellationToken ct)
    {
        if (runId is not { } id || (await _runs.GetAsync(id, ct))?.Value is not { Result: DriverSyncResult.Running } run)
        {
            return;
        }

        // The normal FinalizeRun is only sent when the count reaches zero, so one that finds work
        // outstanding is the 2-hour watchdog.
        if (run.Outstanding > 0)
        {
            await EndRunAsync(id, DriverSyncResult.Failed, TimedOutReason, ct);
            return;
        }

        var items = await _runs.ListItemsAsync(id, ct);
        var failedItems = items.Count(i => i.Outcome == DriverSyncItemOutcome.Failed);
        var failedVendors = run.Vendors.Count(v => !v.Processed);
        var processed = run.Vendors.Any(v => v.Processed) || items.Any(i => i.Outcome != DriverSyncItemOutcome.Failed);
        var result = failedItems == 0 && failedVendors == 0
            ? DriverSyncResult.Completed
            : processed ? DriverSyncResult.PartlyFailed : DriverSyncResult.Failed;
        await EndRunAsync(id, result, null, ct);
    }

    private async Task EndRunAsync(Guid runId, DriverSyncResult result, string? reason, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (reason is not null)
        {
            await _runs.AddItemAsync(runId, now, new DriverSyncRunItem { Outcome = DriverSyncItemOutcome.Failed, Reason = reason }, ct);
        }

        var items = await _runs.ListItemsAsync(runId, ct);
        var counts = items.GroupBy(i => i.Outcome).ToDictionary(g => g.Key, g => g.Count());
        var ended = await UpdateRunAsync(runId, r => r.Result != DriverSyncResult.Running ? r : r with { Result = result, FinishedAt = now, Counts = counts }, ct);
        await _runs.ReleaseAsync(runId, ct);
        await _runs.DeleteStepsAsync(runId, ct);
        if (ended is { Trigger: not DriverSyncTrigger.ModelCheck })
        {
            await CleanUpAsync(ct);
        }

        await _runs.DeleteOlderThanAsync(now - History, ct);
        foreach (var (pair, build) in await _store.SetBuilds.ListAllAsync(ct))
        {
            if (build.Value.BuiltAt < now - History)
            {
                await _store.SetBuilds.DeleteAsync(pair, build.RowKey, ct: ct);
            }
        }

        LogRunEnded(_logger, runId, result);
    }

    /// <summary>
    /// Deletes files and set documents nothing refers to any more (research R16): a file stays
    /// while a current, previous or pending set of any model, or a set pinned to an active
    /// session, lists it, or while it is one of the <c>PreviousVersionsToKeep</c> vendor versions
    /// directly before a component's current one. Files still downloading are left alone.
    /// </summary>
    public async Task CleanUpAsync(CancellationToken ct = default)
    {
        var keep = await KeptSetsAsync(ct);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in await _sets.ListAsync(ct))
        {
            if (!keep.Contains(document))
            {
                await _sets.DeleteAsync(document.ModelId, document.Release, document.Version, ct);
                continue;
            }

            foreach (var item in (await _sets.GetAsync(document.ModelId, document.Release, document.Version, ct))?.Items ?? [])
            {
                referenced.Add(item.BlobName);
            }
        }

        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        foreach (var blobs in (await KeptPreviousVersionsAsync(settings.PreviousVersionsToKeep, ct)).Values)
        {
            referenced.UnionWith(blobs);
        }

        // A component roll back keeps its held version until it ends.
        var vendors = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId, m => m.Vendor);
        var history = (await _store.ComponentVersions.ListAllAsync(ct)).ToLookup(r => r.PartitionKey, r => r.Row.Value, StringComparer.Ordinal);
        foreach (var (pair, rollBack) in await _store.RollBacks.ListAllAsync(ct))
        {
            if (TryParsePair(pair, out var modelId, out _) && vendors.TryGetValue(modelId, out var vendor))
            {
                referenced.UnionWith(history[pair].Where(v => v.LineageKey == rollBack.RowKey && v.Version == rollBack.Value.Version).Select(v => HeldBlob(vendor, v)));
            }
        }

        foreach (var (vendor, file) in await _store.Files.ListAllAsync(ct))
        {
            if (file.Value.Status != DriverFileStatus.Downloading && !referenced.Contains(file.Value.BlobName))
            {
                await _storage.DeleteAsync(file.Value.BlobName, ct);
                await _store.Files.DeleteAsync(vendor, file.RowKey, ct: ct);
            }
        }

        // A custom package stays while its component, a recent upload or a kept set refers to it.
        // An upload never used for a component is dropped a day after it started.
        referenced.UnionWith((await _store.CustomComponents.ListPartitionAsync(DriverStore.CustomPartition, ct)).Select(r => r.Value.BlobName));
        var uploadCutoff = _time.GetUtcNow() - StaleUploadAge;
        foreach (var upload in await _store.CustomUploads.ListPartitionAsync(DriverStore.UploadPartition, ct))
        {
            if (upload.Value.CustomId is null && upload.Value.StartedAt < uploadCutoff)
            {
                await _store.CustomUploads.DeleteAsync(DriverStore.UploadPartition, upload.RowKey, ct: ct);
                continue;
            }

            referenced.Add(upload.Value.BlobName);
            if (upload.Value.StagingBlobName is { } staging)
            {
                referenced.Add(staging);
            }
        }

        foreach (var blob in await _storage.ListAsync("custom/", ct))
        {
            if (!referenced.Contains(blob))
            {
                await _storage.DeleteAsync(blob, ct);
            }
        }
    }

    /// <summary>Set documents clean-up keeps: current, previous and pending sets of every pair, and every set pinned to an active session.</summary>
    public async Task<HashSet<(Guid ModelId, WindowsRelease Release, int Version)>> KeptSetsAsync(CancellationToken ct = default)
    {
        var keep = new HashSet<(Guid, WindowsRelease, int)>();
        foreach (var (pair, row) in await _pointers.ListAllAsync(ct))
        {
            if (TryParsePair(pair, out var modelId, out var release))
            {
                foreach (var version in new[] { row.Value.CurrentVersion, row.Value.PreviousVersion, row.Value.PendingVersion })
                {
                    if (version is { } v)
                    {
                        keep.Add((modelId, release, v));
                    }
                }
            }
        }

        await foreach (var session in _sessions.QueryActiveAsync(ct))
        {
            if (session is { DriverModelId: { } modelId, DriverRelease: { } release, DriverSetVersion: { } version })
            {
                keep.Add((modelId, release, version));
            }
        }

        return keep;
    }

    /// <summary>
    /// Per pair, the files of the <paramref name="keep"/> vendor versions that came directly before
    /// each component's current version (FR-102), newest first. Custom packages do not count.
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>> KeptPreviousVersionsAsync(int keep, CancellationToken ct = default)
    {
        var kept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (keep <= 0)
        {
            return kept;
        }

        var vendors = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId, m => m.Vendor);
        foreach (var pairRows in (await _store.ComponentVersions.ListAllAsync(ct)).GroupBy(r => r.PartitionKey, StringComparer.Ordinal))
        {
            if (!TryParsePair(pairRows.Key, out var modelId, out _) || !vendors.TryGetValue(modelId, out var vendor))
            {
                continue;
            }

            var blobs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lineage in pairRows.Select(r => r.Row.Value).GroupBy(v => v.LineageKey, StringComparer.Ordinal))
            {
                foreach (var version in lineage.Where(v => v.LastSet is not null && v.Origin != DriverComponentOrigin.Custom).OrderByDescending(v => v.From).Take(keep))
                {
                    blobs.Add($"files/{vendor}/{version.ComponentId}/{version.Version}/{version.FileName ?? $"{version.ComponentId}.exe"}");
                }
            }

            kept[pairRows.Key] = blobs;
        }

        return kept;
    }

    private async Task<DriverSyncRunRecord?> UpdateRunAsync(Guid runId, Func<DriverSyncRunRecord, DriverSyncRunRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _runs.GetAsync(runId, ct);
            if (row is null)
            {
                return null;
            }

            var next = change(row.Value);
            if (await _runs.TryUpdateAsync(next, row.ETag, ct))
            {
                return next;
            }
        }

        throw new InvalidOperationException($"Driver sync run {runId} kept changing during an update.");
    }

    private async Task UpdateSettingsAsync(Func<DriverSyncSettingsRecord, DriverSyncSettingsRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _store.GetSyncSettingsAsync(ct);
            var next = change(row.Value);
            var saved = row.ETag == default
                ? await _store.SyncSettings.TryAddAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, ct)
                : await _store.SyncSettings.TryUpdateAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, next, row.ETag, ct);
            if (saved)
            {
                return;
            }
        }

        throw new InvalidOperationException("The driver sync settings kept changing during an update.");
    }

    private async Task UpdateModelAsync(Guid modelId, Func<DriverModelRecord, DriverModelRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _models.GetAsync(modelId, ct);
            if (row is null || await _models.TryUpdateAsync(change(row.Value), row.ETag, ct))
            {
                return;
            }
        }

        throw new InvalidOperationException($"Driver model {modelId} kept changing during an update.");
    }

    /// <summary>Splits a pair key into model and release.</summary>
    public static bool TryParsePair(string pair, out Guid modelId, out WindowsRelease release)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var split = pair.IndexOf('_', StringComparison.Ordinal);
        release = default;
        modelId = default;
        return split > 0 && Guid.TryParse(pair.AsSpan(0, split), out modelId) && Enum.TryParse(pair[(split + 1)..], out release);
    }

    /// <summary>Display name of a vendor.</summary>
    public static string VendorName(DriverVendor vendor) => vendor switch
    {
        DriverVendor.Hp => "HP",
        _ => vendor.ToString(),
    };

    private static DriverSyncRunItem Item(DriverModelRecord model, WindowsRelease release, string? lineage, string? name, string? oldVersion, string? newVersion, DriverSyncItemOutcome outcome) => new()
    {
        ModelId = model.ModelId,
        ModelName = model.DisplayName,
        Release = release,
        LineageKey = lineage,
        ComponentName = name,
        OldVersion = oldVersion,
        NewVersion = newVersion,
        Outcome = outcome,
        Vendor = model.Vendor,
    };

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset now) => a > now ? a : null;

    // What a step did beyond its children, counted on the run when the step completes.
    private sealed record StepResult(IReadOnlyList<DriverWorkMessage> Children, bool NewSet = false, long? DownloadedBytes = null, IReadOnlyList<DriverSyncRunItem>? Items = null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver sync run {RunId} held the run lock past its watchdog; it was ended.")]
    private static partial void LogStaleLockReleased(ILogger logger, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver sync run {RunId} ({Trigger}) started for {VendorCount} vendors.")]
    private static partial void LogRunStarted(ILogger logger, Guid runId, DriverSyncTrigger trigger, int vendorCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver sync run {RunId} ended: {Result}.")]
    private static partial void LogRunEnded(ILogger logger, Guid runId, DriverSyncResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver work message of kind {Kind} has no handler here and was dropped.")]
    private static partial void LogNoHandler(ILogger logger, DriverWorkKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver catalog for model {ModelId} {Release} could not be read: {Reason}")]
    private static partial void LogCatalogFailed(ILogger logger, Guid modelId, WindowsRelease release, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver file {FileKey} failed: {Reason}")]
    private static partial void LogFileFailed(ILogger logger, string fileKey, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver set {Version} of {Pair} is current.")]
    private static partial void LogSetCurrent(ILogger logger, int version, string pair);
}

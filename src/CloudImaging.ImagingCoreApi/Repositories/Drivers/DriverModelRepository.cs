using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.ImagingCoreApi.Repositories.Drivers;

/// <summary>
/// Onboarded models (PK <c>model</c>, RK model id) and their match key reservations (PK vendor,
/// RK match key). A key is reserved by inserting its row, so two models racing for the same HP
/// system ID or Dell SKU cannot both win (FR-007). Drafts reserve keys like active models.
/// </summary>
public sealed class DriverModelRepository
{
    private const string ModelPartition = "model";
    private readonly DriverJsonTable<DriverModelRecord> _models;
    private readonly DriverJsonTable<DriverMatchKeyRecord> _keys;

    /// <param name="tables">Table service.</param>
    public DriverModelRepository(TableServiceClient tables)
    {
        _models = new(tables, DriverTables.Models);
        _keys = new(tables, DriverTables.MatchKeys);
    }

    /// <summary>Creates both tables.</summary>
    public async Task EnsureTablesExistAsync(CancellationToken ct = default)
    {
        await _models.EnsureExistsAsync(ct);
        await _keys.EnsureExistsAsync(ct);
    }

    /// <summary>Reads one model.</summary>
    public Task<Stored<DriverModelRecord>?> GetAsync(Guid modelId, CancellationToken ct = default) =>
        _models.GetAsync(ModelPartition, modelId.ToString("D"), ct);

    /// <summary>Reads every model.</summary>
    public async Task<List<DriverModelRecord>> ListAsync(CancellationToken ct = default) =>
        (await _models.ListPartitionAsync(ModelPartition, ct)).Select(row => row.Value).ToList();

    /// <summary>Returns the model that owns a match key, or null.</summary>
    public async Task<Guid?> GetOwnerAsync(DriverVendor vendor, string matchKey, CancellationToken ct = default) =>
        (await _keys.GetAsync(vendor.ToString(), DriverTables.NormalizeKey(matchKey), ct))?.Value.ModelId;

    /// <summary>
    /// Reserves every key for <paramref name="modelId"/>. When one is owned by another model, the
    /// keys reserved so far are released and that key and its owner are returned.
    /// </summary>
    public async Task<(string Key, Guid Owner)?> TryReserveKeysAsync(DriverVendor vendor, IEnumerable<string> matchKeys, Guid modelId, CancellationToken ct = default)
    {
        var reserved = new List<string>();
        foreach (var key in matchKeys.Select(DriverTables.NormalizeKey).Distinct(StringComparer.Ordinal))
        {
            if (await _keys.TryAddAsync(vendor.ToString(), key, new DriverMatchKeyRecord(modelId), ct))
            {
                reserved.Add(key);
                continue;
            }

            var owner = await GetOwnerAsync(vendor, key, ct);
            if (owner == modelId)
            {
                continue;
            }

            foreach (var done in reserved)
            {
                await _keys.DeleteAsync(vendor.ToString(), done, ct: ct);
            }

            return (key, owner ?? Guid.Empty);
        }

        return null;
    }

    /// <summary>Releases a model's keys.</summary>
    public async Task ReleaseKeysAsync(DriverVendor vendor, IEnumerable<string> matchKeys, Guid modelId, CancellationToken ct = default)
    {
        foreach (var key in matchKeys.Select(DriverTables.NormalizeKey))
        {
            if (await GetOwnerAsync(vendor, key, ct) == modelId)
            {
                await _keys.DeleteAsync(vendor.ToString(), key, ct: ct);
            }
        }
    }

    /// <summary>Inserts a new model; false when the id exists.</summary>
    public Task<bool> TryAddAsync(DriverModelRecord model, CancellationToken ct = default) =>
        _models.TryAddAsync(ModelPartition, model.ModelId.ToString("D"), model, ct);

    /// <summary>Replaces a model if it still has <paramref name="etag"/>.</summary>
    public Task<bool> TryUpdateAsync(DriverModelRecord model, ETag etag, CancellationToken ct = default) =>
        _models.TryUpdateAsync(ModelPartition, model.ModelId.ToString("D"), model, etag, ct);

    /// <summary>Deletes a model row.</summary>
    public Task<bool> DeleteAsync(Guid modelId, CancellationToken ct = default) =>
        _models.DeleteAsync(ModelPartition, modelId.ToString("D"), ct: ct);
}

/// <summary>
/// Set pointers of a model and release (PK pair, RK <c>pointer</c>). Every write is conditional on
/// the ETag the pointer was read with, so two builds cannot both move it.
/// </summary>
public sealed class DriverSetPointerRepository(TableServiceClient tables)
{
    private const string PointerRow = "pointer";
    private readonly DriverJsonTable<DriverSetPointerRecord> _pointers = new(tables, DriverTables.SetPointers);

    /// <summary>Creates the table.</summary>
    public Task EnsureTableExistsAsync(CancellationToken ct = default) => _pointers.EnsureExistsAsync(ct);

    /// <summary>Reads a pointer, or null for a pair without one.</summary>
    public Task<Stored<DriverSetPointerRecord>?> GetAsync(Guid modelId, WindowsRelease release, CancellationToken ct = default) =>
        _pointers.GetAsync(DriverTables.Pair(modelId, release), PointerRow, ct);

    /// <summary>Reads a pointer, creating an empty one when missing.</summary>
    public async Task<Stored<DriverSetPointerRecord>> GetOrCreateAsync(Guid modelId, WindowsRelease release, CancellationToken ct = default)
    {
        var existing = await GetAsync(modelId, release, ct);
        if (existing is not null)
        {
            return existing;
        }

        await _pointers.TryAddAsync(DriverTables.Pair(modelId, release), PointerRow, new DriverSetPointerRecord(), ct);
        return (await GetAsync(modelId, release, ct))!;
    }

    /// <summary>Writes a pointer if it still has <paramref name="etag"/>; false when it changed.</summary>
    public Task<bool> TryUpdateAsync(Guid modelId, WindowsRelease release, DriverSetPointerRecord record, ETag etag, CancellationToken ct = default) =>
        _pointers.TryUpdateAsync(DriverTables.Pair(modelId, release), PointerRow, record, etag, ct);

    /// <summary>Deletes a pointer.</summary>
    public Task<bool> DeleteAsync(Guid modelId, WindowsRelease release, CancellationToken ct = default) =>
        _pointers.DeleteAsync(DriverTables.Pair(modelId, release), PointerRow, ct: ct);

    /// <summary>Reads every pointer with its pair key.</summary>
    public Task<List<(string PartitionKey, Stored<DriverSetPointerRecord> Row)>> ListAllAsync(CancellationToken ct = default) =>
        _pointers.ListAllAsync(ct);
}

/// <summary>
/// Runs (PK <c>run</c>, RK inverted start ticks), an id index (PK <c>runid</c>), their items (PK
/// run id, RK time-ordered) and the <c>active</c> lock row. A run is created by inserting the lock
/// first, so only one run of any trigger is ever active (FR-033, research R7). Each queue message of
/// a run has a step marker (PK <c>steps_</c>run id) from when it is counted until it is done.
/// </summary>
public sealed class DriverSyncRunRepository
{
    private const string RunPartition = "run";
    private const string IndexPartition = "runid";
    private const string QueuePartition = "queue";
    private const string ActiveRow = "active";
    private readonly DriverJsonTable<DriverSyncRunRecord> _runs;
    private readonly DriverJsonTable<DriverActiveRunRecord> _active;
    private readonly DriverJsonTable<DriverQueuedRunRecord> _queue;
    private readonly DriverJsonTable<Contracts.Models.Drivers.DriverSyncRunItem> _items;
    private readonly DriverJsonTable<DriverRunStepRecord> _steps;

    /// <param name="tables">Table service.</param>
    public DriverSyncRunRepository(TableServiceClient tables)
    {
        _runs = new(tables, DriverTables.SyncRuns);
        _active = new(tables, DriverTables.SyncRuns);
        _queue = new(tables, DriverTables.SyncRuns);
        _items = new(tables, DriverTables.SyncRunItems);
        _steps = new(tables, DriverTables.SyncRunItems);
    }

    /// <summary>Creates both tables.</summary>
    public async Task EnsureTablesExistAsync(CancellationToken ct = default)
    {
        await _runs.EnsureExistsAsync(ct);
        await _items.EnsureExistsAsync(ct);
    }

    /// <summary>Takes the lock for a new run; false while another run holds it.</summary>
    public Task<bool> TryAcquireAsync(Guid runId, DateTimeOffset startedAt, CancellationToken ct = default) =>
        _active.TryAddAsync(RunPartition, ActiveRow, new DriverActiveRunRecord(runId, startedAt), ct);

    /// <summary>The active run, or null.</summary>
    public async Task<DriverActiveRunRecord?> GetActiveAsync(CancellationToken ct = default) =>
        (await _active.GetAsync(RunPartition, ActiveRow, ct))?.Value;

    /// <summary>Releases the lock if <paramref name="runId"/> holds it.</summary>
    public async Task ReleaseAsync(Guid runId, CancellationToken ct = default)
    {
        var active = await _active.GetAsync(RunPartition, ActiveRow, ct);
        if (active?.Value.RunId == runId)
        {
            await _active.DeleteAsync(RunPartition, ActiveRow, active.ETag, ct);
        }
    }

    /// <summary>Row key of a run.</summary>
    public static string RunKey(DriverSyncRunRecord run) => $"{DriverTables.InvertedTicks(run.StartedAt)}_{run.RunId:N}";

    /// <summary>Inserts a run and its id index row.</summary>
    public async Task<bool> TryAddAsync(DriverSyncRunRecord run, CancellationToken ct = default)
    {
        await _active.UpsertAsync(IndexPartition, run.RunId.ToString("N"), new DriverActiveRunRecord(run.RunId, run.StartedAt), ct);
        return await _runs.TryAddAsync(RunPartition, RunKey(run), run, ct);
    }

    /// <summary>Reads a run.</summary>
    public async Task<Stored<DriverSyncRunRecord>?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var index = await _active.GetAsync(IndexPartition, runId.ToString("N"), ct);
        return index is null
            ? null
            : await _runs.GetAsync(RunPartition, $"{DriverTables.InvertedTicks(index.Value.StartedAt)}_{runId:N}", ct);
    }

    /// <summary>Replaces a run if it still has <paramref name="etag"/>.</summary>
    public Task<bool> TryUpdateAsync(DriverSyncRunRecord run, ETag etag, CancellationToken ct = default) =>
        _runs.TryUpdateAsync(RunPartition, RunKey(run), run, etag, ct);

    /// <summary>Every run, newest first.</summary>
    public async Task<List<DriverSyncRunRecord>> ListAsync(CancellationToken ct = default) =>
        (await _runs.ListPartitionAsync(RunPartition, ct)).Where(row => row.RowKey != ActiveRow).Select(row => row.Value).ToList();

    /// <summary>Appends an item to a run; items sort in the order they were added. Returns the item as stored.</summary>
    public async Task<Contracts.Models.Drivers.DriverSyncRunItem> AddItemAsync(Guid runId, DateTimeOffset at, Contracts.Models.Drivers.DriverSyncRunItem item, CancellationToken ct = default)
    {
        var stamped = item with { At = at };
        await _items.UpsertAsync(runId.ToString("N"), $"{at.UtcTicks:D19}_{Guid.NewGuid():N}", stamped, ct);
        return stamped;
    }

    /// <summary>Items of a run in order.</summary>
    public async Task<List<Contracts.Models.Drivers.DriverSyncRunItem>> ListItemsAsync(Guid runId, CancellationToken ct = default) =>
        (await _items.ListPartitionAsync(runId.ToString("N"), ct)).Select(row => row.Value).ToList();

    /// <summary>Marks a step as counted; false when its marker already exists.</summary>
    public Task<bool> TryAddStepAsync(Guid runId, string step, CancellationToken ct = default) =>
        _steps.TryAddAsync(StepPartition(runId), StepKey(step), new DriverRunStepRecord(step), ct);

    /// <summary>True while a counted step is not done.</summary>
    public async Task<bool> IsStepOpenAsync(Guid runId, string step, CancellationToken ct = default) =>
        await _steps.GetAsync(StepPartition(runId), StepKey(step), ct) is not null;

    /// <summary>Marks a step as done; false when another copy of its message already did.</summary>
    public Task<bool> TryCompleteStepAsync(Guid runId, string step, CancellationToken ct = default) =>
        _steps.DeleteAsync(StepPartition(runId), StepKey(step), ct: ct);

    /// <summary>Deletes the step markers left by a run that ended without finishing its steps.</summary>
    public async Task DeleteStepsAsync(Guid runId, CancellationToken ct = default) =>
        await _steps.DeleteManyAsync(StepPartition(runId), (await _steps.ListPartitionAsync(StepPartition(runId), ct)).Select(row => row.RowKey), ct);

    /// <summary>Deletes runs that started before <paramref name="cutoff"/> and their items.</summary>
    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        foreach (var row in await _runs.ListPartitionAsync(RunPartition, ct))
        {
            if (row.RowKey == ActiveRow || row.Value.StartedAt >= cutoff)
            {
                continue;
            }

            foreach (var item in await _items.ListPartitionAsync(row.Value.RunId.ToString("N"), ct))
            {
                await _items.DeleteAsync(row.Value.RunId.ToString("N"), item.RowKey, ct: ct);
            }

            await DeleteStepsAsync(row.Value.RunId, ct);
            await _active.DeleteAsync(IndexPartition, row.Value.RunId.ToString("N"), ct: ct);
            await _runs.DeleteAsync(RunPartition, row.RowKey, ct: ct);
        }
    }

    /// <summary>Queues an onboarding behind the active run; false when it is already queued.</summary>
    public Task<bool> TryQueueAsync(DriverQueuedRunRecord queued, CancellationToken ct = default) =>
        _queue.TryAddAsync(QueuePartition, QueueKey(queued), queued, ct);

    /// <summary>Queued onboardings, oldest first.</summary>
    public async Task<List<DriverQueuedRunRecord>> ListQueuedAsync(CancellationToken ct = default) =>
        (await _queue.ListPartitionAsync(QueuePartition, ct)).OrderBy(row => row.RowKey, StringComparer.Ordinal).Select(row => row.Value).ToList();

    /// <summary>Takes an onboarding off the queue; false when another caller already took it.</summary>
    public Task<bool> TryDequeueAsync(DriverQueuedRunRecord queued, CancellationToken ct = default) =>
        _queue.DeleteAsync(QueuePartition, QueueKey(queued), ct: ct);

    // Time first, so the partition lists in the order the onboardings were queued.
    private static string QueueKey(DriverQueuedRunRecord queued) => $"{queued.QueuedAt.UtcTicks:D19}_{queued.ModelId:N}";

    private static string StepPartition(Guid runId) => $"steps_{runId:N}";

    // Step names can hold characters a row key may not, so the key is their hash.
    private static string StepKey(string step) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(step)));
}

/// <summary>A counted step of a run.</summary>
/// <param name="Step">Step name, kept for diagnosis.</param>
public sealed record DriverRunStepRecord(string Step);

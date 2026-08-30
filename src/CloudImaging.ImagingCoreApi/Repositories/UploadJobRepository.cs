using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for asynchronous image publish jobs (<see cref="UploadJob"/>).
/// PartitionKey: "job" | RowKey: uploadId.
///
/// <para>
/// All jobs share a single partition deliberately. The worker's whole query is "everything not yet
/// terminal", the catalogs cap out in the low hundreds of entries, and only one upload is normally
/// in flight at a time, so a single partition keeps the claim query to one cheap point-read range
/// rather than a cross-partition scan.
/// </para>
/// </summary>
public sealed class UploadJobRepository
{
    private const string TableName = "UploadJobs";
    private const string Partition = "job";

    /// <summary>
    /// Terminal jobs are kept this long so the portal can still read a failure reason after a
    /// page reload, then swept by <see cref="DeleteExpiredAsync"/>.
    /// </summary>
    public static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(7);

    private readonly TableClient _table;

    public UploadJobRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Records a newly accepted publish job, or returns the job already recorded for this upload
    /// id. Publish is therefore idempotent: a client that retries after losing the response (or
    /// double-submits) gets the original job back instead of a 409, and the image is never
    /// published twice.
    /// </summary>
    public async Task<UploadJob> CreateOrGetAsync(UploadJob job, CancellationToken ct = default)
    {
        try
        {
            await _table.AddEntityAsync(ToEntity(job), ct);
            return job;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return await GetAsync(job.UploadId, ct) ?? job;
        }
    }

    public async Task<UploadJob?> GetAsync(string uploadId, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, uploadId, cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>
    /// Every job that has not reached a terminal state, oldest first. Includes jobs still marked
    /// <see cref="UploadJobStatus.Processing"/>, since a worker instance can be recycled mid-publish
    /// and leave one stranded; <see cref="TryClaimAsync"/> decides whether its lease has lapsed.
    /// </summary>
    public async Task<IReadOnlyList<UploadJob>> ListUnfinishedAsync(CancellationToken ct = default)
    {
        var pending = UploadJobStatus.Pending.ToString();
        var processing = UploadJobStatus.Processing.ToString();
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Partition} and (Status eq {pending} or Status eq {processing})");

        var jobs = new List<UploadJob>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            jobs.Add(FromEntity(entity));
        }
        return jobs.OrderBy(j => j.CreatedAt).ToList();
    }

    /// <summary>
    /// Attempts to take ownership of a job for <paramref name="leaseDuration"/>, returning the
    /// claimed job or <c>null</c> if another worker got there first or the lease is still valid.
    ///
    /// <para>
    /// The conditional update on the entity's ETag is what makes this safe. Timer triggers are
    /// already singleton-per-app, so contention is rare, but this also covers the manual retry
    /// path and any future move to a queue trigger without needing to revisit the concurrency
    /// story.
    /// </para>
    /// </summary>
    public async Task<UploadJob?> TryClaimAsync(
        string uploadId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        TableEntity entity;
        try
        {
            entity = (await _table.GetEntityAsync<TableEntity>(Partition, uploadId, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }

        var now = DateTimeOffset.UtcNow;
        var status = ParseStatus(entity.GetString("Status"));
        var leaseExpiresAt = entity.GetDateTimeOffset("LeaseExpiresAt");

        var claimable = status == UploadJobStatus.Pending
            || (status == UploadJobStatus.Processing && (leaseExpiresAt is null || leaseExpiresAt <= now));
        if (!claimable)
        {
            return null;
        }

        entity["Status"] = UploadJobStatus.Processing.ToString();
        entity["LeaseExpiresAt"] = now.Add(leaseDuration);
        entity["AttemptCount"] = (entity.GetInt32("AttemptCount") ?? 0) + 1;
        entity["UpdatedAt"] = now;
        // A reclaimed job restarts its work from the beginning, so its reported progress must too.
        entity["Stage"] = UploadJobStage.Verifying.ToString();
        entity["ProgressPercent"] = 0;

        try
        {
            await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return null;
        }

        return FromEntity(entity);
    }

    /// <summary>
    /// Records how far the worker has got through <paramref name="stage"/>, and renews the claim
    /// at the same time so a long publish that is demonstrably still making progress can never have
    /// its lease lapse underneath it.
    ///
    /// <para>
    /// Uses a merge (not a replace) with an unconditional ETag: this races only with itself and the
    /// terminal write, and progress is advisory, so losing one update is preferable to failing a
    /// publish over a concurrency conflict on a field nothing reads back.
    /// </para>
    /// </summary>
    public async Task ReportProgressAsync(
        string uploadId,
        UploadJobStage stage,
        int progressPercent,
        TimeSpan leaseRenewal,
        CancellationToken ct = default)
    {
        var entity = new TableEntity(Partition, uploadId)
        {
            ["Stage"] = stage.ToString(),
            ["ProgressPercent"] = Math.Clamp(progressPercent, 0, 100),
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
            ["LeaseExpiresAt"] = DateTimeOffset.UtcNow.Add(leaseRenewal),
        };

        try
        {
            await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* purged or already terminal */ }
    }

    public async Task CompleteAsync(string uploadId, Guid resultImageId, CancellationToken ct = default) =>
        await SetTerminalAsync(uploadId, UploadJobStatus.Completed, resultImageId, null, ct);

    public async Task FailAsync(string uploadId, string failureReason, CancellationToken ct = default) =>
        await SetTerminalAsync(uploadId, UploadJobStatus.Failed, null, failureReason, ct);

    private async Task SetTerminalAsync(
        string uploadId, UploadJobStatus status, Guid? resultImageId, string? failureReason, CancellationToken ct)
    {
        TableEntity entity;
        try
        {
            entity = (await _table.GetEntityAsync<TableEntity>(Partition, uploadId, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return; }

        entity["Status"] = status.ToString();
        entity["FailureReason"] = failureReason;
        entity["ResultImageId"] = resultImageId?.ToString();
        entity["UpdatedAt"] = DateTimeOffset.UtcNow;
        entity["Stage"] = UploadJobStage.Publishing.ToString();
        // Pin the bar wherever it got to: full on success, frozen at the failure point otherwise.
        entity["ProgressPercent"] = status == UploadJobStatus.Completed
            ? 100
            : entity.GetInt32("ProgressPercent") ?? 0;
        // Clearing the lease keeps a terminal job from ever looking reclaimable to the worker.
        entity["LeaseExpiresAt"] = null;

        await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string uploadId, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, uploadId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }
    }

    /// <summary>
    /// Removes terminal jobs older than <see cref="TerminalRetention"/>. Without this the table
    /// would grow without bound, since nothing else ever deletes a completed job.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - TerminalRetention;
        var completed = UploadJobStatus.Completed.ToString();
        var failed = UploadJobStatus.Failed.ToString();
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Partition} and (Status eq {completed} or Status eq {failed}) and UpdatedAt lt {cutoff}");

        var deleted = 0;
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            try
            {
                await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, ct);
                deleted++;
            }
            catch (RequestFailedException) { /* raced with another sweep; ignore */ }
        }
        return deleted;
    }

    private static UploadJobStatus ParseStatus(string? value) =>
        Enum.TryParse<UploadJobStatus>(value, out var parsed) ? parsed : UploadJobStatus.Pending;

    private static UploadJobKind ParseKind(string? value) =>
        Enum.TryParse<UploadJobKind>(value, out var parsed) ? parsed : UploadJobKind.OsImage;

    private static UploadJobStage ParseStage(string? value) =>
        Enum.TryParse<UploadJobStage>(value, out var parsed) ? parsed : UploadJobStage.Queued;

    private static TableEntity ToEntity(UploadJob j) => new(Partition, j.UploadId)
    {
        ["Kind"] = j.Kind.ToString(),
        ["Status"] = j.Status.ToString(),
        ["BlobName"] = j.BlobName,
        ["Sha256Hash"] = j.Sha256Hash,
        ["Version"] = j.Version,
        ["Name"] = j.Name,
        ["Description"] = j.Description,
        ["SizeBytes"] = j.SizeBytes,
        ["CreatedAt"] = j.CreatedAt,
        ["UpdatedAt"] = j.UpdatedAt,
        ["FailureReason"] = j.FailureReason,
        ["ResultImageId"] = j.ResultImageId?.ToString(),
        ["AttemptCount"] = j.AttemptCount,
        ["LeaseExpiresAt"] = j.LeaseExpiresAt,
        ["Stage"] = j.Stage.ToString(),
        ["ProgressPercent"] = j.ProgressPercent,
    };

    private static UploadJob FromEntity(TableEntity e) => new()
    {
        UploadId = e.RowKey,
        Kind = ParseKind(e.GetString("Kind")),
        Status = ParseStatus(e.GetString("Status")),
        BlobName = e.GetString("BlobName") ?? string.Empty,
        Sha256Hash = e.GetString("Sha256Hash") ?? string.Empty,
        Version = e.GetString("Version") ?? string.Empty,
        Name = e.GetString("Name"),
        Description = e.GetString("Description"),
        SizeBytes = e.GetInt64("SizeBytes") ?? 0L,
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
        UpdatedAt = e.GetDateTimeOffset("UpdatedAt") ?? DateTimeOffset.UtcNow,
        FailureReason = e.GetString("FailureReason"),
        ResultImageId = Guid.TryParse(e.GetString("ResultImageId"), out var id) ? id : null,
        AttemptCount = e.GetInt32("AttemptCount") ?? 0,
        LeaseExpiresAt = e.GetDateTimeOffset("LeaseExpiresAt"),
        Stage = ParseStage(e.GetString("Stage")),
        ProgressPercent = e.GetInt32("ProgressPercent") ?? 0,
    };
}

using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Status endpoint and background worker for asynchronous image publish jobs.
///
/// GET /api/internal/upload-jobs/{uploadId} — poll a job's status; the portal calls this after
///   receiving 202 from any of the three publish endpoints.
///
/// Timer ProcessUploadJobs — claims unfinished jobs and runs the expensive verification.
/// </summary>
public sealed partial class UploadJobFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long a worker owns a claimed job. Comfortably longer than the slowest realistic publish
    /// (hashing plus ISO extraction of a 20 GB image) so a job in progress is never stolen by the
    /// next timer tick, while still being short enough that a job stranded by an instance restart
    /// is picked back up promptly. Kept equal to the <c>functionTimeout</c> in host.json so the
    /// lease lapses at exactly the point the host would have killed the invocation, never before.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Attempts before a job is abandoned. A blob that reliably crashes the worker would otherwise
    /// be retried forever, once per lease expiry.
    /// </summary>
    private const int MaxAttempts = 3;

    private readonly UploadJobRepository _jobRepo;
    private readonly UploadPublishService _publishService;
    private readonly ILogger<UploadJobFunctions> _logger;

    public UploadJobFunctions(
        UploadJobRepository jobRepo,
        UploadPublishService publishService,
        ILogger<UploadJobFunctions> logger)
    {
        _jobRepo = jobRepo;
        _publishService = publishService;
        _logger = logger;
    }

    // ── GET /api/internal/upload-jobs/{uploadId} ──────────────────────────────

    [Function("GetUploadJob")]
    public async Task<HttpResponseData> GetUploadJob(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/upload-jobs/{uploadId}")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        var job = await _jobRepo.GetAsync(uploadId, context.CancellationToken);
        if (job is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(job, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── Timer: drain the publish queue ────────────────────────────────────────

    /// <summary>
    /// Runs every 15 seconds so an operator watching the portal sees publishing start almost
    /// immediately after upload. Timer triggers are singleton per Function app, and each job is
    /// additionally claimed with an ETag-conditional lease, so a long publish started by one tick
    /// is never double-processed by a later one.
    /// </summary>
    [Function("ProcessUploadJobs")]
    public async Task ProcessUploadJobs(
        [TimerTrigger("*/15 * * * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var jobs = await _jobRepo.ListUnfinishedAsync(ct);

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            // Only give up on a job once nobody holds its lease, so a publish that is genuinely
            // still running on its final attempt is never marked failed out from under itself.
            var leaseLapsed = job.LeaseExpiresAt is null || job.LeaseExpiresAt <= DateTimeOffset.UtcNow;
            if (job.AttemptCount >= MaxAttempts && leaseLapsed)
            {
                LogJobAbandoned(_logger, job.UploadId, job.AttemptCount);
                await _jobRepo.FailAsync(
                    job.UploadId,
                    "Publishing failed repeatedly. Discard this upload and try again.",
                    ct);
                continue;
            }

            var claimed = await _jobRepo.TryClaimAsync(job.UploadId, LeaseDuration, ct);
            if (claimed is null)
            {
                // Still leased by an in-flight publish, or another worker won the race.
                continue;
            }

            LogJobClaimed(_logger, claimed.UploadId, claimed.Kind, claimed.AttemptCount);
            await _publishService.ProcessAsync(claimed, ct);
        }
    }

    // ── Timer: sweep old terminal jobs ────────────────────────────────────────

    [Function("PurgeExpiredUploadJobs")]
    public async Task PurgeExpiredUploadJobs(
        [TimerTrigger("0 0 3 * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var deleted = await _jobRepo.DeleteExpiredAsync(context.CancellationToken);
        if (deleted > 0)
        {
            LogJobsPurged(_logger, deleted);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed upload job {UploadId} ({Kind}), attempt {AttemptCount}.")]
    private static partial void LogJobClaimed(ILogger logger, string uploadId, UploadJobKind kind, int attemptCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoning upload job {UploadId} after {AttemptCount} attempts.")]
    private static partial void LogJobAbandoned(ILogger logger, string uploadId, int attemptCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired upload job(s).")]
    private static partial void LogJobsPurged(ILogger logger, int count);
}

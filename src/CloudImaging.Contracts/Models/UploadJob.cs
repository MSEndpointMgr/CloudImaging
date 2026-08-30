using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Tracks the asynchronous "publish" half of a staged image upload.
///
/// <para>
/// Publishing an uploaded image is slow: it recomputes the SHA-256 over the whole staged blob
/// (OS images are supported up to 20 GB), and for an uploaded ISO it also streams the image out to
/// extract <c>sources\install.wim</c>. That cannot run inside the HTTP request, because Azure
/// Static Web Apps aborts every <c>/api</c> call at 45 seconds and returns a bare
/// <c>500 Backend call failure</c>, and Azure Functions HTTP is severed by the load balancer at
/// 230 seconds regardless of <c>functionTimeout</c>. Neither limit is configurable.
/// </para>
///
/// <para>
/// So the publish endpoint does only the fast, cheap work inline (commit the block list, check the
/// file's magic bytes via a ranged header read) and returns <c>202 Accepted</c> with one of these
/// jobs. A timer-triggered worker then does the expensive verification out of band and drives the
/// job to <see cref="UploadJobStatus.Completed"/> or <see cref="UploadJobStatus.Failed"/>. The
/// portal polls the job and only shows the image as published once it reaches a terminal state,
/// so a catalog entry is still never created from an unverified blob.
/// </para>
/// </summary>
public sealed class UploadJob
{
    /// <summary>The upload session id, reused as the job id so the portal can poll without a second identifier.</summary>
    public required string UploadId { get; init; }

    public required UploadJobKind Kind { get; init; }

    public required UploadJobStatus Status { get; init; }

    /// <summary>Path of the staged blob within the kind's upload container.</summary>
    public required string BlobName { get; init; }

    /// <summary>The SHA-256 the browser computed over the local file, which the worker re-verifies.</summary>
    public required string Sha256Hash { get; init; }

    public required string Version { get; init; }

    /// <summary>Catalog display name. OS images only; boot and recovery images are identified by version.</summary>
    public string? Name { get; init; }

    /// <summary>Optional operator-supplied note. Recovery images only.</summary>
    public string? Description { get; init; }

    /// <summary>Size of the uploaded file as reported by the browser, used for the catalog entry.</summary>
    public long SizeBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Operator-facing explanation when <see cref="Status"/> is <see cref="UploadJobStatus.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Which phase of publishing the worker is currently in.</summary>
    public UploadJobStage Stage { get; init; }

    /// <summary>
    /// Completion of the current <see cref="Stage"/>, 0-100. Verifying a 20 GB image takes minutes,
    /// so the worker reports incremental progress here for the portal's progress bar instead of
    /// leaving the operator staring at a bar pinned at 100% for the whole publish.
    /// </summary>
    public int ProgressPercent { get; init; }

    /// <summary>Id of the created catalog entry once <see cref="UploadJobStatus.Completed"/>.</summary>
    public Guid? ResultImageId { get; init; }

    /// <summary>
    /// How many times the worker has claimed this job. Guards against a job that crashes the
    /// worker being retried forever after each lease expiry.
    /// </summary>
    public int AttemptCount { get; init; }

    /// <summary>
    /// When the current worker's claim lapses. A job still <see cref="UploadJobStatus.Processing"/>
    /// past this point is assumed abandoned (instance recycled mid-publish) and may be reclaimed.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
}

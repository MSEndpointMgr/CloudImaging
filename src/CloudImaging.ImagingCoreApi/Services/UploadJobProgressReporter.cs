using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Relays publish progress from the hashing / ISO-extraction loops onto the
/// <see cref="Contracts.Models.UploadJob"/> record the portal polls.
///
/// <para>
/// Two things make this more than a straight pass-through. Hashing a 20 GB blob calls back tens of
/// thousands of times, so writes are throttled to at most one every couple of seconds and only when
/// the whole-percent value actually moved. And progress is advisory: a failed or slow status write
/// must never stall or fail the publish itself, so writes are issued without being awaited and any
/// error is swallowed.
/// </para>
///
/// <para>
/// One reporter covers a job's whole publish, with <see cref="BeginStageAsync"/> marking each
/// transition. Sharing the instance is what keeps a straggling write from the previous stage from
/// landing on top of the next one: writes are serialized through a single in-flight gate, and any
/// sample taken before a transition is discarded by its stage generation.
/// </para>
/// </summary>
public sealed class UploadJobProgressReporter : IProgress<double>
{
    /// <summary>
    /// Floor on the gap between two status writes. Comfortably below the portal's 3 second poll
    /// interval, so the operator still sees the bar advance on nearly every poll, while keeping a
    /// multi-GB publish to a few hundred Table Storage writes rather than tens of thousands.
    /// </summary>
    private static readonly TimeSpan MinWriteInterval = TimeSpan.FromSeconds(2);

    private readonly UploadJobRepository _repository;
    private readonly string _uploadId;
    private readonly TimeSpan _leaseRenewal;
    private readonly CancellationToken _ct;

    private UploadJobStage _stage = UploadJobStage.Queued;
    private int _stageGeneration;
    private int _writeInFlight;
    private int _lastPercent = -1;
    private long _lastWriteTicks = DateTimeOffset.MinValue.UtcTicks;

    public UploadJobProgressReporter(
        UploadJobRepository repository,
        string uploadId,
        TimeSpan leaseRenewal,
        CancellationToken ct)
    {
        _repository = repository;
        _uploadId = uploadId;
        _leaseRenewal = leaseRenewal;
        _ct = ct;
    }

    /// <summary>
    /// Moves to <paramref name="stage"/> and writes it at 0%, invalidating any progress sample from
    /// the stage just finished. Awaited, so the portal sees the transition before the (potentially
    /// long) stage actually begins.
    /// </summary>
    public async Task BeginStageAsync(UploadJobStage stage)
    {
        _stage = stage;
        Interlocked.Increment(ref _stageGeneration);
        _lastPercent = 0;
        Interlocked.Exchange(ref _lastWriteTicks, DateTimeOffset.UtcNow.UtcTicks);

        try
        {
            await _repository.ReportProgressAsync(_uploadId, stage, 0, _leaseRenewal, _ct);
        }
        catch
        {
            // Advisory only: a lost progress update must never take a publish down with it.
        }
    }

    /// <param name="fraction">Completion of the current stage, 0.0 to 1.0.</param>
    public void Report(double fraction)
    {
        if (_ct.IsCancellationRequested)
        {
            return;
        }

        var percent = (int)Math.Clamp(Math.Round(fraction * 100), 0, 100);
        if (percent == _lastPercent)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.UtcTicks;
        // 100% is always written immediately: it is the last thing the operator sees for this stage,
        // and dropping it would leave the bar short of the mark until the next stage starts.
        if (percent < 100 && now - Interlocked.Read(ref _lastWriteTicks) < MinWriteInterval.Ticks)
        {
            return;
        }

        // Only one status write in flight at a time; if the previous one is still going, skip this
        // sample rather than queueing writes up behind a slow storage call.
        if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastWriteTicks, now);
        _lastPercent = percent;
        _ = WriteAsync(_stage, Volatile.Read(ref _stageGeneration), percent);
    }

    private async Task WriteAsync(UploadJobStage stage, int generation, int percent)
    {
        try
        {
            if (generation != Volatile.Read(ref _stageGeneration))
            {
                return; // superseded by a stage transition while this sample waited its turn
            }

            await _repository.ReportProgressAsync(_uploadId, stage, percent, _leaseRenewal, _ct);
        }
        catch
        {
            // Advisory only: a lost progress update must never take a publish down with it.
        }
        finally
        {
            Interlocked.Exchange(ref _writeInFlight, 0);
        }
    }
}

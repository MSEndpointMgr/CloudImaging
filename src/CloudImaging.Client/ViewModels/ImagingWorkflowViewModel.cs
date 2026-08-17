using System.IO;
using System.Net.Http;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// Orchestrates the Format → Download → Apply imaging pipeline once a session has (or reaches)
/// an OS image assignment. Drives <see cref="ProgressViewModel"/>, reports step progress back to
/// the Device Gateway API via <see cref="Services.ImagingProgressReporter"/>, and navigates to
/// <see cref="ResultsView"/> on completion, failure, or an unexpected terminal transition
/// (T056, FR-005, FR-006, FR-007, FR-008, FR-009d).
///
/// Previously, none of <see cref="Services.ImageDownloadService"/>, <see cref="Services.ImageApplyService"/>,
/// <see cref="Services.ImagingProgressReporter"/>, <see cref="Services.SasRefreshCoordinator"/>,
/// <see cref="Services.ImageCacheService"/>, or <see cref="Services.ImageCacheMaintenanceService"/> were
/// ever instantiated by the running application — this type is what wires them together.
/// </summary>
public sealed class ImagingWorkflowViewModel : IDisposable
{
    private static readonly TimeSpan AssignmentWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AssignmentPollInterval = TimeSpan.FromSeconds(5);

    private readonly Services.DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly string? _deviceSerialNumber;
    private readonly ProgressViewModel _progress;
    private readonly Action<ResultsViewModel.Outcome, string?, string?, string?> _navigateToResults;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _cacheRoot;

    private Services.SasRefreshCoordinator? _sasCoordinator;
    private Services.SessionStatusResponse _status;

    public ImagingWorkflowViewModel(
        Services.DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        string? deviceSerialNumber,
        Services.SessionStatusResponse initialStatus,
        ProgressViewModel progress,
        Action<ResultsViewModel.Outcome, string?, string?, string?> navigateToResults,
        ILoggerFactory loggerFactory,
        string? cacheRoot = null)
    {
        _gatewayClient      = gatewayClient;
        _sessionId          = sessionId;
        _deviceSerialNumber = deviceSerialNumber;
        _status             = initialStatus;
        _progress           = progress;
        _navigateToResults  = navigateToResults;
        _loggerFactory      = loggerFactory;
        _cacheRoot          = cacheRoot ?? Path.Combine(Path.GetTempPath(), "CloudImagingCache");
    }

    /// <summary>Starts the imaging pipeline in the background. Fire-and-forget by design.</summary>
    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        var ct = CancellationToken.None;
        var reporter = new Services.ImagingProgressReporter(
            _gatewayClient, _sessionId, _loggerFactory.CreateLogger<Services.ImagingProgressReporter>());

        try
        {
            if (!await WaitForImageAssignmentAsync(ct))
            {
                // A terminal state was reached (or we timed out) and navigation already happened.
                return;
            }

            _sasCoordinator = new Services.SasRefreshCoordinator(
                _gatewayClient,
                _sessionId,
                _status.SasTokenUrl!,
                ParseExpiry(_status.SasTokenUrlExpiresAt),
                _loggerFactory.CreateLogger<Services.SasRefreshCoordinator>());
            _sasCoordinator.Start();

            var hash = _status.Sha256Hash ?? string.Empty;

            // ── Format ──────────────────────────────────────────────────────────
            _progress.StatusMessage = "Preparing the target disk…";
            _progress.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress);
            await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress, ct: ct);

            string targetVolume;
            try
            {
                var formatService = new Services.DiskFormatService(_loggerFactory.CreateLogger<Services.DiskFormatService>());
                targetVolume = await formatService.FormatTargetDiskAsync(ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.FormatDisk, "FMT", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 10;

            // ── Download ────────────────────────────────────────────────────────
            _progress.StatusMessage = "Downloading operating system image…";
            _progress.UpdateStep(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress);

            var cache = new Services.ImageCacheService(_cacheRoot, _loggerFactory.CreateLogger<Services.ImageCacheService>());
            var cacheMaintenance = new Services.ImageCacheMaintenanceService(cache, _loggerFactory.CreateLogger<Services.ImageCacheMaintenanceService>());
            await cacheMaintenance.RunAsync(ct);

            var downloadService = new Services.ImageDownloadService(
                new HttpClient(),
                _loggerFactory.CreateLogger<Services.ImageDownloadService>(),
                cacheMaintenance.CacheWriteEnabled ? cache : null);

            var destinationPath = Path.Combine(Path.GetTempPath(), $"ci-image-{_sessionId:N}.wim");

            string wimPath;
            try
            {
                wimPath = await downloadService.EnsureLocalWimAsync(
                    imageId: hash,
                    expectedHash: hash,
                    sasUrl: _sasCoordinator.CurrentSasUrl ?? _status.SasTokenUrl!,
                    destinationPath: destinationPath,
                    onProgress: pct =>
                    {
                        _progress.OverallPercent = 10 + (int)(pct * 0.6); // 10–70%
                        _ = reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    ct: ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.DownloadImage, "DWN", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.DownloadImage, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 70;

            // ── Apply ───────────────────────────────────────────────────────────
            _progress.StatusMessage = "Applying operating system image…";
            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);

            var applyService = new Services.ImageApplyService(_loggerFactory.CreateLogger<Services.ImageApplyService>());
            try
            {
                await applyService.ApplyAsync(
                    wimPath,
                    hash,
                    targetVolume,
                    onProgress: pct => _progress.OverallPercent = 70 + (int)(pct * 0.3), // 70–100%
                    ct: ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyImage, "APL", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 100;
            _progress.StatusMessage = "Imaging complete.";

            _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
        }
        catch (Exception ex)
        {
            await FailAsync(reporter, null, "REG", ex.Message, ct);
        }
        finally
        {
            _sasCoordinator?.Dispose();
        }
    }

    /// <summary>
    /// Polls the Device Gateway API until the session carries both a SAS URL and image hash
    /// (i.e. an OS image has actually been assigned), a terminal state is reached, or
    /// <see cref="AssignmentWaitTimeout"/> elapses. Returns <c>false</c> (and has already
    /// navigated to <see cref="ResultsView"/>) when the caller should stop; <c>true</c> when
    /// <see cref="_status"/> is ready to drive the pipeline.
    /// </summary>
    private async Task<bool> WaitForImageAssignmentAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + AssignmentWaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!string.IsNullOrEmpty(_status.SasTokenUrl) && !string.IsNullOrEmpty(_status.Sha256Hash))
            {
                return true;
            }

            if (!Enum.TryParse<SessionState>(_status.State ?? string.Empty, ignoreCase: true, out var state))
            {
                state = SessionState.SessionInit;
            }

            switch (state)
            {
                case SessionState.SessionCompleted:
                    _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
                    return false;
                case SessionState.SessionFailed:
                    _navigateToResults(
                        ResultsViewModel.Outcome.Failure,
                        _deviceSerialNumber,
                        "The session failed before an image could be applied.",
                        null);
                    return false;
                case SessionState.SessionNotAuthorized:
                    _navigateToResults(ResultsViewModel.Outcome.NotAuthorized, _deviceSerialNumber, null, null);
                    return false;
            }

            try { await Task.Delay(AssignmentPollInterval, ct); }
            catch (OperationCanceledException) { return false; }

            var next = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct);
            if (next is not null) _status = next;
        }

        _navigateToResults(
            ResultsViewModel.Outcome.Failure,
            _deviceSerialNumber,
            "Timed out waiting for an OS image assignment.",
            SupportReferenceCode.ForClient(_sessionId, "REG").ToString());
        return false;
    }

    private async Task FailAsync(
        Services.ImagingProgressReporter reporter,
        ImagingStepName? step,
        string stageCode,
        string errorDetail,
        CancellationToken ct)
    {
        var code = SupportReferenceCode.ForClient(_sessionId, stageCode).ToString();

        if (step is { } s)
        {
            _progress.UpdateStep(s, ImagingStepStatus.Failed);
            try { await reporter.ReportAsync(s, ImagingStepStatus.Failed, errorDetail: errorDetail, ct: ct); }
            catch { /* best-effort — progress reporting must never mask the real failure */ }
        }

        _progress.ErrorMessage = errorDetail;
        _progress.SupportReferenceCode = code;

        _navigateToResults(ResultsViewModel.Outcome.Failure, _deviceSerialNumber, errorDetail, code);
    }

    private static DateTimeOffset? ParseExpiry(string? iso) =>
        DateTimeOffset.TryParse(
            iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;

    public void Dispose() => _sasCoordinator?.Dispose();
}

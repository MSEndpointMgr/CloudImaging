using System.IO;
using System.Net.Http;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// Orchestrates the Format → Download → Apply → Configure Boot → Apply Recovery imaging
/// pipeline once a session has (or reaches) an OS image assignment. Drives
/// <see cref="ProgressViewModel"/>, reports step progress back to the Device Gateway API via
/// <see cref="Services.ImagingProgressReporter"/>, and navigates to <see cref="ResultsView"/> on
/// completion, failure, or an unexpected terminal transition
/// (T056, FR-005, FR-006, FR-007, FR-008, FR-009d).
///
/// Previously, none of <see cref="Services.ImageDownloadService"/>, <see cref="Services.ImageApplyService"/>,
/// <see cref="Services.ImagingProgressReporter"/>, <see cref="Services.SasRefreshCoordinator"/>,
/// <see cref="Services.ImageCacheService"/>, or <see cref="Services.ImageCacheMaintenanceService"/> were
/// ever instantiated by the running application — this type is what wires them together.
///
/// The disk is now partitioned per the admin-configured <see cref="PartitioningScheme"/>
/// snapshotted onto the session (ESP/MSR/Windows/Recovery), boot files are configured with
/// <c>bcdboot</c>, and the WinRE recovery image is downloaded and applied — turning what was
/// previously a single non-bootable partition into a real UEFI-bootable device.
/// </summary>
public sealed partial class ImagingWorkflowViewModel : IDisposable
{
    private static readonly TimeSpan AssignmentWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AssignmentPollInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SchemeJsonOptions = new(JsonSerializerDefaults.Web);

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

        // A technician reviewing the rolling log after a Retry (which restarts the whole
        // pipeline from the beginning, see ResultsViewModel.RetryCommand) otherwise has no way
        // to tell where one attempt's entries end and the next one's begin. This banner line
        // makes each attempt visually distinct when scrolling through the combined log.
        var pipelineLogger = _loggerFactory.CreateLogger<ImagingWorkflowViewModel>();
        LogPipelineStarting(pipelineLogger, _sessionId);

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

            // Every Information-level log message the step services below emit (disk selection,
            // "Starting DISM: ...", "Starting bcdboot ...", "Starting reagentc.exe ...", hash
            // verification, etc.) is also surfaced in ProgressView's activity ticker, in addition
            // to the rolling file log — FR-007's "light details around what's currently going on".
            var pipelineLoggerFactory = new Services.ProgressActivityLoggerFactory(_loggerFactory, _progress.AppendActivity);

            var hash = _status.Sha256Hash ?? string.Empty;
            var scheme = _status.PartitioningScheme ?? PartitioningScheme.Default;

            // ── Format ──────────────────────────────────────────────────────────
            _progress.StatusMessage = "Preparing the target disk…";
            _progress.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress);
            await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress, ct: ct);

            Services.DiskFormatResult diskFormat;
            try
            {
                var formatService = new Services.DiskFormatService(pipelineLoggerFactory.CreateLogger<Services.DiskFormatService>());
                diskFormat = await formatService.FormatTargetDiskAsync(scheme, ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.FormatDisk, "FMT", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 8;

            // ── Download OS image ──────────────────────────────────────────────
            _progress.StatusMessage = "Downloading operating system image…";
            _progress.UpdateStep(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress);

            var cache = new Services.ImageCacheService(_cacheRoot, _loggerFactory.CreateLogger<Services.ImageCacheService>());
            var cacheMaintenance = new Services.ImageCacheMaintenanceService(cache, _loggerFactory.CreateLogger<Services.ImageCacheMaintenanceService>());
            await cacheMaintenance.RunAsync(ct);

            var downloadService = new Services.ImageDownloadService(
                new HttpClient(),
                pipelineLoggerFactory.CreateLogger<Services.ImageDownloadService>(),
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
                        _progress.OverallPercent = 8 + (int)(pct * 0.47); // 8–55%
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
            _progress.OverallPercent = 55;

            // ── Apply OS image ─────────────────────────────────────────────────
            _progress.StatusMessage = "Applying operating system image…";
            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);

            var applyService = new Services.ImageApplyService(pipelineLoggerFactory.CreateLogger<Services.ImageApplyService>());
            try
            {
                await applyService.ApplyAsync(
                    wimPath,
                    hash,
                    diskFormat.WindowsVolume,
                    onProgress: pct =>
                    {
                        _progress.OverallPercent = 55 + (int)(pct * 0.25); // 55–80%
                        _ = reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    ct: ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyImage, "APL", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 80;

            // ── Configure boot ──────────────────────────────────────────────────
            _progress.StatusMessage = "Configuring boot files…";
            _progress.UpdateStep(ImagingStepName.ConfigureBoot, ImagingStepStatus.InProgress);
            await reporter.ReportAsync(ImagingStepName.ConfigureBoot, ImagingStepStatus.InProgress, ct: ct);

            var bootConfigService = new Services.BootConfigurationService(pipelineLoggerFactory.CreateLogger<Services.BootConfigurationService>());
            try
            {
                await bootConfigService.ConfigureAsync(diskFormat.WindowsVolume, diskFormat.EfiSystemVolume, ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ConfigureBoot, "BOT", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.ConfigureBoot, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.ConfigureBoot, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 85;

            // ── Download + apply recovery image ─────────────────────────────────
            _progress.StatusMessage = "Downloading recovery image…";
            _progress.UpdateStep(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress);
            await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, ct: ct);

            var recoveryInfo = await _gatewayClient.GetLatestRecoveryImageAsync(ct);
            var recoveryService = new Services.RecoveryImageService(pipelineLoggerFactory.CreateLogger<Services.RecoveryImageService>());

            if (recoveryInfo is null)
            {
                // No custom Recovery Image has been published to the Portal catalog — fall back
                // to the WinRE that the OS image already carries at
                // Windows\System32\Recovery\Winre.wim rather than failing the whole session.
                _progress.StatusMessage = "No published recovery image — reusing the OS image's embedded recovery environment…";
                try
                {
                    var reusedEmbeddedImage = await recoveryService.ApplyFromEmbeddedImageAsync(
                        diskFormat.WindowsVolume, diskFormat.RecoveryVolume, ct);

                    if (!reusedEmbeddedImage)
                    {
                        await FailAsync(reporter, ImagingStepName.ApplyRecoveryImage, "REC",
                            "No published recovery image is available, and the applied OS image does not contain an embedded WinRE (Winre.wim) to fall back to.", ct);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    await FailAsync(reporter, ImagingStepName.ApplyRecoveryImage, "REC", ex.Message, ct);
                    return;
                }

                _progress.UpdateStep(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed);
                await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed, ct: ct);
                _progress.OverallPercent = 100;
                _progress.StatusMessage = "Imaging complete.";

                _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
                return;
            }

            var recoveryDestinationPath = Path.Combine(Path.GetTempPath(), $"ci-recovery-{_sessionId:N}.wim");
            string recoveryWimPath;
            try
            {
                recoveryWimPath = await downloadService.EnsureLocalWimAsync(
                    imageId: recoveryInfo.Sha256Hash,
                    expectedHash: recoveryInfo.Sha256Hash,
                    sasUrl: recoveryInfo.SasTokenUrl,
                    destinationPath: recoveryDestinationPath,
                    onProgress: pct =>
                    {
                        _progress.OverallPercent = 85 + (int)(pct * 0.10); // 85–95%
                        _ = reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    ct: ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyRecoveryImage, "REC", ex.Message, ct);
                return;
            }

            _progress.StatusMessage = "Applying recovery image…";
            try
            {
                await recoveryService.ApplyAsync(
                    recoveryWimPath,
                    recoveryInfo.Sha256Hash,
                    diskFormat.WindowsVolume,
                    diskFormat.RecoveryVolume,
                    ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyRecoveryImage, "REC", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed, ct: ct);
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
                    FireAndForgetLogUpload();
                    _navigateToResults(
                        ResultsViewModel.Outcome.Failure,
                        _deviceSerialNumber,
                        "The session failed before an image could be applied.",
                        null);
                    return false;
                case SessionState.SessionNotAuthorized:
                    FireAndForgetLogUpload();
                    _navigateToResults(ResultsViewModel.Outcome.NotAuthorized, _deviceSerialNumber, null, null);
                    return false;
            }

            try { await Task.Delay(AssignmentPollInterval, ct); }
            catch (OperationCanceledException) { return false; }

            var next = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct);
            if (next is not null) _status = next;
        }

        FireAndForgetLogUpload();
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

        // ProgressView and ResultsView are both FR-002b no-scroll views (see
        // UiThreadResponsivenessTests.View_ContainsNoScrollViewer): a failed external tool
        // (diskpart/bcdboot/reagentc) can embed its whole console transcript in errorDetail, which
        // would otherwise overflow those fixed-size windows with no way to see the rest. The
        // reporter call and the rolling log above already got the untruncated errorDetail; only
        // what's shown on screen is bounded, pointing the technician at "View Log" for the rest.
        var onScreenDetail = BuildOnScreenErrorDetail(errorDetail);
        _progress.ErrorMessage = onScreenDetail;
        _progress.SupportReferenceCode = code;

        FireAndForgetLogUpload();
        _navigateToResults(ResultsViewModel.Outcome.Failure, _deviceSerialNumber, onScreenDetail, code);
    }

    /// <summary>Upper bound on how much of a failure's error detail is shown on screen (see <see cref="FailAsync"/>).</summary>
    private const int MaxOnScreenErrorDetailChars = 240;

    private static string BuildOnScreenErrorDetail(string errorDetail)
    {
        if (errorDetail.Length <= MaxOnScreenErrorDetailChars)
        {
            return errorDetail;
        }

        return errorDetail[..MaxOnScreenErrorDetailChars].TrimEnd() + "…\n\nSee \"View Log\" for the full detail.";
    }

    /// <summary>
    /// Kicks off a best-effort upload of the current local diagnostic log for this session,
    /// without awaiting it — the failure Results view must appear immediately regardless of
    /// network conditions. <see cref="Services.LogUploadService"/> internally bounds the whole
    /// attempt with its own timeout and never throws, so this is safe to fire-and-forget.
    /// </summary>
    private void FireAndForgetLogUpload()
    {
        var httpClient = new HttpClient();
        var logUploadService = new Services.LogUploadService(
            httpClient, _gatewayClient, _loggerFactory.CreateLogger<Services.LogUploadService>());

        _ = logUploadService.UploadCurrentLogAsync(_sessionId, CancellationToken.None)
            .ContinueWith(_ => httpClient.Dispose(), TaskScheduler.Default);
    }

    private static DateTimeOffset? ParseExpiry(string? iso) =>
        DateTimeOffset.TryParse(
            iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;

    public void Dispose() => _sasCoordinator?.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "==== Starting imaging pipeline for session {SessionId} ====")]
    private static partial void LogPipelineStarting(ILogger logger, Guid sessionId);
}

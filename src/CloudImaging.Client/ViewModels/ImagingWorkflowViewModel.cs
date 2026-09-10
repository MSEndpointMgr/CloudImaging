using System.IO;
using System.Management;
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

    // Null until resolved. An explicit constructor override or a discovered CACHE-labelled USB
    // volume resolves this immediately; otherwise it stays null until the Format step completes
    // and falls back to the freshly-formatted target Windows volume (see RunAsync) — never to
    // Path.GetTempPath(), which is the WinPE boot RAM disk (X:) and has nowhere near enough space
    // for a multi-GB OS image.
    private string? _cacheRoot;

    // True when _cacheRoot resolved to the target Windows volume rather than to durable boot
    // media. That location is only ever scratch space: it is wiped by the next session's Format
    // step, so persisting a cross-session cache there is worthless — and it would ship a
    // multi-GB duplicate of the WIM inside the OS that is about to be handed to the end user.
    // Drives both "don't persist to the cache" and "delete the staged WIMs afterwards" below.
    private bool _cacheRootIsTargetVolume;

    private Services.SasRefreshCoordinator? _sasCoordinator;
    private Services.SessionHeartbeatCoordinator? _heartbeatCoordinator;
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

        // Resolve immediately if possible: an explicit override (tests) always wins, otherwise try
        // the USB boot media's dedicated CACHE-labelled NTFS partition (created by
        // UsbPartitionProvisioningService during USB preparation, sized at a 24 GB minimum
        // specifically to hold one OS image — FR-055). If neither is available — e.g. WinPE
        // running in a Hyper-V VM with no physical USB attached, an ISO/PXE boot, or dev/test —
        // this stays null and is resolved later in RunAsync once the target Windows volume is
        // known (see the fallback there for why Path.GetTempPath() must never be the default).
        var cacheVolume = FindCacheVolumeDriveLetter();
        _cacheRoot = cacheRoot ?? (cacheVolume is not null ? Path.Combine(cacheVolume, "CloudImagingCache") : null);
    }

    /// <summary>
    /// Locates the CACHE-labelled NTFS volume this Client's USB boot media was prepared with —
    /// mirrors <see cref="Services.BootImageSelfUpdateService"/>'s analogous BOOT-volume lookup,
    /// and <c>UsbPartitionProvisioningService.FindCacheVolumeDriveLetter</c> in the Media Builder,
    /// which creates and labels this same volume during USB preparation.
    /// </summary>
    private static string? FindCacheVolumeDriveLetter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, Label FROM Win32_Volume WHERE Label='CACHE'");
            using var results = searcher.Get();
            foreach (ManagementObject volume in results)
            {
                var letter = volume["DriveLetter"]?.ToString();
                if (!string.IsNullOrWhiteSpace(letter))
                    return letter;
            }
        }
        catch (ManagementException)
        {
            // Treated the same as "not found" by the caller — which defers cache-root resolution
            // to the post-Format fallback in RunAsync.
        }
        return null;
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

            // Steps that go quiet (DISM commit, reagentc, a stalled download) produce no progress
            // reports, so liveness needs its own cadence — see SessionHeartbeatCoordinator.
            _heartbeatCoordinator = new Services.SessionHeartbeatCoordinator(
                _gatewayClient,
                _sessionId,
                _loggerFactory.CreateLogger<Services.SessionHeartbeatCoordinator>());
            _heartbeatCoordinator.Start();

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

            // No dedicated CACHE-labelled USB partition was found when this view model was
            // constructed — e.g. WinPE running in a Hyper-V VM with no physical USB attached, an
            // ISO/PXE boot, or dev/test. Fall back to the freshly-formatted target Windows volume,
            // which is guaranteed to exist with ample free space, unlike the WinPE boot RAM disk
            // (X:). This mirrors ConfigMgr OSD's SMSTSLocalDataDrive behavior: when the boot
            // media's own cache location is unusable or too small, the client falls back to a real
            // local fixed disk rather than the tiny WinPE RAM disk.
            if (_cacheRoot is null)
            {
                _cacheRoot = Path.Combine($"{diskFormat.WindowsVolume}\\", "CloudImagingCache");
                _cacheRootIsTargetVolume = true;
            }

            // ── Download OS image ──────────────────────────────────────────────
            _progress.StatusMessage = "Downloading operating system image…";
            _progress.UpdateStep(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress);

            // The persistent cache only pays off on durable boot media, so it is skipped entirely
            // when the cache root is the target Windows volume (see _cacheRootIsTargetVolume). The
            // WIM is still downloaded to that volume and still hash-verified — ImageApplyService /
            // RecoveryImageService verify SHA-256 before DISM runs, independently of the cache.
            Services.ImageCacheService? cache = null;
            if (!_cacheRootIsTargetVolume)
            {
                var cacheService = new Services.ImageCacheService(_cacheRoot, _loggerFactory.CreateLogger<Services.ImageCacheService>());
                var cacheMaintenance = new Services.ImageCacheMaintenanceService(cacheService, _loggerFactory.CreateLogger<Services.ImageCacheMaintenanceService>());
                await cacheMaintenance.RunAsync(ct);
                if (cacheMaintenance.CacheWriteEnabled) cache = cacheService;
            }

            var downloadService = new Services.ImageDownloadService(
                new HttpClient(),
                pipelineLoggerFactory.CreateLogger<Services.ImageDownloadService>(),
                cache);

            var destinationPath = Path.Combine(_cacheRoot, $"ci-image-{_sessionId:N}.wim");

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
                        _progress.StepPercent = pct;
                        _ = reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    onBytesProgress: _progress.SetTransferProgress,
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
                        _progress.StepPercent = pct;
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

            var driveLetterService = new Services.OfflineDriveLetterService(pipelineLoggerFactory.CreateLogger<Services.OfflineDriveLetterService>());
            var bootConfigService = new Services.BootConfigurationService(pipelineLoggerFactory.CreateLogger<Services.BootConfigurationService>());
            try
            {
                // The Windows partition is mounted under whichever letter WinPE had spare (often
                // something like H:), and a custom-captured image can carry the letter mappings of
                // the machine it came from. Re-point the applied image's own mount-manager state at
                // C: before writing boot files, so the device boots as C: — see
                // OfflineDriveLetterService for why this is not automatic.
                await driveLetterService.EnsureWindowsVolumeBootsAsCAsync(diskFormat.WindowsVolume, ct);

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

            var recoveryDestinationPath = Path.Combine(_cacheRoot, $"ci-recovery-{_sessionId:N}.wim");
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
                        _progress.StepPercent = pct;
                        _ = reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    onBytesProgress: _progress.SetTransferProgress,
                    ct: ct);
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyRecoveryImage, "REC", ex.Message, ct);
                return;
            }

            _progress.StatusMessage = "Applying recovery image…";
            // Second phase of the same step: DISM reports nothing here, so empty the bar rather
            // than leaving it full from the download that just finished.
            _progress.ResetStepProgress();
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
            _heartbeatCoordinator?.Dispose();
            CleanUpTargetVolumeCacheRoot();
        }
    }

    /// <summary>
    /// Removes the staging directory when it lives on the target Windows volume, so the machine
    /// does not boot into its new OS with a multi-GB <c>\CloudImagingCache</c> folder at the root
    /// of the system drive. No-op when the cache root is the USB CACHE partition (which is meant
    /// to retain images across sessions) or an explicit test override. Resetting the field lets a
    /// Retry re-resolve it against the volume its own Format step produces.
    /// </summary>
    private void CleanUpTargetVolumeCacheRoot()
    {
        if (!_cacheRootIsTargetVolume || _cacheRoot is null) return;

        try
        {
            if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: leftover scratch files must never fail an otherwise successful image.
        }

        _cacheRoot = null;
        _cacheRootIsTargetVolume = false;
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

        // Cut at the nearest preceding whitespace instead of hard mid-word (a raw character cut
        // produced things like "DiskPar…", which reads as broken rather than intentionally
        // summarized).
        var truncated = errorDetail[..MaxOnScreenErrorDetailChars];
        var lastBreak = truncated.LastIndexOfAny([' ', '\n', '\r', '\t']);
        if (lastBreak > 0)
        {
            truncated = truncated[..lastBreak];
        }

        return truncated.TrimEnd() + "…\n\nSee \"View Log\" for the full detail.";
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

    public void Dispose()
    {
        _sasCoordinator?.Dispose();
        _heartbeatCoordinator?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "==== Starting imaging pipeline for session {SessionId} ====")]
    private static partial void LogPipelineStarting(ILogger logger, Guid sessionId);
}

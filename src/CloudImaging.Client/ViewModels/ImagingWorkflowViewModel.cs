using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// Orchestrates the Format -> Download -> Apply -> Configure Boot -> Apply Recovery imaging
/// pipeline once a session has (or reaches) an OS image assignment. Drives
/// <see cref="ProgressViewModel"/>, reports step progress back to the Device Gateway API via
/// <see cref="Services.ImagingProgressReporter"/>, and navigates to <see cref="Views.ResultsView"/> on
/// completion, failure, or an unexpected terminal transition
/// (T056, FR-005, FR-006, FR-007, FR-008, FR-009d).
///
/// Previously, none of <see cref="Services.ImageDownloadService"/>, <see cref="Services.ImageApplyService"/>,
/// <see cref="Services.ImagingProgressReporter"/>, <see cref="Services.SasRefreshCoordinator"/>,
/// <see cref="Services.ImageCacheService"/>, or <see cref="Services.ImageCacheMaintenanceService"/> were
/// ever instantiated by the running application: this type is what wires them together.
///
/// The disk is now partitioned per the admin-configured <see cref="PartitioningScheme"/>
/// snapshotted onto the session (ESP/MSR/Windows/Recovery), boot files are configured with
/// <c>bcdboot</c>, and the WinRE recovery image is downloaded and applied: turning what was
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
    // and falls back to the freshly-formatted target Windows volume (see RunAsync): never to
    // Path.GetTempPath(), which is the WinPE boot RAM disk (X:) and has nowhere near enough space
    // for a multi-GB OS image.
    private string? _cacheRoot;

    // True when _cacheRoot resolved to the target Windows volume rather than to durable boot
    // media. That location is only ever scratch space: it is wiped by the next session's Format
    // step, so persisting a cross-session cache there is worthless: and it would ship a
    // multi-GB duplicate of the WIM inside the OS that is about to be handed to the end user.
    // Drives both "don't persist to the cache" and "delete the staged WIMs afterwards" below.
    private bool _cacheRootIsTargetVolume;

    private Services.SasRefreshCoordinator? _sasCoordinator;
    private Services.SessionHeartbeatCoordinator? _heartbeatCoordinator;
    private Services.SessionStatusResponse _status;

    /// <summary>Builds the pipeline and immediately starts running it in the background.</summary>
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
        _gatewayClient = gatewayClient;
        _sessionId = sessionId;
        _deviceSerialNumber = deviceSerialNumber;
        _status = initialStatus;
        _progress = progress;
        _navigateToResults = navigateToResults;
        _loggerFactory = loggerFactory;

        // Resolve immediately if possible: an explicit override (tests) always wins, otherwise try
        // the USB boot media's dedicated CACHE-labelled NTFS partition (created by
        // UsbPartitionProvisioningService during USB preparation, sized at a 24 GB minimum
        // specifically to hold one OS image, FR-055). If neither is available (e.g. WinPE
        // running in a Hyper-V VM with no physical USB attached, an ISO/PXE boot, or dev/test),
        // this stays null and is resolved later in RunAsync once the target Windows volume is
        // known (see the fallback there for why Path.GetTempPath() must never be the default).
        var cacheVolume = FindCacheVolumeDriveLetter();
        _usbCacheRoot = cacheVolume is not null ? Path.Combine(cacheVolume, "CloudImagingCache") : null;
        _cacheRoot = cacheRoot ?? _usbCacheRoot;
    }

    // Driver files are only cached on the USB drive's CACHE volume, never on the Windows volume fallback.
    private readonly string? _usbCacheRoot;

    /// <summary>
    /// Locates the CACHE-labelled NTFS volume this Client's USB boot media was prepared with:
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
                {
                    return letter;
                }
            }
        }
        catch (ManagementException)
        {
            // Treated the same as "not found" by the caller: which defers cache-root resolution
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

            var deviceArchitecture = MachineArchitecturePlatform.HostArchitecture();
            if (Services.ArchitecturePreflight.CheckOsImage(deviceArchitecture, _status.OsImageArchitecture) is { } architectureError)
            {
                await FailAsync(reporter, ImagingStepName.FormatDisk, "ARC", architectureError, ct);
                return;
            }

            // The setup profile is fetched before the disk is touched, so a device that cannot get
            // its profile fails with its existing OS intact rather than half-prepared.
            var setupProfile = await LoadSetupProfileAsync(reporter, ct);
            if (setupProfile is null)
            {
                return;
            }

            // Install drivers hands Windows setup work (Specialize installers) over to Prepare setup.
            IReadOnlyList<Services.Setup.SpecializeInstaller> specializeInstallers = [];
            var runPrepareSetup = ShouldRunPrepareSetup(setupProfile.Status, specializeInstallers);
            _progress.ConfigureSteps(runPrepareSetup);
            var setupWarnings = 0;

            _sasCoordinator = new Services.SasRefreshCoordinator(
                _gatewayClient,
                _sessionId,
                _status.SasTokenUrl!,
                ParseExpiry(_status.SasTokenUrlExpiresAt),
                _loggerFactory.CreateLogger<Services.SasRefreshCoordinator>());
            _sasCoordinator.Start();

            // Steps that go quiet (DISM commit, reagentc, a stalled download) produce no progress
            // reports, so liveness needs its own cadence: see SessionHeartbeatCoordinator.
            _heartbeatCoordinator = new Services.SessionHeartbeatCoordinator(
                _gatewayClient,
                _sessionId,
                _loggerFactory.CreateLogger<Services.SessionHeartbeatCoordinator>());
            _heartbeatCoordinator.Start();

            // Every Information-level log message the step services below emit (disk selection,
            // "Starting DISM: ...", "Starting bcdboot ...", "Starting reagentc.exe ...", hash
            // verification, etc.) is also surfaced in ProgressView's activity ticker, in addition
            // to the rolling file log: FR-007's "light details around what's currently going on".
            var pipelineLoggerFactory = new Services.ProgressActivityLoggerFactory(_loggerFactory, _progress.AppendActivity);

            var hash = _status.Sha256Hash ?? string.Empty;
            var scheme = _status.PartitioningScheme ?? PartitioningScheme.Default;

            // The disk layout and boot files must match how the firmware booted WinPE: a GPT disk
            // never boots in Legacy BIOS mode, and an MBR disk never boots in UEFI mode.
            var firmwareMode = new Services.DevicePostureDetector(_loggerFactory.CreateLogger<Services.DevicePostureDetector>()).DetectFirmwareMode();
            var activityLogger = pipelineLoggerFactory.CreateLogger<ImagingWorkflowViewModel>();
            switch (firmwareMode)
            {
                case FirmwareMode.Uefi:
                    LogFirmwareModeUefi(activityLogger);
                    break;
                case FirmwareMode.LegacyBios:
                    LogFirmwareModeLegacy(activityLogger);
                    break;
                default:
                    await FailAsync(reporter, ImagingStepName.FormatDisk, "FWM", "The firmware mode (UEFI or Legacy BIOS) could not be detected, so the disk layout cannot be chosen. The disk was not changed.", ct);
                    return;
            }

            // ── Format ──────────────────────────────────────────────────────────
            _progress.StatusMessage = "Preparing the target disk…";
            _progress.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress);
            await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress, ct: ct);

            Services.DiskFormatResult diskFormat;
            try
            {
                var formatService = new Services.DiskFormatService(pipelineLoggerFactory.CreateLogger<Services.DiskFormatService>());
                diskFormat = await formatService.FormatTargetDiskAsync(scheme, firmwareMode, ct);
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
            // constructed: e.g. WinPE running in a Hyper-V VM with no physical USB attached, an
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
            // WIM is still downloaded to that volume and still hash-verified: ImageApplyService /
            // RecoveryImageService verify SHA-256 before DISM runs, independently of the cache.
            Services.ImageCacheService? cache = null;
            if (!_cacheRootIsTargetVolume)
            {
                var cacheService = new Services.ImageCacheService(_cacheRoot, _loggerFactory.CreateLogger<Services.ImageCacheService>());
                var cacheMaintenance = new Services.ImageCacheMaintenanceService(cacheService, _loggerFactory.CreateLogger<Services.ImageCacheMaintenanceService>());
                await cacheMaintenance.RunAsync(ct);
                if (cacheMaintenance.CacheWriteEnabled)
                {
                    cache = cacheService;
                }
            }

            var downloadService = new Services.ImageDownloadService(
                new HttpClient(),
                pipelineLoggerFactory.CreateLogger<Services.ImageDownloadService>(),
                cache);

            // USB devices under the recommended size have a small cache partition. When an image
            // does not fit, stage it uncached on the target volume, like boot media without a cache.
            async Task<string> DownloadWimAsync(string imageHash, Func<string> sasUrl, string fileName, Action<int> onProgress)
            {
                try
                {
                    return await downloadService.EnsureLocalWimAsync(imageHash, imageHash, sasUrl(), Path.Combine(_cacheRoot!, fileName), onProgress, _progress.SetTransferProgress, ct);
                }
                catch (Services.InsufficientDiskSpaceException ex) when (!_cacheRootIsTargetVolume)
                {
                    LogCachePartitionTooSmall(pipelineLoggerFactory.CreateLogger<ImagingWorkflowViewModel>(), ex.RequiredBytes, ex.AvailableBytes);
                    _cacheRoot = Path.Combine($"{diskFormat.WindowsVolume}\\", "CloudImagingCache");
                    _cacheRootIsTargetVolume = true;
                    downloadService = new Services.ImageDownloadService(new HttpClient(), pipelineLoggerFactory.CreateLogger<Services.ImageDownloadService>());
                    return await downloadService.EnsureLocalWimAsync(imageHash, imageHash, sasUrl(), Path.Combine(_cacheRoot, fileName), onProgress, _progress.SetTransferProgress, ct);
                }
            }

            string wimPath;
            try
            {
                wimPath = await DownloadWimAsync(
                    hash,
                    () => _sasCoordinator.CurrentSasUrl ?? _status.SasTokenUrl!,
                    $"ci-image-{_sessionId:N}.wim",
                    pct =>
                    {
                        _progress.OverallPercent = 8 + (int)(pct * 0.47); // 8-55%
                        _progress.StepPercent = pct;
                        _ = reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    });
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
            _progress.StatusMessage = "Verifying operating system image…";
            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);

            // Tells the portal the step has started. Verification of a multi-gigabyte image runs
            // before DISM emits anything, so without this the session reports no progress at all
            // for the whole of what is often the longest wait in the pipeline.
            await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress, ct: ct);

            var applyService = new Services.ImageApplyService(pipelineLoggerFactory.CreateLogger<Services.ImageApplyService>());
            try
            {
                await applyService.ApplyAsync(
                    wimPath,
                    hash,
                    diskFormat.WindowsVolume,
                    onProgress: pct =>
                    {
                        _progress.StatusMessage = "Applying operating system image…";
                        _progress.OverallPercent = 55 + (int)(pct * 0.25); // 55-80%
                        _progress.StepPercent = pct;
                        _ = reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    },
                    ct: ct,
                    // Verification has its own bar rather than sharing the apply percentage: the two
                    // measure different work, and a bar that filled twice would read as a restart.
                    onVerifyProgress: pct =>
                    {
                        _progress.StatusMessage = $"Verifying operating system image… {pct}%";
                        _progress.StepPercent = pct;
                    });
            }
            catch (Exception ex)
            {
                await FailAsync(reporter, ImagingStepName.ApplyImage, "APL", ex.Message, ct);
                return;
            }

            _progress.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.Completed);
            await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.Completed, ct: ct);
            _progress.OverallPercent = 80;

            // ── Install drivers ─────────────────────────────────────────────────
            var drivers = await RunInstallDriversAsync(reporter, setupProfile, diskFormat.WindowsVolume, pipelineLoggerFactory, ct);
            if (drivers is null)
            {
                return;
            }

            specializeInstallers = drivers;
            runPrepareSetup = ShouldRunPrepareSetup(setupProfile.Status, specializeInstallers);

            // Driver installers can add Prepare setup to a session without a profile, so the step list is refreshed.
            _progress.ConfigureSteps(runPrepareSetup, includeInstallDrivers: _progress.Steps.Any(s => s.Step == ImagingStepName.InstallDrivers));

            // ── Prepare setup ───────────────────────────────────────────────────
            if (runPrepareSetup)
            {
                var result = await RunPrepareSetupAsync(reporter, setupProfile, specializeInstallers, diskFormat.WindowsVolume, deviceArchitecture, pipelineLoggerFactory, ct);
                if (result is null)
                {
                    return;
                }
                setupWarnings = result.WarningCount;
                _progress.OverallPercent = 82;
            }

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
                // C: before writing boot files, so the device boots as C:: see
                // OfflineDriveLetterService for why this is not automatic.
                await driveLetterService.EnsureWindowsVolumeBootsAsCAsync(diskFormat.WindowsVolume, ct);

                await bootConfigService.ConfigureAsync(diskFormat.WindowsVolume, diskFormat.SystemVolume, firmwareMode, ct);
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

            var recoveryInfo = await _gatewayClient.GetLatestRecoveryImageAsync(deviceArchitecture, ct);
            // A mismatched WinRE would leave the OS unrecoverable; the OS image's own embedded WinRE always matches.
            if (recoveryInfo is not null && !Services.ArchitecturePreflight.IsRecoveryImageCompatible(deviceArchitecture, recoveryInfo.Architecture))
            {
                recoveryInfo = null;
            }

            var recoveryService = new Services.RecoveryImageService(pipelineLoggerFactory.CreateLogger<Services.RecoveryImageService>());

            if (recoveryInfo is null)
            {
                // No custom Recovery Image has been published to the Portal catalog: fall back
                // to the WinRE that the OS image already carries at
                // Windows\System32\Recovery\Winre.wim rather than failing the whole session.
                _progress.StatusMessage = "No published recovery image. Reusing the OS image's embedded recovery environment…";
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

                _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, SuccessNote(setupWarnings), null);
                return;
            }

            string recoveryWimPath;
            try
            {
                recoveryWimPath = await DownloadWimAsync(
                    recoveryInfo.Sha256Hash,
                    () => recoveryInfo.SasTokenUrl,
                    $"ci-recovery-{_sessionId:N}.wim",
                    pct =>
                    {
                        _progress.OverallPercent = 85 + (int)(pct * 0.10); // 85-95%
                        _progress.StepPercent = pct;
                        _ = reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, pct, ct: ct);
                    });
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

            _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, SuccessNote(setupWarnings), null);
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

    private static string? SuccessNote(int setupWarnings) =>
        setupWarnings > 0 ? ResultsViewModel.WarningsMessage(setupWarnings) : null;

    /// <summary>Prepare setup runs for a pinned profile, or for Windows setup work Install drivers handed over.</summary>
    internal static bool ShouldRunPrepareSetup(Services.Setup.SetupProfileLoadStatus profileStatus, IReadOnlyList<Services.Setup.SpecializeInstaller> specializeInstallers) =>
        profileStatus == Services.Setup.SetupProfileLoadStatus.Loaded || specializeInstallers.Count > 0;

    /// <summary>What Prepare setup works from; the handed-over installers pass through as they are.</summary>
    internal static Services.Setup.PrepareSetupInput BuildPrepareSetupInput(
        Services.Setup.SetupProfileLoadResult setupProfile,
        IReadOnlyList<Services.Setup.SpecializeInstaller> specializeInstallers,
        string volumeRoot,
        MachineArchitecture deviceArchitecture,
        Guid sessionId,
        IReadOnlyDictionary<string, string>? scriptFiles = null) =>
        new(setupProfile.Descriptor?.ProfileName, setupProfile.Content, specializeInstallers, volumeRoot, deviceArchitecture)
        {
            SessionId = sessionId,
            ProfileVersion = setupProfile.Descriptor?.Version,
            ScriptFiles = scriptFiles ?? new Dictionary<string, string>(),
            AnswerFileText = setupProfile.AnswerFileText,
        };

    /// <summary>
    /// Loads the session's setup profile before Format disk. Returns null when the pipeline must
    /// stop: the profile could not be loaded (reported as a failed Prepare setup step, with no
    /// items and the disk untouched), or the session is already ending.
    /// </summary>
    private async Task<Services.Setup.SetupProfileLoadResult?> LoadSetupProfileAsync(Services.ImagingProgressReporter reporter, CancellationToken ct)
    {
        _progress.StatusMessage = "Checking for a setup profile…";
        Services.Setup.SetupProfileLoadResult result;
        using (var download = new HttpClient())
        {
            var loader = new Services.Setup.SetupProfileContentLoader(_gatewayClient, download, _loggerFactory.CreateLogger<Services.Setup.SetupProfileContentLoader>());
            result = await loader.LoadAsync(_sessionId, ct);
        }

        switch (result.Status)
        {
            case Services.Setup.SetupProfileLoadStatus.Failed:
                _progress.ConfigureSteps(includePrepareSetup: true);
                await FailAsync(reporter, ImagingStepName.PrepareSetup, "SPF", result.FailureMessage ?? Services.Setup.SetupProfileContentLoader.DownloadFailedMessage, ct);
                return null;
            case Services.Setup.SetupProfileLoadStatus.PipelineEnding:
                try
                {
                    _status = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct) ?? _status;
                }
                catch (Exception ex) when (ex is HttpRequestException or Services.DeviceGatewayApiException)
                {
                    // Fall through with the last known status.
                }
                if (!HandleTerminalState())
                {
                    await FailAsync(reporter, null, "SPF", "The session ended before imaging started.", ct);
                }
                return null;
            default:
                return result;
        }
    }

    /// <summary>
    /// Runs the Install drivers step after Apply image (contracts/client-install-drivers.md). The
    /// step is shown only when the Device Gateway returns a driver set (with components or a skip
    /// note). Returns the installers handed to Prepare setup, or null when the pipeline stopped: the
    /// descriptor could not be read after 3 attempts, a critical component failed, or the session
    /// is ending.
    /// </summary>
    private async Task<IReadOnlyList<Services.Setup.SpecializeInstaller>?> RunInstallDriversAsync(Services.ImagingProgressReporter reporter, Services.Setup.SetupProfileLoadResult setupProfile, string windowsVolume, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        using var download = new HttpClient();
        var logger = loggerFactory.CreateLogger<Services.Drivers.DriverInstallService>();
        var loader = new Services.Drivers.DriverSetLoader(_gatewayClient, download, logger);
        var loaded = await loader.LoadAsync(_sessionId, ct);
        switch (loaded.Status)
        {
            case Services.Drivers.DriverSetLoadStatus.NotShown:
                return [];
            case Services.Drivers.DriverSetLoadStatus.PipelineEnding:
                await StopForEndingSessionAsync(reporter, ct);
                return null;
            case Services.Drivers.DriverSetLoadStatus.Failed:
                _progress.ConfigureSteps(ShouldRunPrepareSetup(setupProfile.Status, []), includeInstallDrivers: true);
                await FailAsync(reporter, ImagingStepName.InstallDrivers, "DRV", Services.Drivers.DriverSetLoader.DescriptorFailedMessage, ct);
                return null;
        }

        var descriptor = loaded.Descriptor!;
        _progress.ConfigureSteps(ShouldRunPrepareSetup(setupProfile.Status, []), includeInstallDrivers: true);
        _progress.ResetStepProgress();
        _progress.StatusMessage = "Installing drivers...";
        _progress.UpdateStep(ImagingStepName.InstallDrivers, ImagingStepStatus.InProgress);
        await reporter.ReportAsync(ImagingStepName.InstallDrivers, ImagingStepStatus.InProgress, ct: ct);

        var processes = new Services.Setup.RestrictedProcessRunner();
        var service = new Services.Drivers.DriverInstallService(
            loader,
            [new Services.Drivers.HpSoftPaqExtractor(processes, logger), new Services.Drivers.DellPackageExtractor(processes, logger), new Services.Drivers.ZipPackageExtractor()],
            new Services.Drivers.DismDriverInjector(processes, logger),
            logger,
            cache: new Services.Drivers.DriverFileCache(_usbCacheRoot, logger));
        var result = await service.RunAsync(_sessionId, descriptor, windowsVolume, _gatewayClient.DeviceManufacturer, update =>
        {
            // Same as Apply image: progress setters are safe from a background thread, and the
            // reporter throttles percent updates to the gateway.
            _progress.StatusMessage = update.Status;
            _progress.StepPercent = update.Percent;
            _ = reporter.ReportAsync(ImagingStepName.InstallDrivers, ImagingStepStatus.InProgress, update.Percent, ct: ct);
        }, ct);

        await reporter.ReportResultAsync(ImagingStepName.InstallDrivers, result.Status, result.WarningCount, result.Items, result.ErrorDetail, result.StatusNote, ct);
        if (result.Status == ImagingStepStatus.Failed)
        {
            await FailAsync(reporter, ImagingStepName.InstallDrivers, "DRV", result.ErrorDetail ?? "Critical drivers could not be installed.", ct, alreadyReported: true);
            return null;
        }

        _progress.UpdateStep(ImagingStepName.InstallDrivers, ImagingStepStatus.Completed, result.WarningCount);
        if (result.StatusNote is { } note)
        {
            _progress.AppendActivity(note);
        }

        _progress.OverallPercent = 81;
        return result.Installers;
    }

    private async Task StopForEndingSessionAsync(Services.ImagingProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            _status = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct) ?? _status;
        }
        catch (Exception ex) when (ex is HttpRequestException or Services.DeviceGatewayApiException)
        {
            // Fall through with the last known status.
        }

        if (!HandleTerminalState())
        {
            await FailAsync(reporter, ImagingStepName.InstallDrivers, "DRV", "The session ended while drivers were being installed.", ct);
        }
    }

    /// <summary>
    /// Runs Prepare setup on the applied volume and reports its result with every item in one
    /// call. Returns null when the step failed and the pipeline has stopped.
    /// </summary>
    private async Task<Services.Setup.PrepareSetupResult?> RunPrepareSetupAsync(
        Services.ImagingProgressReporter reporter,
        Services.Setup.SetupProfileLoadResult setupProfile,
        IReadOnlyList<Services.Setup.SpecializeInstaller> specializeInstallers,
        string windowsVolume,
        MachineArchitecture deviceArchitecture,
        ILoggerFactory pipelineLoggerFactory,
        CancellationToken ct)
    {
        _progress.StatusMessage = "Preparing Windows setup…";
        _progress.UpdateStep(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress);
        await reporter.ReportAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress, ct: ct);

        var volumeRoot = $"{windowsVolume.TrimEnd('\\')}\\";
        IReadOnlyDictionary<string, string>? scriptFiles = null;
        if (setupProfile.Content is { Scripts.Count: > 0 })
        {
            // Verified before Format disk; fetched again here straight to the volume, so the texts are never held in memory.
            _progress.StatusMessage = "Downloading scripts…";
            using var download = new HttpClient();
            var loader = new Services.Setup.SetupProfileContentLoader(_gatewayClient, download, pipelineLoggerFactory.CreateLogger<Services.Setup.SetupProfileContentLoader>());
            scriptFiles = await loader.DownloadScriptsAsync(_sessionId, setupProfile, new Services.Setup.SetupFileSystem(volumeRoot), (number, total) =>
                _progress.StatusMessage = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Downloading scripts ({number} of {total})"), ct);
        }
        var input = BuildPrepareSetupInput(setupProfile, specializeInstallers, volumeRoot, deviceArchitecture, _sessionId, scriptFiles);
        var lastLine = string.Empty;
        Services.Setup.PrepareSetupResult result;
        try
        {
            // Hive loads, scripts and file writes are synchronous work on the disk just written to;
            // keep them off the UI thread, which only receives status lines and percent.
            result = await Task.Run(() =>
            {
                using var hive = new Services.Setup.OfflineRegistryHive();
                // Script output goes to the file log only; the activity ticker shows one line at a time.
                var scripts = new Services.Setup.WinPeScriptRunner(
                    new Services.Setup.RestrictedProcessRunner(),
                    new Services.Setup.SetupFileSystem(volumeRoot),
                    () => Services.BootImageCapabilities.SupportsPowerShell(),
                    _loggerFactory.CreateLogger<Services.Setup.WinPeScriptRunner>());
                var service = new Services.Setup.PrepareSetupService(
                    hive,
                    new Services.Setup.SetupFileSystem(volumeRoot),
                    scripts,
                    () => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SetupRunner", Services.Setup.UnattendComposer.RunnerScriptName)),
                    TimeProvider.System,
                    pipelineLoggerFactory.CreateLogger<Services.Setup.PrepareSetupService>());
                return service.Run(input, (line, percent) =>
                {
                    if (line != lastLine)
                    {
                        lastLine = line;
                        _progress.StatusMessage = line;
                    }
                    _progress.StepPercent = percent;
                    _ = reporter.ReportAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress, percent, ct: ct);
                }, ct);
            }, ct);
        }
        catch (Exception ex)
        {
            // The service removes the downloaded texts itself, unless it failed before it started.
            if (scriptFiles is { Count: > 0 })
            {
                try
                {
                    new Services.Setup.SetupFileSystem(volumeRoot).DeleteTree(Services.Setup.WinPeScriptRunner.WorkFolder);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // The failed session is retried from Format disk, which erases the volume.
                }
            }
            await FailAsync(reporter, ImagingStepName.PrepareSetup, "PSU", ex.Message, ct);
            return null;
        }

        await reporter.ReportResultAsync(ImagingStepName.PrepareSetup, result.Status, result.WarningCount, result.Items, result.ErrorDetail, ct: ct);
        if (result.Status == ImagingStepStatus.Failed)
        {
            await FailAsync(reporter, ImagingStepName.PrepareSetup, "PSU", result.ErrorDetail ?? "Prepare setup failed.", ct, alreadyReported: true);
            return null;
        }

        _progress.UpdateStep(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed, result.WarningCount);
        return result;
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
        if (!_cacheRootIsTargetVolume || _cacheRoot is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
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
    /// navigated to <see cref="Views.ResultsView"/>) when the caller should stop; <c>true</c> when
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

            if (HandleTerminalState())
            {
                return false;
            }

            try { await Task.Delay(AssignmentPollInterval, ct); }
            catch (OperationCanceledException) { return false; }

            var next = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct);
            if (next is not null)
            {
                _status = next;
            }
        }

        FireAndForgetLogUpload();
        _navigateToResults(
            ResultsViewModel.Outcome.Failure,
            _deviceSerialNumber,
            "Timed out waiting for an OS image assignment.",
            SupportReferenceCode.ForClient(_sessionId, "REG").ToString());
        return false;
    }

    /// <summary>
    /// Navigates to the Results screen when <see cref="_status"/> is terminal. Returns true when it
    /// did, so the caller stops.
    /// </summary>
    private bool HandleTerminalState()
    {
        if (!Enum.TryParse<SessionState>(_status.State ?? string.Empty, ignoreCase: true, out var state))
        {
            state = SessionState.SessionInit;
        }

        switch (state)
        {
            case SessionState.SessionCompleted:
                _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
                return true;
            case SessionState.SessionFailed:
                FireAndForgetLogUpload();
                _navigateToResults(
                    ResultsViewModel.Outcome.Failure,
                    _deviceSerialNumber,
                    "The session failed before an image could be applied.",
                    null);
                return true;
            case SessionState.SessionNotAuthorized:
                FireAndForgetLogUpload();
                _navigateToResults(ResultsViewModel.Outcome.NotAuthorized, _deviceSerialNumber, null, null);
                return true;
            default:
                return false;
        }
    }

    private async Task FailAsync(
        Services.ImagingProgressReporter reporter,
        ImagingStepName? step,
        string stageCode,
        string errorDetail,
        CancellationToken ct,
        bool alreadyReported = false)
    {
        var code = SupportReferenceCode.ForClient(_sessionId, stageCode).ToString();

        if (step is { } s)
        {
            _progress.UpdateStep(s, ImagingStepStatus.Failed);
            if (!alreadyReported)
            {
                try { await reporter.ReportAsync(s, ImagingStepStatus.Failed, errorDetail: errorDetail, ct: ct); }
                catch { /* best-effort: progress reporting must never mask the real failure */ }
            }
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
    /// without awaiting it: the failure Results view must appear immediately regardless of
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

    /// <summary>Stops the background SAS refresh and session heartbeat coordinators.</summary>
    public void Dispose()
    {
        _sasCoordinator?.Dispose();
        _heartbeatCoordinator?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "==== Starting imaging pipeline for session {SessionId} ====")]
    private static partial void LogPipelineStarting(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firmware mode detected: UEFI. Using GPT layout with UEFI boot files.")]
    private static partial void LogFirmwareModeUefi(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firmware mode detected: Legacy BIOS (CSM). Using MBR layout with BIOS boot files.")]
    private static partial void LogFirmwareModeLegacy(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The USB cache partition has {AvailableBytes} bytes free but the image needs {RequiredBytes}. Downloading to the target disk instead, without caching.")]
    private static partial void LogCachePartitionTooSmall(ILogger logger, long requiredBytes, long availableBytes);
}

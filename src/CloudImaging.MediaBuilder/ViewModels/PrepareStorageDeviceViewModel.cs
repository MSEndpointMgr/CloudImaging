using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the PrepareStorageDeviceView (FR-053, FR-054, FR-055, FR-056, FR-057).
///
/// Orchestrates the "Prepare USB Storage Device" workflow over the existing services:
///   1. Refresh   — list published boot images (Operator API) and enumerate local disks (WMI).
///   2. Validate  — block non-USB / non-removable / system disks (FR-054).
///   3. Prepare   — request SAS, download + hash-verify the WIM (FR-056), provision the two
///                  partitions (FR-055, DESTRUCTIVE), then deploy the boot image and configure
///                  WinPE auto-start (FR-057).
///
/// The destructive step requires an explicit confirmation gate (<see cref="ConfirmErase"/>).
/// </summary>
public sealed class PrepareStorageDeviceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OperatorApiClient _operatorApi;
    private readonly EntraAuthenticationService _authService;
    private readonly UsbSafetyValidationService _validator;
    private readonly BootImageDownloadService _downloader;
    private readonly UsbPreparationService _preparation;
    private readonly BootImageCacheService _cache;
    private readonly IsoGenerationService? _isoGeneration;
    private readonly UsbDeviceChangeWatcher? _deviceWatcher;
    private readonly Action _navigateBack;

    private BootImageChoice? _selectedBootImage;
    private DiskChoice? _selectedDisk;
    private bool _confirmErase;
    private bool _prepareAsIso;
    private string? _isoOutputPath = Path.Combine(GetDefaultIsoOutputFolder(), "cloud-imaging-boot.iso");
    // True once the technician has explicitly chosen an ISO path via BrowseIsoOutputCommand —
    // from that point on their choice of folder/filename is respected as-is, even if they later
    // pick a different boot image. Until then, IsoOutputPath is a suggested default that stays
    // in sync with SelectedBootImage so it is already version-specific without requiring a
    // Browse click (matching the name Browse itself would suggest).
    private bool _isoOutputPathCustomized;
    private bool _isBusy;
    private bool _isRefreshingBootImages;
    private bool _isComplete;
    private int _progressPercent;
    private string _progressMessage = string.Empty;
    private string _statusMessage = "Select a boot image and a USB device to begin.";
    private string? _resultFilePath;
    private string? _errorMessage;
    private string _errorTitle = "Preparation failed";
    private bool _wasCancelled;
    private CancellationTokenSource? _cts;
    /// <summary>
    /// Which stage of the destructive workflow is currently executing, so a failure can be
    /// attributed to the right support-reference stage code (T160/FR-058): DVI (disk
    /// validation), BID (boot image download), PRT (partitioning), BCF (boot config/deploy),
    /// ISO (ISO packaging).
    /// </summary>
    private string _currentStage = "DVI";

    public PrepareStorageDeviceViewModel(
        OperatorApiClient operatorApi,
        EntraAuthenticationService authService,
        UsbSafetyValidationService validator,
        BootImageDownloadService downloader,
        UsbPreparationService preparation,
        BootImageCacheService cache,
        Action navigateBack,
        UsbDeviceChangeWatcher? deviceWatcher = null,
        IsoGenerationService? isoGeneration = null,
        Func<bool>? isAdkInstalled = null)
    {
        _operatorApi   = operatorApi;
        _authService   = authService;
        _validator     = validator;
        _downloader    = downloader;
        _preparation   = preparation;
        _cache         = cache;
        _isoGeneration = isoGeneration;
        _navigateBack  = navigateBack;
        _deviceWatcher = deviceWatcher;

        // Generating an ISO needs the same Windows ADK (WinPE add-on) as Generate Boot Image —
        // gated the same way (Func<bool> injection point for tests), and additionally requires
        // an IsoGenerationService instance to actually be wired up by the caller.
        IsIsoGenerationAvailable = _isoGeneration is not null && (isAdkInstalled ?? IsoGenerationService.IsAdkInstalled)();

        _downloader.ProgressChanged   += OnDownloadProgress;
        _preparation.ProgressChanged  += OnPreparationProgress;

        // T071/FR-054: auto-refresh the disk list when a USB device is plugged/unplugged,
        // instead of relying solely on the manual Refresh button. Never disrupts an
        // in-progress destructive operation (guarded by IsBusy in OnDeviceChanged).
        if (_deviceWatcher is not null)
        {
            _deviceWatcher.DeviceChanged += OnDeviceChanged;
            _deviceWatcher.Start();
        }

        RefreshCommand = new RelayCommand(async _ => await RefreshBootImagesAsync(), _ => !IsBusy && !IsRefreshingBootImages);
        PrepareCommand = new RelayCommand(async _ => await PrepareAsync(), _ => CanPrepare);
        CancelCommand  = new RelayCommand(_ => Cancel(), _ => IsBusy);
        BackCommand    = new RelayCommand(_ => _navigateBack());
        DismissResultCommand = new RelayCommand(_ => CloseResultDialog());
        BrowseIsoOutputCommand = new RelayCommand(_ => BrowseIsoOutput());
        OpenResultFolderCommand = new RelayCommand(_ => OpenResultFolder(), _ => ResultFilePath is not null);

        // Load the eligible USB disk list (fast, local WMI) and kick off the published boot
        // image list from the portal as soon as this view is reached, instead of requiring a
        // manual Refresh click first. The Refresh button next to the boot image dropdown only
        // re-fetches boot images (FR-053) — disks already refresh automatically on USB
        // plug/unplug via _deviceWatcher (OnDeviceChanged), so re-scanning them here too would
        // just be redundant work that briefly toggles IsBusy and flickers the whole page.
        try { LoadDisks(); } catch { /* best-effort; the disk list stays empty, no fallback needed */ }
        _ = RefreshBootImagesAsync();
    }

    public ObservableCollection<BootImageChoice> BootImages { get; } = [];
    public ObservableCollection<DiskChoice> Disks { get; } = [];

    public bool HasBootImages => BootImages.Count > 0;
    public bool HasDisks => Disks.Count > 0;

    public BootImageChoice? SelectedBootImage
    {
        get => _selectedBootImage;
        set
        {
            _selectedBootImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPrepare));

            // Keep the suggested ISO filename version-specific by default (FR: technicians who
            // never click Browse should still get "cloud-imaging-boot-v{version}.iso" instead of
            // the generic "cloud-imaging-boot.iso"), unless they've already picked a path
            // themselves via the Browse dialog.
            if (!_isoOutputPathCustomized)
            {
                var directory = !string.IsNullOrWhiteSpace(_isoOutputPath)
                    ? Path.GetDirectoryName(_isoOutputPath)
                    : null;
                if (string.IsNullOrWhiteSpace(directory))
                    directory = GetDefaultIsoOutputFolder();
                IsoOutputPath = Path.Combine(directory, GetDefaultIsoFileName());
            }
        }
    }

    public DiskChoice? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            _selectedDisk = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasValidationMessage));
            OnPropertyChanged(nameof(CanPrepare));
        }
    }

    public bool ConfirmErase
    {
        get => _confirmErase;
        set { _confirmErase = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPrepare)); }
    }

    /// <summary>
    /// True when the Windows ADK (WinPE add-on) needed to package an ISO is installed AND an
    /// <see cref="IsoGenerationService"/> was supplied — gates the "Generate ISO File" target
    /// option (disabled, not hidden, so it's discoverable with a reason) the same way
    /// <c>ShellViewModel.AdkAvailable</c> gates Generate Boot Image (FR-050a).
    /// </summary>
    public bool IsIsoGenerationAvailable { get; }

    /// <summary>Selects the "prepare a USB device" target. Mutually exclusive with <see cref="PrepareAsIso"/>.</summary>
    public bool PrepareAsUsb
    {
        get => !_prepareAsIso;
        set
        {
            if (!value || !_prepareAsIso) return;
            _prepareAsIso = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrepareAsIso));
            OnPropertyChanged(nameof(CanPrepare));
            OnPropertyChanged(nameof(PrepareButtonLabel));
            OnPropertyChanged(nameof(PrepareDescription));
            OnPropertyChanged(nameof(SuccessTitle));
        }
    }

    /// <summary>Selects the "generate a bootable ISO file" target (for Hyper-V VMs or physical media). Mutually exclusive with <see cref="PrepareAsUsb"/>.</summary>
    public bool PrepareAsIso
    {
        get => _prepareAsIso;
        set
        {
            if (!value || _prepareAsIso) return;
            _prepareAsIso = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrepareAsUsb));
            OnPropertyChanged(nameof(CanPrepare));
            OnPropertyChanged(nameof(PrepareButtonLabel));
            OnPropertyChanged(nameof(PrepareDescription));
            OnPropertyChanged(nameof(SuccessTitle));
        }
    }

    /// <summary>Destination path for the generated ISO file, chosen via <see cref="BrowseIsoOutputCommand"/>.</summary>
    public string? IsoOutputPath
    {
        get => _isoOutputPath;
        set { _isoOutputPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPrepare)); }
    }

    /// <summary>Footer action button label — changes with the selected target.</summary>
    public string PrepareButtonLabel => PrepareAsIso ? "Generate ISO File" : "Prepare USB Device";

    /// <summary>Footer caption explaining what the action button does — changes with the selected target.</summary>
    public string PrepareDescription => PrepareAsIso
        ? "Downloads (or reuses a cached copy of) the selected boot image and packages it into a bootable ISO file you can attach to a Hyper-V VM's DVD drive or burn to physical media."
        : "Downloads (or reuses a cached copy of) the selected boot image, partitions the USB device, and deploys WinPE with auto-start onto it.";

    /// <summary>Success modal title — changes with the selected target.</summary>
    public string SuccessTitle => PrepareAsIso ? "ISO file generated" : "USB device prepared";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPrepare));
            OnPropertyChanged(nameof(IsFormEnabled));
            OnPropertyChanged(nameof(IsBootImageSelectionEnabled));
            OnPropertyChanged(nameof(IsDiskSelectionEnabled));
            // RelayCommand.CanExecuteChanged is wired to CommandManager.RequerySuggested, which
            // WPF only re-raises automatically on certain UI input events (click, keypress,
            // focus change) — not when a bound property changes from a background/async
            // continuation. Without this, the Refresh/Prepare/Cancel buttons' enabled state can
            // visually lag behind their real CanExecute value until the user happens to
            // interact with the window.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// False while a Prepare operation is in flight (<see cref="IsBusy"/>) — disables the
    /// destructive-confirmation checkbox (and, via the more specific properties below, the boot
    /// image/disk pickers) so nothing about the target selection can change mid-operation.
    /// Re-enabled the instant the workflow reaches ANY terminal outcome — success, failure, or
    /// cancellation — not gated on the result modal being dismissed.
    /// </summary>
    public bool IsFormEnabled => !IsBusy;

    public bool IsBootImageSelectionEnabled => HasBootImages && !IsBusy;

    public bool IsDiskSelectionEnabled => HasDisks && !IsBusy;

    /// <summary>
    /// True only while the Refresh button's boot-image re-fetch is in flight. Deliberately
    /// separate from <see cref="IsBusy"/> (the destructive Prepare workflow's busy flag), so a
    /// quick boot-image refresh never shows/hides the Progress card and reflows the whole page.
    /// </summary>
    public bool IsRefreshingBootImages
    {
        get => _isRefreshingBootImages;
        private set { _isRefreshingBootImages = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set { _isComplete = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowResultDialog)); }
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set { _progressMessage = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Path to the generated ISO file, shown as its own compact, selectable/openable row in the
    /// result modal instead of being embedded in <see cref="StatusMessage"/>'s prose (Fluent
    /// dialog guidance: keep the content message simple; surface actionable details, like a file
    /// location, as their own row with a supporting action rather than a wall of text). Null for
    /// the USB target (there's no output file — the disk info fits in one short summary line).
    /// </summary>
    public string? ResultFilePath
    {
        get => _resultFilePath;
        private set { _resultFilePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasResultFilePath)); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HasResultFilePath => ResultFilePath is not null;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowResultDialog)); }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Heading shown alongside <see cref="ErrorMessage"/> — distinguishes a failure while merely
    /// listing boot images/disks ("Refresh failed") from a failure during the destructive
    /// download/partition/deploy workflow ("Preparation failed"), so a refresh error can never be
    /// mistaken for the USB device having been touched.
    /// </summary>
    public string ErrorTitle
    {
        get => _errorTitle;
        private set { _errorTitle = value; OnPropertyChanged(); }
    }

    public bool WasCancelled
    {
        get => _wasCancelled;
        private set { _wasCancelled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowResultDialog)); }
    }

    /// <summary>
    /// True once the Prepare workflow has reached a terminal outcome (success, error, or
    /// cancellation) that hasn't yet been dismissed. Drives a modal overlay (instead of inline
    /// InfoBars competing for space with the still-visible form) so the result is impossible to
    /// miss and the next action — <see cref="DismissResultCommand"/> — is unambiguous.
    /// </summary>
    public bool ShowResultDialog => IsComplete || HasError || WasCancelled;

    /// <summary>Reason the selected disk is not eligible, or null when it is valid.</summary>
    public string? ValidationMessage => SelectedDisk is { IsValid: false } d ? d.InvalidReason : null;

    public bool HasValidationMessage => ValidationMessage is not null;

    public bool CanPrepare =>
        !IsBusy
        && SelectedBootImage is not null
        && (PrepareAsIso
            ? !string.IsNullOrWhiteSpace(IsoOutputPath)
            : SelectedDisk is { IsValid: true } && ConfirmErase);

    public ICommand RefreshCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand BrowseIsoOutputCommand { get; }
    public ICommand OpenResultFolderCommand { get; }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshBootImagesAsync()
    {
        IsRefreshingBootImages = true;
        IsComplete             = false;
        ErrorMessage           = null;

        try
        {
            await LoadBootImagesAsync();
        }
        catch (Exception ex)
        {
            ErrorTitle   = "Refresh failed";
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRefreshingBootImages = false;
        }
    }

    private void LoadDisks()
    {
        Disks.Clear();
        foreach (var disk in _validator.EnumerateDisks())
        {
            // FR-054: only USB, removable disks ever appear in this list at all — internal,
            // fixed, and virtual (e.g. Hyper-V SCSI) disks are excluded entirely rather than
            // merely grayed out. Validate() still runs below for the disks that DO pass this
            // filter, to catch the rare case of a USB removable disk that is also the host's
            // system disk (e.g. booting from a USB stick).
            if (!disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) || !disk.IsRemovable)
                continue;

            var result = _validator.Validate(disk);
            var label  = string.Create(CultureInfo.InvariantCulture,
                $"Disk {disk.DiskNumber}: {disk.Caption} ({FormatBytes(disk.SizeBytes)}, {disk.BusType})");
            Disks.Add(new DiskChoice(disk.DiskNumber, label, disk, result.Valid, result.FailureReason));
        }

        // Pre-select the first eligible USB disk, if any.
        SelectedDisk = Disks.FirstOrDefault(d => d.IsValid);
        OnPropertyChanged(nameof(HasDisks));
        OnPropertyChanged(nameof(IsDiskSelectionEnabled));
    }

    private async Task LoadBootImagesAsync()
    {
        var token = await _authService.GetAccessTokenAsync();
        if (token is null)
        {
            StatusMessage = "Sign in to load published boot images from the portal.";
            return;
        }

        _operatorApi.SetAccessToken(token);
        var images = await _operatorApi.GetBootImagesAsync();

        BootImages.Clear();
        foreach (var image in images.Where(i => i.IsActive))
        {
            var latest = image.IsLatestPublished ? " (latest)" : string.Empty;
            var label  = string.Create(CultureInfo.InvariantCulture,
                $"v{image.Version} ({FormatBytes(image.SizeBytes)}){latest}");
            BootImages.Add(new BootImageChoice(image.BootImageId, label, image));
        }

        // FR-053: pre-select the latest published boot image.
        SelectedBootImage = BootImages.FirstOrDefault(b => b.Dto.IsLatestPublished)
                            ?? BootImages.FirstOrDefault();
        OnPropertyChanged(nameof(HasBootImages));
        OnPropertyChanged(nameof(IsBootImageSelectionEnabled));
        StatusMessage = BootImages.Count > 0
            ? "Confirm the destructive action, then prepare the USB device."
            : "No published boot images are available in the portal.";
    }

    // ── Prepare (destructive) ──────────────────────────────────────────────────

    private async Task PrepareAsync()
    {
        if (SelectedBootImage is null)
            return;

        if (PrepareAsIso)
        {
            await GenerateIsoAsync();
            return;
        }

        if (SelectedDisk is null)
            return;

        IsBusy       = true;
        IsComplete   = false;
        WasCancelled = false;
        ErrorMessage = null;
        ProgressPercent = 0;

        var wimPath = Path.Combine(Path.GetTempPath(), $"ci-boot-{Guid.NewGuid():N}.wim");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // Re-validate defensively right before the destructive operation (FR-054).
            _currentStage = "DVI";
            var validation = _validator.Validate(SelectedDisk.Info);
            if (!validation.Valid)
                throw new InvalidOperationException(validation.FailureReason ?? "Selected disk is not eligible.");

            var token = await _authService.GetAccessTokenAsync()
                ?? throw new InvalidOperationException("Sign in to the Operator API before preparing USB media.");
            _operatorApi.SetAccessToken(token);

            DiskSpaceGuard.EnsureFreeSpace(wimPath, SelectedBootImage.Dto.SizeBytes, "download the boot image");

            _currentStage = "BID";
            SetProgress("Checking local cache…", 8);
            var cachedWimPath = await _cache.TryGetCachedWimAsync(SelectedBootImage.Dto.Sha256Hash, ct);

            if (cachedWimPath is not null)
            {
                SetProgress("Using cached boot image…", 55);
                File.Copy(cachedWimPath, wimPath, overwrite: true);
            }
            else
            {
                SetProgress("Requesting download URL…", 10);
                var sas = await _operatorApi.GetBootImageSasAsync(SelectedBootImage.Id, ct);

                SetProgress("Downloading boot image…", 15);
                await _downloader.DownloadAsync(sas.SasTokenUrl, sas.Sha256Hash, wimPath, ct);

                SetProgress("Caching boot image for future use…", 55);
                await _cache.SaveAsync(wimPath, SelectedBootImage.Id, SelectedBootImage.Dto.Version, sas.Sha256Hash, ct);
            }

            _currentStage = "PRT";
            SetProgress("Partitioning USB device…", 60);

            // Partitioning (diskpart.exe) and boot-partition activation (bootsect.exe) both
            // require Administrator privileges. PrepareElevatedAsync transparently relaunches
            // this executable elevated (one UAC prompt) for just that work when the current
            // process isn't already Administrator — mirrors
            // BootImageGenerationService.GenerateElevatedAsync for DISM image mounting. The
            // boot image has already been downloaded to wimPath above (in this, non-elevated,
            // process) so no Operator API/network calls are needed from the elevated worker.
            // Partitioning, deployment, and the manifest write all happen as one atomic
            // elevated step (see UsbPreparationService.PrepareAsync), so any failure here is
            // attributed to the "PRT" stage rather than distinguishing partition vs. deploy.
            var toolVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            var preparationParams = new UsbPreparationService.PreparationParams(
                SelectedDisk.DiskNumber,
                SelectedDisk.Info.SizeBytes,
                wimPath,
                SelectedBootImage.Dto.Version,
                SelectedDisk.DiskNumber.ToString(CultureInfo.InvariantCulture),
                SelectedDisk.Info.BusType,
                validation.Valid,
                toolVersion,
                SelectedDisk.Info.Caption);
            await _preparation.PrepareElevatedAsync(preparationParams, ct);

            SetProgress("USB device prepared successfully.", 100);
            StatusMessage = string.Create(CultureInfo.InvariantCulture,
                $"Boot image v{SelectedBootImage.Dto.Version} was written to disk {SelectedDisk.DiskNumber} ({SelectedDisk.Info.Caption}, {FormatBytes(SelectedDisk.Info.SizeBytes)}). Boot the target machine from it to start imaging.");
            IsComplete    = true;
        }
        catch (OperationCanceledException)
        {
            WasCancelled  = true;
            StatusMessage = "Preparation cancelled.";
            ProgressMessage = "Preparation cancelled. Cleaning up temporary files…";
        }
        catch (Exception ex)
        {
            ErrorTitle   = "Preparation failed";
            // T160/FR-058: every failure path surfaces a support reference code so a technician
            // can quote it to support without needing log access. BootImageDownloadService
            // already embeds its own BID code on retry exhaustion — avoid double-wrapping that.
            ErrorMessage = ex.Message.Contains("CMB-", StringComparison.Ordinal)
                ? ex.Message
                : string.Create(CultureInfo.InvariantCulture,
                    $"{ex.Message} (Error reference: {SupportReferenceCode.ForMediaBuilder("PREPUSB", _currentStage)})");
        }
        finally
        {
            try { if (File.Exists(wimPath)) File.Delete(wimPath); } catch { /* best-effort cleanup */ }
            _cts.Dispose();
            _cts = null;
            IsBusy = false;

            // Whatever the outcome (success, failure, or cancellation), the destructive
            // confirmation must be re-armed explicitly for the next device rather than staying
            // checked — otherwise a technician could click Prepare again on a newly-inserted
            // disk without re-confirming the erase.
            ConfirmErase = false;
        }
    }

    // ── Generate ISO (non-destructive) ─────────────────────────────────────────

    /// <summary>
    /// Downloads (or reuses a cached copy of) the selected boot image and packages it into a
    /// bootable ISO file at <see cref="IsoOutputPath"/> — an alternative to
    /// <see cref="PrepareAsync"/>'s USB partitioning path for booting a Hyper-V VM (or burning
    /// physical media) without needing a physical USB device at all. Never touches a disk, so
    /// there is no destructive-confirmation gate for this path.
    /// </summary>
    private async Task GenerateIsoAsync()
    {
        if (SelectedBootImage is null || string.IsNullOrWhiteSpace(IsoOutputPath) || _isoGeneration is null)
            return;

        IsBusy       = true;
        IsComplete   = false;
        WasCancelled = false;
        ErrorMessage = null;
        ProgressPercent = 0;

        var wimPath = Path.Combine(Path.GetTempPath(), $"ci-boot-{Guid.NewGuid():N}.wim");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _currentStage = "BID";
            var token = await _authService.GetAccessTokenAsync()
                ?? throw new InvalidOperationException("Sign in to the Operator API before generating an ISO.");
            _operatorApi.SetAccessToken(token);

            DiskSpaceGuard.EnsureFreeSpace(wimPath, SelectedBootImage.Dto.SizeBytes, "download the boot image");

            SetProgress("Checking local cache…", 8);
            var cachedWimPath = await _cache.TryGetCachedWimAsync(SelectedBootImage.Dto.Sha256Hash, ct);

            if (cachedWimPath is not null)
            {
                SetProgress("Using cached boot image…", 25);
                File.Copy(cachedWimPath, wimPath, overwrite: true);
            }
            else
            {
                SetProgress("Requesting download URL…", 10);
                var sas = await _operatorApi.GetBootImageSasAsync(SelectedBootImage.Id, ct);

                SetProgress("Downloading boot image…", 15);
                await _downloader.DownloadAsync(sas.SasTokenUrl, sas.Sha256Hash, wimPath, ct);

                SetProgress("Caching boot image for future use…", 25);
                await _cache.SaveAsync(wimPath, SelectedBootImage.Id, SelectedBootImage.Dto.Version, sas.Sha256Hash, ct);
            }

            _currentStage = "ISO";
            ct.ThrowIfCancellationRequested();
            SetProgress("Building bootable ISO…", 30);

            // ISO packaging (copype.cmd/MakeWinPEMedia.cmd) occupies the 30-100% band, remapped
            // from IsoGenerationService's own internal 0-100% progress — the same remapping
            // pattern UsbPreparationService's partition/deploy steps already use.
            void OnIsoProgress(string message, int percent) =>
                SetProgress(message, 30 + (int)(0.70 * percent));
            await _isoGeneration.GenerateElevatedAsync(wimPath, IsoOutputPath, OnIsoProgress, ct);

            var isoSizeBytes = new FileInfo(IsoOutputPath).Length;
            StatusMessage = string.Create(CultureInfo.InvariantCulture,
                $"Boot image v{SelectedBootImage.Dto.Version} ({FormatBytes(isoSizeBytes)}).");
            ResultFilePath = IsoOutputPath;
            IsComplete    = true;
        }
        catch (OperationCanceledException)
        {
            WasCancelled  = true;
            StatusMessage = "ISO generation cancelled.";
            ProgressMessage = "ISO generation cancelled. Cleaning up temporary files…";
        }
        catch (Exception ex)
        {
            ErrorTitle   = "ISO generation failed";
            ErrorMessage = ex.Message.Contains("CMB-", StringComparison.Ordinal)
                ? ex.Message
                : string.Create(CultureInfo.InvariantCulture,
                    $"{ex.Message} (Error reference: {SupportReferenceCode.ForMediaBuilder("GENISO", _currentStage)})");
        }
        finally
        {
            try { if (File.Exists(wimPath)) File.Delete(wimPath); } catch { /* best-effort cleanup */ }
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>Prompts for the ISO output file path via the standard Windows Save dialog.</summary>
    private void BrowseIsoOutput()
    {
        var defaultName = GetDefaultIsoFileName();

        // Defaults to the same folder as the currently-configured output path (initially the
        // "Cloud Imaging Media Builder\ISO Files" folder under Documents — see
        // GetDefaultIsoOutputFolder), so re-opening Browse after a prior choice starts from
        // wherever the technician last saved an ISO rather than jumping back to Documents.
        var initialDirectory = !string.IsNullOrWhiteSpace(IsoOutputPath)
            ? Path.GetDirectoryName(IsoOutputPath)
            : null;
        if (string.IsNullOrWhiteSpace(initialDirectory))
            initialDirectory = GetDefaultIsoOutputFolder();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save ISO file as",
            Filter           = "ISO image (*.iso)|*.iso",
            DefaultExt       = ".iso",
            FileName         = defaultName,
            InitialDirectory = initialDirectory,
            AddExtension     = true,
        };
        if (dialog.ShowDialog() == true)
        {
            IsoOutputPath = dialog.FileName;
            _isoOutputPathCustomized = true;
        }
    }

    /// <summary>
    /// The default/suggested ISO filename for the currently selected boot image — version-specific
    /// ("cloud-imaging-boot-v{version}.iso") once a boot image is selected, otherwise the generic
    /// "cloud-imaging-boot.iso". Shared by the auto-updated <see cref="IsoOutputPath"/> default and
    /// the Browse dialog's suggested filename so both stay consistent.
    /// </summary>
    private string GetDefaultIsoFileName() =>
        SelectedBootImage is not null
            ? $"cloud-imaging-boot-v{SelectedBootImage.Dto.Version}.iso"
            : "cloud-imaging-boot.iso";

    /// <summary>
    /// Default ISO output location: a "Cloud Imaging Media Builder\ISO Files" subfolder under
    /// the current user's Documents folder — the same base folder (and fallback chain) as
    /// <c>GenerateBootImageViewModel.GetDefaultOutputFolder</c> uses for generated boot images,
    /// just under a distinct "ISO Files" subfolder rather than "Boot Images". Writable without
    /// elevation.
    /// </summary>
    private static string GetDefaultIsoOutputFolder()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(basePath))
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(basePath))
            basePath = Path.GetTempPath();

        return Path.Combine(basePath, "Cloud Imaging Media Builder", "ISO Files");
    }

    private void Cancel()
    {
        if (_cts is null)
            return;

        ProgressMessage = "Cancelling. Waiting for cleanup to finish…";
        _cts.Cancel();
    }

    /// <summary>
    /// Dismisses the success/error/cancelled result modal and resets the workflow so the next
    /// USB device can be prepared immediately, without leaving the app or re-navigating.
    /// Re-scans the disk list (the just-prepared device may have been removed and a new one
    /// inserted while the modal was open) and requires <see cref="ConfirmErase"/> to be
    /// re-checked for the next device as a safety measure — the boot image selection is left
    /// as-is since it's almost always the same for consecutive devices.
    /// </summary>
    private void CloseResultDialog()
    {
        IsComplete      = false;
        ErrorMessage    = null;
        WasCancelled    = false;
        ConfirmErase    = false;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ResultFilePath  = null;
        StatusMessage   = PrepareAsIso
            ? "Select an output path to generate another ISO."
            : "Select a USB device to begin.";

        try { LoadDisks(); } catch { /* best-effort; manual Refresh remains available */ }
    }

    /// <summary>
    /// Opens File Explorer with the generated ISO pre-selected (FR-057-adjacent UX: the Fluent
    /// dialog guidance favors an actionable button over asking the technician to manually copy
    /// a path and navigate there themselves). Best-effort — the path remains visible/selectable
    /// in the modal regardless, so a failure here is never fatal to the workflow.
    /// </summary>
    private void OpenResultFolder()
    {
        if (ResultFilePath is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ResultFilePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    // ── Progress plumbing ──────────────────────────────────────────────────────

    private void OnDownloadProgress(object? sender, (long Downloaded, long Total) e)
    {
        if (e.Total <= 0)
            return;

        // Download occupies the 15–55% band of the overall workflow (skipped entirely on a cache hit).
        ProgressPercent = 15 + (int)(40.0 * e.Downloaded / e.Total);
        ProgressMessage = string.Create(CultureInfo.InvariantCulture,
            $"Downloading boot image… {FormatBytes(e.Downloaded)} / {FormatBytes(e.Total)}");
    }

    private void OnPreparationProgress(object? sender, (string Message, int Percent) e)
    {
        // UsbPreparationService already reports the overall 60–100% band for the
        // partition/deploy/manifest workflow (including the "Requesting Administrator
        // privileges" step at 58% when an elevation relaunch is required) — no remapping needed.
        ProgressPercent = e.Percent;
        ProgressMessage = e.Message;
    }

    /// <summary>
    /// Handles a debounced USB plug/unplug notification from <see cref="UsbDeviceChangeWatcher"/>
    /// (T071, FR-054). Fires on a background thread, so refresh work is marshaled onto the UI
    /// thread. Never runs while a destructive Prepare operation is in progress — re-enumerating
    /// disks mid-operation could otherwise change <see cref="SelectedDisk"/> out from under it.
    /// The manual Refresh button remains available as a fallback regardless of this.
    /// </summary>
    private void OnDeviceChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (IsBusy)
            return;

        try { LoadDisks(); } catch { /* best-effort; manual Refresh remains available */ }
    });

    /// <summary>Marshals an action onto the UI thread (WMI events arrive on a worker thread).</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private void SetProgress(string message, int percent)
    {
        ProgressMessage = message;
        ProgressPercent = percent;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{size:0.0} {units[unit]}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Disposes the in-flight cancellation token source, if any (owned by this view model).</summary>
    public void Dispose()
    {
        _cts?.Dispose();
        if (_deviceWatcher is not null)
            _deviceWatcher.DeviceChanged -= OnDeviceChanged;
    }

    /// <summary>A selectable disk plus its pre-computed eligibility (FR-054).</summary>
    public sealed record DiskChoice(
        uint DiskNumber,
        string Label,
        UsbSafetyValidationService.DiskInfo Info,
        bool IsValid,
        string? InvalidReason);

    /// <summary>A selectable published boot image.</summary>
    public sealed record BootImageChoice(Guid Id, string Label, BootImageDto Dto);
}

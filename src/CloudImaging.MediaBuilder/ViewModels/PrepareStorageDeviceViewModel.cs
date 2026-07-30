using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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
public sealed class PrepareStorageDeviceViewModel : INotifyPropertyChanged
{
    private readonly OperatorApiClient _operatorApi;
    private readonly EntraAuthenticationService _authService;
    private readonly UsbSafetyValidationService _validator;
    private readonly BootImageDownloadService _downloader;
    private readonly UsbPartitionProvisioningService _provisioner;
    private readonly BootImageDeploymentService _deployer;
    private readonly Action _navigateBack;

    private BootImageChoice? _selectedBootImage;
    private DiskChoice? _selectedDisk;
    private bool _confirmErase;
    private bool _isBusy;
    private bool _isComplete;
    private int _progressPercent;
    private string _progressMessage = string.Empty;
    private string _statusMessage = "Select a boot image and a USB device to begin.";
    private string? _errorMessage;

    public PrepareStorageDeviceViewModel(
        OperatorApiClient operatorApi,
        EntraAuthenticationService authService,
        UsbSafetyValidationService validator,
        BootImageDownloadService downloader,
        UsbPartitionProvisioningService provisioner,
        BootImageDeploymentService deployer,
        Action navigateBack)
    {
        _operatorApi  = operatorApi;
        _authService  = authService;
        _validator    = validator;
        _downloader   = downloader;
        _provisioner  = provisioner;
        _deployer     = deployer;
        _navigateBack = navigateBack;

        _downloader.ProgressChanged += OnDownloadProgress;
        _deployer.ProgressChanged   += OnDeployProgress;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        PrepareCommand = new RelayCommand(async _ => await PrepareAsync(), _ => CanPrepare);
        BackCommand    = new RelayCommand(_ => _navigateBack());
    }

    public ObservableCollection<BootImageChoice> BootImages { get; } = [];
    public ObservableCollection<DiskChoice> Disks { get; } = [];

    public BootImageChoice? SelectedBootImage
    {
        get => _selectedBootImage;
        set { _selectedBootImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPrepare)); }
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

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPrepare)); }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set { _isComplete = value; OnPropertyChanged(); }
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

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Reason the selected disk is not eligible, or null when it is valid.</summary>
    public string? ValidationMessage => SelectedDisk is { IsValid: false } d ? d.InvalidReason : null;

    public bool HasValidationMessage => ValidationMessage is not null;

    public bool CanPrepare =>
        !IsBusy
        && SelectedBootImage is not null
        && SelectedDisk is { IsValid: true }
        && ConfirmErase;

    public ICommand RefreshCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand BackCommand { get; }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        IsBusy       = true;
        IsComplete   = false;
        ErrorMessage = null;

        try
        {
            LoadDisks();
            await LoadBootImagesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadDisks()
    {
        Disks.Clear();
        foreach (var disk in _validator.EnumerateDisks())
        {
            var result = _validator.Validate(disk);
            var label  = string.Create(CultureInfo.InvariantCulture,
                $"Disk {disk.DiskNumber}: {disk.Caption} ({FormatBytes(disk.SizeBytes)}, {disk.BusType})");
            Disks.Add(new DiskChoice(disk.DiskNumber, label, disk, result.Valid, result.FailureReason));
        }

        // Pre-select the first eligible USB disk, if any.
        SelectedDisk = Disks.FirstOrDefault(d => d.IsValid);
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
            var latest = image.IsLatestPublished ? " — latest" : string.Empty;
            var label  = string.Create(CultureInfo.InvariantCulture,
                $"v{image.Version} ({FormatBytes(image.SizeBytes)}){latest}");
            BootImages.Add(new BootImageChoice(image.BootImageId, label, image));
        }

        // FR-053: pre-select the latest published boot image.
        SelectedBootImage = BootImages.FirstOrDefault(b => b.Dto.IsLatestPublished)
                            ?? BootImages.FirstOrDefault();
        StatusMessage = BootImages.Count > 0
            ? "Confirm the destructive action, then prepare the USB device."
            : "No published boot images are available in the portal.";
    }

    // ── Prepare (destructive) ──────────────────────────────────────────────────

    private async Task PrepareAsync()
    {
        if (SelectedBootImage is null || SelectedDisk is null)
            return;

        IsBusy       = true;
        IsComplete   = false;
        ErrorMessage = null;
        ProgressPercent = 0;

        var wimPath = Path.Combine(Path.GetTempPath(), $"ci-boot-{Guid.NewGuid():N}.wim");

        try
        {
            // Re-validate defensively right before the destructive operation (FR-054).
            var validation = _validator.Validate(SelectedDisk.Info);
            if (!validation.Valid)
                throw new InvalidOperationException(validation.FailureReason ?? "Selected disk is not eligible.");

            var token = await _authService.GetAccessTokenAsync()
                ?? throw new InvalidOperationException("Sign in to the Operator API before preparing USB media.");
            _operatorApi.SetAccessToken(token);

            SetProgress("Requesting download URL…", 5);
            var sas = await _operatorApi.GetBootImageSasAsync(SelectedBootImage.Id);

            SetProgress("Downloading boot image…", 10);
            await _downloader.DownloadAsync(sas.SasTokenUrl, sas.Sha256Hash, wimPath);

            SetProgress("Partitioning USB device…", 55);
            await _provisioner.ProvisionAsync(SelectedDisk.DiskNumber, msg => ProgressMessage = msg);

            var bootDrive = _provisioner.FindBootVolumeDriveLetter()
                ?? throw new InvalidOperationException(
                    "Could not locate the BOOT partition after provisioning the USB device.");

            SetProgress("Deploying boot image to USB…", 70);
            await _deployer.DeployAsync(wimPath, bootDrive);

            SetProgress("USB device prepared successfully.", 100);
            StatusMessage = "The USB device is ready. Boot the target machine from it to start imaging.";
            IsComplete    = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            try { if (File.Exists(wimPath)) File.Delete(wimPath); } catch { /* best-effort cleanup */ }
            IsBusy = false;
        }
    }

    // ── Progress plumbing ──────────────────────────────────────────────────────

    private void OnDownloadProgress(object? sender, (long Downloaded, long Total) e)
    {
        if (e.Total <= 0)
            return;

        // Download occupies the 10–55% band of the overall workflow.
        ProgressPercent = 10 + (int)(45.0 * e.Downloaded / e.Total);
        ProgressMessage = string.Create(CultureInfo.InvariantCulture,
            $"Downloading boot image… {FormatBytes(e.Downloaded)} / {FormatBytes(e.Total)}");
    }

    private void OnDeployProgress(object? sender, (string Message, int Percent) e)
    {
        // Deployment occupies the 70–100% band of the overall workflow.
        ProgressPercent = 70 + (int)(0.3 * e.Percent);
        ProgressMessage = e.Message;
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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.Views;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for <see cref="ShellView"/> — the persistent NavigationView-style app shell
/// shown once the user is signed in. Owns the current section and swaps the hosted
/// content (Home / Generate Boot Image / Prepare USB Device) without leaving the shell,
/// so the left navigation pane stays visible across every section.
///
/// Generate Boot Image is disabled up front (nav item + Home tile) rather than only warned
/// about after selection, when the Windows ADK is not installed (FR-050a) or the signed-in
/// user lacks the Administrator role (FR-050b).
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly EntraAuthenticationService _authService;
    private readonly OperatorApiClient _operatorApiClient;
    private readonly BootImageGenerationService _genService;
    private readonly GitHubReleasesClient _gitHubReleasesClient;
    private readonly UsbSafetyValidationService _usbValidator;
    private readonly BootImageDownloadService _downloader;
    private readonly UsbPartitionProvisioningService _provisioner;
    private readonly BootImageDeploymentService _deployer;
    private readonly BootImageCacheService _cache;
    private readonly UsbDeviceChangeWatcher? _deviceWatcher;
    private readonly BootMediaCertificateCheckService? _certCheckService;

    private object? _currentContent;
    private ShellSection _currentSection;
    private bool _canNavigate = true;
    private INotifyPropertyChanged? _trackedContentViewModel;
    private Func<bool>? _isTrackedContentBusy;
    /// <summary>
    /// Whether an active boot media certificate is configured (T151, FR-050a). Starts true
    /// (optimistic) so the nav item isn't spuriously disabled before the async check below
    /// completes; refined to the real value shortly after construction.
    /// </summary>
    private bool _certificateConfigured = true;

    public ShellViewModel(
        EntraAuthenticationService authService,
        OperatorApiClient operatorApiClient,
        BootImageGenerationService genService,
        GitHubReleasesClient gitHubReleasesClient,
        UsbSafetyValidationService usbValidator,
        BootImageDownloadService downloader,
        UsbPartitionProvisioningService provisioner,
        BootImageDeploymentService deployer,
        BootImageCacheService cache,
        UsbDeviceChangeWatcher? deviceWatcher = null,
        BootMediaCertificateCheckService? certCheckService = null,
        Func<bool>? isAdkInstalled = null)
    {
        _authService       = authService;
        _operatorApiClient = operatorApiClient;
        _genService        = genService;
        _gitHubReleasesClient = gitHubReleasesClient;
        _usbValidator      = usbValidator;
        _downloader        = downloader;
        _provisioner       = provisioner;
        _deployer          = deployer;
        _cache             = cache;
        _deviceWatcher     = deviceWatcher;
        _certCheckService  = certCheckService;

        AdkAvailable    = (isAdkInstalled ?? BootImageGenerationService.IsAdkInstalled)();
        IsAdministrator = _authService.IsAdministrator;

        GoHomeCommand       = new RelayCommand(_ => GoHome(), _ => CanNavigate);
        GoGenerateCommand   = new RelayCommand(_ => GoGenerate(), _ => IsGenerateBootImageAvailable && CanNavigate);
        GoPrepareUsbCommand = new RelayCommand(_ => GoPrepareUsb(), _ => CanNavigate);

        GoHome();

        // T151/FR-050a: only Administrators can ever reach Generate Boot Image, so the check
        // is skipped entirely for other roles. Runs after GoHome() so navigation is never
        // blocked waiting on a network round-trip; the Home tile/nav item simply refreshes
        // live (fail-closed until confirmed) once the check resolves.
        if (IsAdministrator && _certCheckService is not null)
            _ = RefreshCertificateStatusAsync();
    }

    /// <summary>
    /// True when the Windows ADK is installed. Gates the "Generate Boot Image" nav item —
    /// bound as its <c>IsEnabled</c> and its command's CanExecute — so that section is
    /// unreachable from the shell (not just warned about) while the ADK is missing.
    /// </summary>
    public bool AdkAvailable { get; }

    /// <summary>
    /// True when the signed-in user holds the Administrator app role. Gates the "Generate Boot
    /// Image" nav item (FR-050b) the same way <see cref="AdkAvailable"/> does for the ADK
    /// prerequisite (FR-050a) — Technician (or no-role) users only get Prepare USB Device.
    /// Computed once at shell construction time, which always happens after sign-in completes.
    /// </summary>
    public bool IsAdministrator { get; }

    /// <summary>True when the "Generate Boot Image" nav item should be reachable at all — requires the ADK, the Administrator role, AND a configured boot media certificate (T151).</summary>
    public bool IsGenerateBootImageAvailable => AdkAvailable && IsAdministrator && _certificateConfigured;

    /// <summary>Tooltip/reason shown when the Generate Boot Image nav item is disabled; role restriction takes precedence.</summary>
    public string? GenerateBootImageUnavailableReason =>
        !IsAdministrator ? "Generate Boot Image requires the Administrator role."
        : !AdkAvailable ? "Install the Windows ADK to unlock this section."
        : !_certificateConfigured ? "Generate a boot media certificate in Portal Configuration before generating boot images."
        : null;

    /// <summary>The view currently hosted in the shell's content area.</summary>
    public object? CurrentContent
    {
        get => _currentContent;
        private set { _currentContent = value; OnPropertyChanged(); }
    }

    public ShellSection CurrentSection
    {
        get => _currentSection;
        private set
        {
            _currentSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHomeActive));
            OnPropertyChanged(nameof(IsGenerateActive));
            OnPropertyChanged(nameof(IsPrepareUsbActive));
            OnPropertyChanged(nameof(SectionLabel));
        }
    }

    public bool IsHomeActive       => CurrentSection == ShellSection.Home;
    public bool IsGenerateActive   => CurrentSection == ShellSection.Generate;
    public bool IsPrepareUsbActive => CurrentSection == ShellSection.PrepareUsb;

    /// <summary>
    /// True while the nav pane (Home / Generate Boot Image / Prepare USB Device) should be
    /// selectable. False while the currently hosted section has a long-running operation in
    /// flight (WinPE generation, or a USB write) — there's no way to resume an abandoned
    /// operation after navigating away and back, so the nav pane is locked to prevent that
    /// data loss outright rather than warning about it afterwards.
    /// </summary>
    public bool CanNavigate
    {
        get => _canNavigate;
        private set
        {
            if (_canNavigate == value) return;
            _canNavigate = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Current section's display name, used as the bold trailing crumb in the shell's breadcrumb bar.</summary>
    public string SectionLabel => CurrentSection switch
    {
        ShellSection.Generate   => "Generate Boot Image",
        ShellSection.PrepareUsb => "Prepare USB Device",
        _                       => "Home",
    };

    /// <summary>Signed-in user's display name, shown in the shell's footer. Falls back to the UPN, then a generic label.</summary>
    public string SignedInUserDisplay => _authService.SignedInDisplayName ?? "Signed in";

    /// <summary>Up to two initials derived from the display name (or UPN's local part as a fallback), for the footer avatar badge.</summary>
    public string SignedInInitials
    {
        get
        {
            var name = _authService.SignedInDisplayName;
            if (string.IsNullOrWhiteSpace(name)) return "?";

            // A display name splits on spaces ("Jamie Doe"); a UPN fallback splits on the
            // separators typically used in its local part ("jamie.doe@contoso.com").
            var parts = name.Contains('@')
                ? name.Split('@')[0].Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
                : name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var initials = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
            return initials.Length > 0 ? initials : "?";
        }
    }

    public ICommand GoHomeCommand       { get; }
    public ICommand GoGenerateCommand   { get; }
    public ICommand GoPrepareUsbCommand { get; }

    /// <summary>Navigates to Home. Public (not just via the command) so dev-mode tooling can jump here directly.</summary>
    public void GoHome()
    {
        var operationSelectionViewModel = new OperationSelectionViewModel(
            op =>
            {
                if (op == "GenerateBootImage") GoGenerate();
                else if (op == "PrepareUSB") GoPrepareUsb();
            },
            isAdkInstalled: () => AdkAvailable,
            isAdministrator: IsAdministrator,
            isCertificateConfigured: _certificateConfigured);
        var view = new OperationSelectionView { DataContext = operationSelectionViewModel };
        CurrentContent = view;
        CurrentSection = ShellSection.Home;
        StopTrackingBusyState();
    }

    /// <summary>
    /// Navigates to Generate Boot Image unconditionally. Public so dev-mode tooling can preview
    /// the section even without the ADK installed; the nav pane itself is what's gated (via
    /// <see cref="GoGenerateCommand"/>'s CanExecute), not this method.
    /// </summary>
    public void GoGenerate()
    {
        var viewModel = new GenerateBootImageViewModel(_genService, _authService, _gitHubReleasesClient, GoHome);
        CurrentContent = new GenerateBootImageView { DataContext = viewModel };
        CurrentSection = ShellSection.Generate;
        TrackBusyState(viewModel, () => viewModel.IsGenerating);
    }

    /// <summary>Navigates to Prepare USB Device. Public so dev-mode tooling can jump here directly.</summary>
    public void GoPrepareUsb()
    {
        var viewModel = new PrepareStorageDeviceViewModel(
            _operatorApiClient, _authService, _usbValidator, _downloader, _provisioner, _deployer, _cache, GoHome, _deviceWatcher);
        CurrentContent = new PrepareStorageDeviceView { DataContext = viewModel };
        CurrentSection = ShellSection.PrepareUsb;
        TrackBusyState(viewModel, () => viewModel.IsBusy);
    }

    /// <summary>
    /// Checks whether an active boot media certificate is configured (T151, FR-050a) and
    /// refreshes both the shell-level nav-item gate and, if Home is still the currently
    /// displayed section, the live <see cref="OperationSelectionViewModel"/> so the tile
    /// updates without requiring a re-navigation.
    /// </summary>
    private async Task RefreshCertificateStatusAsync()
    {
        var configured = false;
        try
        {
            var token = await _authService.GetAccessTokenAsync();
            if (token is not null)
            {
                _operatorApiClient.SetAccessToken(token);
                configured = await _certCheckService!.IsCertificateConfiguredAsync();
            }
        }
        catch
        {
            configured = false;
        }

        if (_certificateConfigured == configured)
            return;

        _certificateConfigured = configured;
        OnPropertyChanged(nameof(IsGenerateBootImageAvailable));
        OnPropertyChanged(nameof(GenerateBootImageUnavailableReason));
        CommandManager.InvalidateRequerySuggested();

        if (CurrentContent is OperationSelectionView { DataContext: OperationSelectionViewModel vm })
            vm.SetCertificateConfigured(configured);
    }

    /// <summary>
    /// Starts watching <paramref name="viewModel"/> for changes to its busy state (via
    /// <paramref name="isBusy"/>, re-evaluated on every <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// from it) so <see cref="CanNavigate"/> tracks whether the just-navigated-to section is
    /// mid-operation. Replaces any previously tracked view model.
    /// </summary>
    private void TrackBusyState(INotifyPropertyChanged viewModel, Func<bool> isBusy)
    {
        StopTrackingBusyState();
        _trackedContentViewModel = viewModel;
        _isTrackedContentBusy = isBusy;
        viewModel.PropertyChanged += OnTrackedContentPropertyChanged;
        CanNavigate = !isBusy();
    }

    /// <summary>Unsubscribes from the currently tracked content view model, if any, and unlocks the nav pane.</summary>
    private void StopTrackingBusyState()
    {
        if (_trackedContentViewModel is not null)
            _trackedContentViewModel.PropertyChanged -= OnTrackedContentPropertyChanged;
        _trackedContentViewModel = null;
        _isTrackedContentBusy = null;
        CanNavigate = true;
    }

    private void OnTrackedContentPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        CanNavigate = !(_isTrackedContentBusy?.Invoke() ?? false);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The section currently hosted in the shell's content area.</summary>
public enum ShellSection
{
    Home,
    Generate,
    PrepareUsb,
}

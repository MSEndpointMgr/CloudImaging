using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.MediaBuilder.Services;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the MediaBuilder OperationSelectionView (T151, FR-051).
/// </summary>
public sealed class OperationSelectionViewModel : INotifyPropertyChanged
{
    private readonly Action<string> _navigate;
    private readonly bool _adkAvailable;
    private readonly bool _isAdministrator;
    private readonly bool _hasMediaBuilderRole;
    private string? _selectedOperation;
    private bool _showAdkWarning;
    private bool _showCertificateWarning;
    private BootMediaCertificateStatus _certificateStatus;

    public OperationSelectionViewModel(
        Action<string> navigate,
        Func<bool>? isAdkInstalled = null,
        bool isAdministrator = true,
        BootMediaCertificateStatus certificateStatus = BootMediaCertificateStatus.Configured,
        bool hasMediaBuilderRole = true)
    {
        _navigate = navigate;
        _adkAvailable = (isAdkInstalled ?? BootImageGenerationService.IsAdkInstalled)();
        _isAdministrator = isAdministrator;
        // An Administrator implicitly holds a role, so callers passing only isAdministrator keep
        // their existing behaviour.
        _hasMediaBuilderRole = hasMediaBuilderRole || isAdministrator;
        _certificateStatus = certificateStatus;
        SelectOperationCommand = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand = new RelayCommand(_ => _navigate(_selectedOperation!), _ => CanContinue);
        NavigateCommand = new RelayCommand(op => SelectAndNavigate(op?.ToString()));

        // Preselect the first operation so Continue is immediately actionable.
        _selectedOperation = "GenerateBootImage";
        UpdateWarnings();
    }

    public string? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            _selectedOperation = value;
            OnPropertyChanged();
            UpdateWarnings();
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    /// <summary>
    /// True when the ADK prerequisite blocks the currently selected operation. Surfaced as a
    /// warning on this screen so the user is informed before navigating (FR-050a).
    /// </summary>
    public bool ShowAdkWarning
    {
        get => _showAdkWarning;
        private set { _showAdkWarning = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// True when the missing boot media certificate blocks the currently selected operation
    /// (T151, FR-050a).
    /// </summary>
    public bool ShowCertificateWarning
    {
        get => _showCertificateWarning;
        private set { _showCertificateWarning = value; OnPropertyChanged(); }
    }

    // Generate Boot Image requires the ADK (FR-050a), the Administrator role (FR-050b), AND
    // a configured boot media certificate (T151, FR-050a); Prepare USB only requires a role.
    public bool CanContinue =>
        _selectedOperation is not null
        && !(_selectedOperation == "GenerateBootImage" && !IsGenerateBootImageAvailable)
        && !(_selectedOperation == "PrepareUSB" && !IsPrepareUsbAvailable);

    /// <summary>
    /// True when the Generate Boot Image tile/nav item should be reachable at all. Bound
    /// directly (independent of <see cref="SelectedOperation"/>) so the Home tile can be
    /// disabled up front rather than only warning about it after the user picks it.
    /// </summary>
    public bool IsGenerateBootImageAvailable =>
        _adkAvailable && _isAdministrator && _certificateStatus == BootMediaCertificateStatus.Configured;

    /// <summary>
    /// True when the Prepare USB Device tile/nav item should be reachable. Open to both roles,
    /// but closed to a signed-in user holding no role at all.
    /// </summary>
    public bool IsPrepareUsbAvailable => _hasMediaBuilderRole;

    /// <summary>
    /// True when the signed-in user holds no Media Builder app role, so neither workflow is
    /// available. Distinct from <see cref="IsRestrictedByRole"/>, which is the Technician case.
    /// </summary>
    public bool HasNoRole => !_hasMediaBuilderRole;

    /// <summary>
    /// True once signed in as a Technician, who can prepare USB media but not generate boot
    /// images (FR-050b). Independent of ADK availability so the role reason is never masked by
    /// the ADK-missing reason, and the two are always mutually exclusive with
    /// <see cref="IsBlockedByMissingAdk"/>.
    /// </summary>
    public bool IsRestrictedByRole => _hasMediaBuilderRole && !_isAdministrator;

    /// <summary>
    /// True when the Administrator role check passes but the ADK is the only remaining
    /// blocker (FR-050a). Mutually exclusive with <see cref="IsRestrictedByRole"/>.
    /// </summary>
    public bool IsBlockedByMissingAdk => _isAdministrator && !_adkAvailable;

    /// <summary>
    /// True when the Administrator role and ADK checks both pass but no active boot media
    /// certificate is configured in the portal (T151, FR-050a) — generating a boot image
    /// without one would produce media that can never authenticate to the Device Gateway API.
    /// </summary>
    public bool IsBlockedByMissingCertificate => IsBlockedByCertificateStatus(BootMediaCertificateStatus.NotConfigured);

    /// <summary>
    /// True when the certificate check itself was refused by the Operator API. Distinct from
    /// <see cref="IsBlockedByMissingCertificate"/>: creating a certificate would not help.
    /// </summary>
    public bool IsBlockedByApiAccess => IsBlockedByCertificateStatus(BootMediaCertificateStatus.AccessDenied);

    /// <summary>True when the certificate check could not be completed (network or service error).</summary>
    public bool IsBlockedByFailedCertificateCheck => IsBlockedByCertificateStatus(BootMediaCertificateStatus.CheckFailed);

    private bool IsBlockedByCertificateStatus(BootMediaCertificateStatus status) =>
        _isAdministrator && _adkAvailable && _certificateStatus == status;

    /// <summary>Tooltip shown when the Generate Boot Image tile is disabled; role restriction takes precedence.</summary>
    public string? GenerateBootImageUnavailableReason =>
        HasNoRole ? "Requires the Technician or Administrator role."
        : IsRestrictedByRole ? "Requires the Administrator role."
        : IsBlockedByMissingAdk ? "Install the Windows ADK to unlock this section."
        : IsBlockedByApiAccess ? OperatorApiErrorDescription.AccessDenied
        : IsBlockedByFailedCertificateCheck ? "The boot media certificate could not be verified. Check your connection to the Cloud Imaging Operator API and relaunch."
        : IsBlockedByMissingCertificate ? "Generate a boot media certificate in Portal Configuration before generating boot images."
        : null;

    /// <summary>Tooltip shown when the Prepare USB Device tile is disabled.</summary>
    public string? PrepareUsbUnavailableReason =>
        HasNoRole ? "Requires the Technician or Administrator role." : null;

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    /// <summary>Single-tap navigation used by the Home tiles: select, then continue immediately if allowed.</summary>
    public ICommand NavigateCommand { get; }

    /// <summary>
    /// Updates the boot media certificate check result (T151), after the asynchronous check
    /// against the Operator API completes. Re-raises every property derived from this state so
    /// the Home tiles refresh live if the check resolves after the screen is already showing.
    /// </summary>
    public void SetCertificateStatus(BootMediaCertificateStatus status)
    {
        if (_certificateStatus == status)
            return;

        _certificateStatus = status;
        OnPropertyChanged(nameof(IsGenerateBootImageAvailable));
        OnPropertyChanged(nameof(IsBlockedByMissingCertificate));
        OnPropertyChanged(nameof(IsBlockedByApiAccess));
        OnPropertyChanged(nameof(IsBlockedByFailedCertificateCheck));
        OnPropertyChanged(nameof(GenerateBootImageUnavailableReason));
        OnPropertyChanged(nameof(CanContinue));
        UpdateWarnings();
    }

    private void UpdateWarnings()
    {
        ShowAdkWarning         = _selectedOperation == "GenerateBootImage" && IsBlockedByMissingAdk;
        ShowCertificateWarning = _selectedOperation == "GenerateBootImage" && IsBlockedByMissingCertificate;
    }

    private void SelectAndNavigate(string? operation)
    {
        SelectedOperation = operation;
        if (CanContinue)
            _navigate(_selectedOperation!);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

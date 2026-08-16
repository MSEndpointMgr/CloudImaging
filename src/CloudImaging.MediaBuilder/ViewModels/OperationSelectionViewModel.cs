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
    private string? _selectedOperation;
    private bool _showAdkWarning;
    private bool _showCertificateWarning;
    private bool _certificateConfigured;

    public OperationSelectionViewModel(
        Action<string> navigate,
        Func<bool>? isAdkInstalled = null,
        bool isAdministrator = true,
        bool isCertificateConfigured = true)
    {
        _navigate = navigate;
        _adkAvailable = (isAdkInstalled ?? BootImageGenerationService.IsAdkInstalled)();
        _isAdministrator = isAdministrator;
        _certificateConfigured = isCertificateConfigured;
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
    // a configured boot media certificate (T151, FR-050a); Prepare USB does not depend on any.
    public bool CanContinue =>
        _selectedOperation is not null
        && !(_selectedOperation == "GenerateBootImage" && !IsGenerateBootImageAvailable);

    /// <summary>
    /// True when the Generate Boot Image tile/nav item should be reachable at all. Bound
    /// directly (independent of <see cref="SelectedOperation"/>) so the Home tile can be
    /// disabled up front rather than only warning about it after the user picks it.
    /// </summary>
    public bool IsGenerateBootImageAvailable => _adkAvailable && _isAdministrator && _certificateConfigured;

    /// <summary>
    /// True once signed in as a user who lacks the Administrator role. Generate Boot Image is
    /// Administrator-only (FR-050b); Technician (or no-role) users only get Prepare USB Device.
    /// Independent of ADK availability so the role reason is never masked by the ADK-missing
    /// reason, and the two are always mutually exclusive with <see cref="IsBlockedByMissingAdk"/>.
    /// </summary>
    public bool IsRestrictedByRole => !_isAdministrator;

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
    public bool IsBlockedByMissingCertificate => _isAdministrator && _adkAvailable && !_certificateConfigured;

    /// <summary>Tooltip shown when the Generate Boot Image tile is disabled; role restriction takes precedence.</summary>
    public string? GenerateBootImageUnavailableReason =>
        IsRestrictedByRole ? "Requires the Administrator role."
        : IsBlockedByMissingAdk ? "Install the Windows ADK to unlock this section."
        : IsBlockedByMissingCertificate ? "Generate a boot media certificate in Portal Configuration before generating boot images."
        : null;

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    /// <summary>Single-tap navigation used by the Home tiles: select, then continue immediately if allowed.</summary>
    public ICommand NavigateCommand { get; }

    /// <summary>
    /// Updates whether an active boot media certificate is configured (T151), after an
    /// asynchronous check against the Operator API completes. Re-raises every property
    /// derived from this state so the Home tiles refresh live if the check resolves after
    /// the screen is already showing.
    /// </summary>
    public void SetCertificateConfigured(bool configured)
    {
        if (_certificateConfigured == configured)
            return;

        _certificateConfigured = configured;
        OnPropertyChanged(nameof(IsGenerateBootImageAvailable));
        OnPropertyChanged(nameof(IsBlockedByMissingCertificate));
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

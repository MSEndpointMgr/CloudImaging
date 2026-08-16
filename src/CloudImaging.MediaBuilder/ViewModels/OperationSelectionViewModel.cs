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

    public OperationSelectionViewModel(Action<string> navigate, Func<bool>? isAdkInstalled = null, bool isAdministrator = true)
    {
        _navigate = navigate;
        _adkAvailable = (isAdkInstalled ?? BootImageGenerationService.IsAdkInstalled)();
        _isAdministrator = isAdministrator;
        SelectOperationCommand = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand = new RelayCommand(_ => _navigate(_selectedOperation!), _ => CanContinue);
        NavigateCommand = new RelayCommand(op => SelectAndNavigate(op?.ToString()));

        // Preselect the first operation so Continue is immediately actionable.
        _selectedOperation = "GenerateBootImage";
        UpdateAdkWarning();
    }

    public string? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            _selectedOperation = value;
            OnPropertyChanged();
            UpdateAdkWarning();
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

    // Generate Boot Image requires the ADK (FR-050a) AND the Administrator role (FR-050b);
    // Prepare USB does not depend on either.
    public bool CanContinue =>
        _selectedOperation is not null
        && !(_selectedOperation == "GenerateBootImage" && !(_adkAvailable && _isAdministrator));

    /// <summary>
    /// True when the Generate Boot Image tile/nav item should be reachable at all. Bound
    /// directly (independent of <see cref="SelectedOperation"/>) so the Home tile can be
    /// disabled up front rather than only warning about it after the user picks it.
    /// </summary>
    public bool IsGenerateBootImageAvailable => _adkAvailable && _isAdministrator;

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

    /// <summary>Tooltip shown when the Generate Boot Image tile is disabled; role restriction takes precedence.</summary>
    public string? GenerateBootImageUnavailableReason =>
        IsRestrictedByRole ? "Requires the Administrator role."
        : IsBlockedByMissingAdk ? "Install the Windows ADK to unlock this section."
        : null;

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    /// <summary>Single-tap navigation used by the Home tiles: select, then continue immediately if allowed.</summary>
    public ICommand NavigateCommand { get; }

    private void UpdateAdkWarning() =>
        ShowAdkWarning = _selectedOperation == "GenerateBootImage" && IsBlockedByMissingAdk;

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

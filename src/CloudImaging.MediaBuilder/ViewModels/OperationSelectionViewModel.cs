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
    private string? _selectedOperation;
    private bool _showAdkWarning;

    public OperationSelectionViewModel(Action<string> navigate, Func<bool>? isAdkInstalled = null)
    {
        _navigate = navigate;
        _adkAvailable = (isAdkInstalled ?? BootImageGenerationService.IsAdkInstalled)();
        SelectOperationCommand = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand = new RelayCommand(_ => _navigate(_selectedOperation!), _ => CanContinue);

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

    // Generate Boot Image requires the ADK; Prepare USB does not.
    public bool CanContinue =>
        _selectedOperation is not null
        && !(_selectedOperation == "GenerateBootImage" && !_adkAvailable);

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    private void UpdateAdkWarning() =>
        ShowAdkWarning = _selectedOperation == "GenerateBootImage" && !_adkAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the MediaBuilder OperationSelectionView (T151, FR-051).
/// </summary>
public sealed class OperationSelectionViewModel : INotifyPropertyChanged
{
    private readonly Action<string> _navigate;
    private string? _selectedOperation;

    public OperationSelectionViewModel(Action<string> navigate)
    {
        _navigate = navigate;
        SelectOperationCommand = new RelayCommand(op => SelectedOperation = op?.ToString());
        ContinueCommand = new RelayCommand(_ => _navigate(_selectedOperation!), _ => CanContinue);

        // Preselect the first operation so Continue is immediately actionable.
        _selectedOperation = "GenerateBootImage";
    }

    public string? SelectedOperation
    {
        get => _selectedOperation;
        set { _selectedOperation = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanContinue)); }
    }

    public bool CanContinue => _selectedOperation is not null;

    public ICommand SelectOperationCommand { get; }
    public ICommand ContinueCommand        { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

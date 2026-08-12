using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.MediaBuilder.Services;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the SignInView welcome screen (T063, FR-050, FR-052).
/// Sign-in is the required first action; navigation is blocked until it completes.
/// </summary>
public sealed class SignInViewModel : INotifyPropertyChanged
{
    private readonly EntraAuthenticationService _authService;
    private readonly Action _navigateToOperationSelection;
    private string _statusMessage = string.Empty;
    private bool _hasError;
    private bool _isBusy;

    public SignInViewModel(
        EntraAuthenticationService authService,
        Action navigateToOperationSelection)
    {
        _authService                  = authService;
        _navigateToOperationSelection  = navigateToOperationSelection;
        SignInCommand = new RelayCommand(async _ => await SignInAsync(), _ => CanSignIn);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool HasError
    {
        get => _hasError;
        private set { _hasError = value; OnPropertyChanged(); }
    }

    public bool CanSignIn => !_isBusy;

    public bool IsBusy => _isBusy;

    public ICommand SignInCommand { get; }

    // ── Sign-in flow ──────────────────────────────────────────────────────────

    private async Task SignInAsync()
    {
        _isBusy = true;
        HasError = false;
        StatusMessage = "Signing in…";
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(IsBusy));

        try
        {
            var result = await _authService.SignInAsync();
            if (result.Success)
            {
                StatusMessage = $"Signed in as {result.UserPrincipalName}";
                _navigateToOperationSelection();
            }
            else
            {
                HasError = true;
                StatusMessage = result.ErrorMessage ?? "Sign-in failed. Please try again.";
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Sign-in error: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(CanSignIn));
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Relay command shared across MediaBuilder view models.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add    => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)    => _execute(parameter);
}

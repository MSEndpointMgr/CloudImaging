using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the ResultsView displaying all three terminal outcomes (T135, FR-025, FR-026).
/// </summary>
public sealed class ResultsViewModel : INotifyPropertyChanged
{
    public enum Outcome { Success, Failure, NotAuthorized }

    private readonly Outcome _outcome;
    private readonly string? _deviceSerialNumber;
    private readonly string? _errorDetail;
    private readonly Action _navigateToStart;

    public ResultsViewModel(
        Outcome outcome,
        string? deviceSerialNumber,
        string? errorDetail,
        Action navigateToStart,
        string? explicitSupportReferenceCode = null)
    {
        _outcome            = outcome;
        _deviceSerialNumber = deviceSerialNumber;
        _errorDetail        = errorDetail;
        _navigateToStart    = navigateToStart;

        // Support reference code: prefer the code the failing stage actually generated
        // (e.g. FMT/DWN/APL from ImagingWorkflowViewModel); fall back to a REG-stage code with a
        // sentinel all-zero session ref only when the caller didn't already produce one (this
        // should only happen for callers that don't yet carry session context, e.g. tooling).
        SupportReferenceCode = outcome != Outcome.Failure
            ? null
            : explicitSupportReferenceCode
                ?? CloudImaging.Contracts.Models.SupportReferenceCode
                    .ForClient(Guid.Empty, "REG")
                    .ToString();

        RetryCommand = new RelayCommand(_ => _navigateToStart());
        ExitCommand  = new RelayCommand(_ => System.Windows.Application.Current?.Shutdown());
    }

    public bool IsSuccess       => _outcome == Outcome.Success;
    public bool IsFailure       => _outcome == Outcome.Failure;
    public bool IsNotAuthorized => _outcome == Outcome.NotAuthorized;

    public string? DeviceSerialNumber => _deviceSerialNumber;
    public string? SupportReferenceCode { get; }
    public string? ErrorDetail  => _errorDetail;

    public ICommand RetryCommand { get; }

    /// <summary>
    /// Closes the application. This is the only available action on the NotAuthorized outcome
    /// (FR-007b/FR-026) — there is no automatic retry, since the device must first be enrolled.
    /// </summary>
    public ICommand ExitCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

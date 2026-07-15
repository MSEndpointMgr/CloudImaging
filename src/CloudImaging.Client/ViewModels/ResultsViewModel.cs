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
        Action navigateToStart)
    {
        _outcome            = outcome;
        _deviceSerialNumber = deviceSerialNumber;
        _errorDetail        = errorDetail;
        _navigateToStart    = navigateToStart;

        // Support reference code: deterministic from serial + timestamp for correlation
        SupportReferenceCode = outcome == Outcome.Failure
            ? CloudImaging.Contracts.Models.SupportReferenceCode
                .ForClient(deviceSerialNumber is not null && Guid.TryParse(deviceSerialNumber, out var g) ? g : Guid.Empty, "REG")
                .ToString()
            : null;

        RetryCommand = new RelayCommand(_ => _navigateToStart());
    }

    public bool IsSuccess       => _outcome == Outcome.Success;
    public bool IsFailure       => _outcome == Outcome.Failure;
    public bool IsNotAuthorized => _outcome == Outcome.NotAuthorized;

    public string? DeviceSerialNumber => _deviceSerialNumber;
    public string? SupportReferenceCode { get; }
    public string? ErrorDetail  => _errorDetail;

    public ICommand RetryCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

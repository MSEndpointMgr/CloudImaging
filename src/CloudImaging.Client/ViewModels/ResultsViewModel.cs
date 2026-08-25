using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the ResultsView displaying all three terminal outcomes (T135, FR-025, FR-026).
/// </summary>
public sealed class ResultsViewModel : INotifyPropertyChanged
{
    public enum Outcome { Success, Failure, NotAuthorized }

    /// <summary>How long the Success outcome waits before automatically restarting the device.</summary>
    private const int RestartCountdownDurationSeconds = 10;

    private readonly Outcome _outcome;
    private readonly string? _deviceSerialNumber;
    private readonly string? _errorDetail;
    private readonly Action _navigateToStart;
    private readonly Action? _restartSystem;
    private readonly DispatcherTimer? _restartTimer;
    private int _restartCountdownSecondsRemaining = RestartCountdownDurationSeconds;

    public ResultsViewModel(
        Outcome outcome,
        string? deviceSerialNumber,
        string? errorDetail,
        Action navigateToStart,
        string? explicitSupportReferenceCode = null,
        Action? restartSystem = null)
    {
        _outcome            = outcome;
        _deviceSerialNumber = deviceSerialNumber;
        _errorDetail        = errorDetail;
        _navigateToStart    = navigateToStart;
        _restartSystem      = restartSystem;

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

        // FR-025: on success, count down and restart the device automatically instead of
        // leaving it sitting on a terminal screen indefinitely — no restartSystem callback is
        // supplied by unit tests, so the countdown/restart is inert there.
        if (_outcome == Outcome.Success && _restartSystem is not null)
        {
            _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _restartTimer.Tick += OnRestartTimerTick;
            _restartTimer.Start();
        }
    }

    public bool IsSuccess       => _outcome == Outcome.Success;
    public bool IsFailure       => _outcome == Outcome.Failure;
    public bool IsNotAuthorized => _outcome == Outcome.NotAuthorized;

    public string? DeviceSerialNumber => _deviceSerialNumber;
    public string? SupportReferenceCode { get; }
    public string? ErrorDetail  => _errorDetail;

    /// <summary>Seconds remaining before an automatic restart (Success outcome only).</summary>
    public int RestartCountdownSecondsRemaining
    {
        get => _restartCountdownSecondsRemaining;
        private set
        {
            _restartCountdownSecondsRemaining = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RestartCountdownPercent));
        }
    }

    /// <summary>
    /// Countdown expressed as a percentage that drains from 100 to 0 over
    /// <see cref="RestartCountdownDurationSeconds"/> seconds, for a determinate ProgressBar.
    /// </summary>
    public double RestartCountdownPercent =>
        RestartCountdownSecondsRemaining * 100.0 / RestartCountdownDurationSeconds;

    private void OnRestartTimerTick(object? sender, EventArgs e)
    {
        RestartCountdownSecondsRemaining--;
        if (RestartCountdownSecondsRemaining > 0)
            return;

        _restartTimer!.Stop();
        _restartTimer.Tick -= OnRestartTimerTick;
        _restartSystem?.Invoke();
    }

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

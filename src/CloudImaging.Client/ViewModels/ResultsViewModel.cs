using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.ViewModels;

/// <summary>One enabled pre-flight requirement on the blocked screen.</summary>
public sealed record PreFlightRequirementRow(string Name, string Value, bool Met, SymbolRegular Icon);

/// <summary>
/// View model for the ResultsView displaying all four terminal outcomes (T135, FR-021, FR-025, FR-026).
/// </summary>
public sealed partial class ResultsViewModel : INotifyPropertyChanged
{
    public enum Outcome { Success, Failure, NotAuthorized, Expired }

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
        Action? restartSystem = null,
        Guid? sessionId = null,
        IReadOnlyList<PreFlightCheckResult>? preFlightChecks = null,
        Action? tryAgain = null,
        ILogger? logger = null)
    {
        _outcome            = outcome;
        _deviceSerialNumber = deviceSerialNumber;
        _errorDetail        = errorDetail;
        _navigateToStart    = navigateToStart;
        _restartSystem      = restartSystem;
        SessionId           = sessionId;

        // Only requirements that were switched on are shown; a switched-off check blocks nothing.
        var required = (preFlightChecks ?? [])
            .Where(c => c.Outcome != PreFlightCheckOutcome.NotRequired)
            .ToList();
        BlockedRequirements = required
            .Select(c => new PreFlightRequirementRow(
                PreFlightCheckText.Name(c.Check),
                PreFlightCheckText.Value(c.Check, c.Observed),
                c.Outcome != PreFlightCheckOutcome.Failed,
                IconFor(c.Check)))
            .ToList();
        var failedCount = required.Count(c => c.Outcome == PreFlightCheckOutcome.Failed);
        BlockedSummary = required.Count == 1
            ? $"{failedCount} of 1 pre-flight requirement is not met. Fix it, then restart the device."
            : $"{failedCount} of {required.Count} pre-flight requirements are not met. Fix them, then restart the device.";

        // Fix hints are only in the log; the screen keeps to one line per requirement.
        if (outcome == Outcome.NotAuthorized && logger is not null)
        {
            foreach (var failed in required.Where(c => c.Outcome == PreFlightCheckOutcome.Failed))
            {
                LogCheckFailed(logger, PreFlightCheckText.Name(failed.Check), PreFlightCheckText.Value(failed.Check, failed.Observed),
                    PreFlightCheckText.FixHint(failed.Check, failed.Observed));
            }
        }

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
        TryAgainCommand = new RelayCommand(_ => (tryAgain ?? _navigateToStart)());

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

    /// <summary>
    /// The session was never coupled before its passcode/inactivity window elapsed — a benign
    /// timeout, not a diagnosable failure (FR-021). Shown with warning (not error) styling, and
    /// still offers Retry so the technician can simply request a fresh session.
    /// </summary>
    public bool IsExpired      => _outcome == Outcome.Expired;

    public string? DeviceSerialNumber => _deviceSerialNumber;
    public string? SupportReferenceCode { get; }
    public string? ErrorDetail  => _errorDetail;

    public Guid? SessionId { get; }

    /// <summary>Every switched-on pre-flight requirement for the blocked outcome, in check order.</summary>
    public IReadOnlyList<PreFlightRequirementRow> BlockedRequirements { get; }

    /// <summary>False for sessions from a backend without per-check results: the enrollment-only message is shown instead.</summary>
    public bool HasBlockedRequirements => BlockedRequirements.Count > 0;
    public bool HasNoBlockedRequirements => !HasBlockedRequirements;

    public string BlockedSummary { get; }

    /// <summary>Starts a new session without a reboot, so a fixed or approved device can continue.</summary>
    public ICommand TryAgainCommand { get; }

    private static SymbolRegular IconFor(PreFlightCheck check) => check switch
    {
        PreFlightCheck.AutopilotPresence => SymbolRegular.Fingerprint24,
        PreFlightCheck.FirmwareMode => SymbolRegular.DeveloperBoard24,
        PreFlightCheck.SecureBoot => SymbolRegular.ShieldCheckmark24,
        _ => SymbolRegular.Key24,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pre-flight check failed: {Check}: {Value}. {FixHint}")]
    private static partial void LogCheckFailed(ILogger logger, string check, string value, string fixHint);

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
    /// Closes the application. On the NotAuthorized outcome it sits beside Try again, since a
    /// blocked device usually needs a firmware change or an administrator approval first.
    /// </summary>
    public ICommand ExitCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

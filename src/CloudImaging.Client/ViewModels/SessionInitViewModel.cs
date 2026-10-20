using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Client.Logging;
using CloudImaging.Contracts.Enums;
using CloudImaging.Client.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the SessionInitView (T033, FR-031).
/// Polls session status every 5 seconds with a manual refresh option.
/// Transitions immediately to ResultsView on SessionNotAuthorized (FR-026) and on
/// SessionExpired — a passcode that timed out before an operator coupled it (FR-021).
/// Handles cert-error state for mTLS failures (FR-071).
/// </summary>
public sealed partial class SessionInitViewModel : INotifyPropertyChanged, IDisposable
{
    // FR-003: server-side polling cadence is 30s; the Client must match it rather than
    // polling 6x more often than necessary.
    private const int PollingIntervalSeconds = 30;

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly string? _deviceSerialNumber;
    private readonly Action<ResultsViewModel.Outcome, string?, string?, string?> _navigateToResults;
    private readonly Action<SessionStatusResponse, string?> _navigateToProgress;
    private readonly Action<SessionStatusResponse, string?>? _navigateToBlocked;
    private readonly Action? _navigateToStart;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private bool _isPolling = true;
    private string? _statusMessage;
    private bool _hasCertError;
    private string? _certErrorMessage;

    // Guards against the manual RefreshCommand racing the automatic 30s poll loop: without this,
    // both could independently observe the same terminal/hand-off state and each fire
    // _navigateToProgress/_navigateToResults, constructing a second ImagingWorkflowViewModel that
    // restarts the pipeline from Format Disk and replaces the already-progressing ProgressView —
    // visible to the user as the Format Disk step "reverting" to active after Download had
    // already started. Only the first caller to reach a terminal/hand-off state may navigate.
    private readonly object _terminalGate = new();
    private bool _hasReachedTerminal;

    private bool TryClaimTerminalTransition()
    {
        lock (_terminalGate)
        {
            if (_hasReachedTerminal) return false;
            _hasReachedTerminal = true;
            return true;
        }
    }

    /// <summary>Builds the view model and starts the background 30s status poll loop immediately.</summary>
    /// <param name="gatewayClient">Device Gateway client used to poll session status.</param>
    /// <param name="sessionId">Session this view model polls.</param>
    /// <param name="passcode">Coupling passcode shown to the operator.</param>
    /// <param name="deviceSerialNumber">Serial number carried through to downstream views.</param>
    /// <param name="navigateToResults">Shows the results screen when the session reaches a terminal state.</param>
    /// <param name="navigateToProgress">Hands off to the imaging pipeline once imaging starts.</param>
    /// <param name="navigateToBlocked">Shows the blocked screen when pre-flight authorization refused the device.</param>
    /// <param name="navigateToStart">Abandons this session and returns to operation selection to request a new passcode.</param>
    /// <param name="logger">Defaults to a no-op logger.</param>
    public SessionInitViewModel(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        string passcode,
        string? deviceSerialNumber,
        Action<ResultsViewModel.Outcome, string?, string?, string?> navigateToResults,
        Action<SessionStatusResponse, string?> navigateToProgress,
        Action<SessionStatusResponse, string?>? navigateToBlocked = null,
        Action? navigateToStart = null,
        ILogger<SessionInitViewModel>? logger = null)
    {
        _gatewayClient      = gatewayClient;
        _sessionId          = sessionId;
        Passcode            = passcode;
        _deviceSerialNumber = deviceSerialNumber;
        _navigateToResults  = navigateToResults;
        _navigateToProgress = navigateToProgress;
        _navigateToBlocked  = navigateToBlocked;
        _navigateToStart    = navigateToStart;
        _logger             = logger ?? NullLogger<SessionInitViewModel>.Instance;

        RefreshCommand = new RelayCommand(async _ => await PollOnceAsync());
        StartOverCommand = new RelayCommand(_ => StartOver());

        // Start background polling loop
        _ = PollLoopAsync(_cts.Token);
    }

    /// <summary>Identifier of the session this view model polls.</summary>
    public Guid    SessionId   => _sessionId;

    /// <summary>6-digit coupling passcode shown to the operator.</summary>
    public string  Passcode    { get; }

    /// <summary>
    /// Passcode grouped into 3-digit chunks (e.g. "428913" → "428 913") so it's easier for an
    /// operator to read back over the phone/radio than an unbroken 6-digit string.
    /// </summary>
    public string FormattedPasscode => FormatPasscode(Passcode);

    private static string FormatPasscode(string passcode)
    {
        if (string.IsNullOrEmpty(passcode)) return passcode;

        var sb = new System.Text.StringBuilder(passcode.Length + passcode.Length / 3);
        for (var i = 0; i < passcode.Length; i++)
        {
            if (i > 0 && i % 3 == 0) sb.Append(' ');
            sb.Append(passcode[i]);
        }
        return sb.ToString();
    }

    /// <summary>True while the status poll loop is still running; false once a terminal state is reached.</summary>
    public bool IsPolling
    {
        get => _isPolling;
        private set { _isPolling = value; OnPropertyChanged(); }
    }

    /// <summary>Text shown under the passcode describing the current session state.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>True when the boot-media certificate or device-session token was rejected by the server.</summary>
    public bool HasCertError
    {
        get => _hasCertError;
        private set { _hasCertError = value; OnPropertyChanged(); }
    }

    /// <summary>Explanatory text shown when <see cref="HasCertError"/> is set.</summary>
    public string? CertErrorMessage
    {
        get => _certErrorMessage;
        private set { _certErrorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Polls the session status immediately instead of waiting for the next scheduled poll.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Abandons this session and returns to operation selection so a new passcode can be issued.</summary>
    public ICommand StartOverCommand { get; }

    /// <summary>True when a start-over affordance should be offered.</summary>
    public bool CanStartOver => _navigateToStart is not null;

    // The passcode on screen may already have been refused by the server while this session is
    // still polling happily, so the operator needs a way out that does not involve a reboot.
    private void StartOver()
    {
        if (_navigateToStart is null) return;
        if (!TryClaimTerminalTransition()) return;
        IsPolling = false;
        _cts.Cancel();
        _navigateToStart();
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        StatusMessage = "Waiting for operator to couple session…";

        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync();

            if (!IsPolling) break; // Terminal state reached

            try { await Task.Delay(TimeSpan.FromSeconds(PollingIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync()
    {
        // Already reached a terminal/hand-off state on a previous (possibly concurrent) call —
        // e.g. the manual Refresh button was clicked around the same time the automatic 30s loop
        // fired. Ignore this call entirely rather than issuing a redundant status GET.
        if (!IsPolling) return;

        try
        {
            var session = await _gatewayClient.GetSessionStatusAsync(_sessionId, _cts.Token);
            if (session is null) return;

            if (!Enum.TryParse<SessionState>(session.State ?? string.Empty, ignoreCase: true, out var state))
                state = SessionState.SessionInit;

            switch (state)
            {
                case SessionState.SessionNotAuthorized:
                    if (!TryClaimTerminalTransition()) break;
                    IsPolling = false;
                    if (_navigateToBlocked is not null)
                    {
                        _navigateToBlocked(session, _deviceSerialNumber);
                        break;
                    }

                    _navigateToResults(
                        ResultsViewModel.Outcome.NotAuthorized,
                        _deviceSerialNumber,
                        null,
                        null);
                    break;

                case SessionState.SessionCompleted:
                    if (!TryClaimTerminalTransition()) break;
                    IsPolling = false;
                    _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
                    break;

                case SessionState.SessionFailed:
                    if (!TryClaimTerminalTransition()) break;
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.Failure,
                        _deviceSerialNumber,
                        null,
                        null);
                    break;

                // FR-021: the session was never coupled before the passcode/inactivity window
                // elapsed — a benign timeout, not a real failure, so it must not be presented as
                // one (see ResultsViewModel.Outcome.Expired).
                case SessionState.SessionExpired:
                    if (!TryClaimTerminalTransition()) break;
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.Expired,
                        _deviceSerialNumber,
                        null,
                        null);
                    break;

                // FR-005/FR-009d: hand off to the imaging pipeline once the operator has actually
                // clicked "Start Imaging" and an OS image has been assigned server-side (state
                // SessionStarted or later). SessionAssigned only means the device has been
                // coupled by passcode — no image is selected yet and the session must NOT
                // proceed to the imaging/progress view at that point (previously this case
                // incorrectly grouped SessionAssigned with SessionStarted/SessionInProgress,
                // causing the Client to jump into the Format Disk step immediately on coupling,
                // before an operator ever pressed Start Imaging).
                case SessionState.SessionAssigned:
                    StatusMessage = "Device coupled. Waiting for the operator to select an OS image and start imaging…";
                    break;

                case SessionState.SessionStarted:
                case SessionState.SessionInProgress:
                    if (!TryClaimTerminalTransition()) break;
                    IsPolling = false;
                    _navigateToProgress(session, _deviceSerialNumber);
                    break;

                default:
                    StatusMessage = $"Status: {session.State}. Awaiting coupling…";
                    break;
            }
        }
        catch (HttpRequestException ex) when
            (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            LogPollFailed(_logger, LoggedFailure.Reason(ex), null);
            ShowCertError("TLS handshake failed: the service rejected the boot media certificate. Rebuild the boot image in Media Builder and re-prepare the USB drive.");
        }
        catch (DeviceGatewayApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Both an mTLS certificate rejection and a revoked/expired device-session Bearer
            // token surface as HTTP 401 — disambiguate using the RFC7807 problem "type" the
            // server includes, instead of always showing the (often wrong) cert-mismatch text.
            if (string.Equals(ex.ProblemType, DeviceGatewayApiException.TokenProblemType, StringComparison.Ordinal))
            {
                ShowCertError("Session token rejected or expired (HTTP 401). Select Start over to register a new session.");
            }
            else
            {
                ShowCertError("Boot media certificate rejected (HTTP 401). Rebuild the boot image in Media Builder and re-prepare the USB drive.");
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Start over was pressed while a poll was in flight.
        }
        catch (Exception ex)
        {
            LogPollFailed(_logger, LoggedFailure.Reason(ex), LoggedFailure.StackTrace(ex));
            StatusMessage = $"Status check failed: {ex.Message}";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session status check failed: {Reason}")]
    private static partial void LogPollFailed(ILogger logger, string reason, Exception? ex);

    private void ShowCertError(string message)
    {
        IsPolling      = false;
        HasCertError   = true;
        CertErrorMessage = message;
    }

    /// <summary>Cancels the background poll loop.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Contracts.Enums;
using CloudImaging.Client.Services;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the SessionInitView (T033, FR-031).
/// Polls session status every 5 seconds with a manual refresh option.
/// Transitions immediately to ResultsView on SessionNotAuthorized (FR-026).
/// Handles cert-error state for mTLS failures (FR-071).
/// </summary>
public sealed class SessionInitViewModel : INotifyPropertyChanged, IDisposable
{
    // FR-003: server-side polling cadence is 30s; the Client must match it rather than
    // polling 6x more often than necessary.
    private const int PollingIntervalSeconds = 30;

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly string? _deviceSerialNumber;
    private readonly Action<ResultsViewModel.Outcome, string?, string?, string?> _navigateToResults;
    private readonly Action<SessionStatusResponse, string?> _navigateToProgress;
    private readonly CancellationTokenSource _cts = new();

    private bool _isPolling = true;
    private string? _statusMessage;
    private bool _hasCertError;
    private string? _certErrorMessage;

    public SessionInitViewModel(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        string passcode,
        string? deviceSerialNumber,
        Action<ResultsViewModel.Outcome, string?, string?, string?> navigateToResults,
        Action<SessionStatusResponse, string?> navigateToProgress)
    {
        _gatewayClient      = gatewayClient;
        _sessionId          = sessionId;
        Passcode            = passcode;
        _deviceSerialNumber = deviceSerialNumber;
        _navigateToResults  = navigateToResults;
        _navigateToProgress = navigateToProgress;

        RefreshCommand = new RelayCommand(async _ => await PollOnceAsync());

        // Start background polling loop
        _ = PollLoopAsync(_cts.Token);
    }

    public Guid    SessionId   => _sessionId;
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

    public bool IsPolling
    {
        get => _isPolling;
        private set { _isPolling = value; OnPropertyChanged(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool HasCertError
    {
        get => _hasCertError;
        private set { _hasCertError = value; OnPropertyChanged(); }
    }

    public string? CertErrorMessage
    {
        get => _certErrorMessage;
        private set { _certErrorMessage = value; OnPropertyChanged(); }
    }

    public ICommand RefreshCommand { get; }

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
        try
        {
            var session = await _gatewayClient.GetSessionStatusAsync(_sessionId, _cts.Token);
            if (session is null) return;

            if (!Enum.TryParse<SessionState>(session.State ?? string.Empty, ignoreCase: true, out var state))
                state = SessionState.SessionInit;

            switch (state)
            {
                case SessionState.SessionNotAuthorized:
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.NotAuthorized,
                        _deviceSerialNumber,
                        null,
                        null);
                    break;

                case SessionState.SessionCompleted:
                    IsPolling = false;
                    _navigateToResults(ResultsViewModel.Outcome.Success, _deviceSerialNumber, null, null);
                    break;

                case SessionState.SessionFailed:
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.Failure,
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
                    StatusMessage = "Device coupled — waiting for the operator to select an OS image and start imaging…";
                    break;

                case SessionState.SessionStarted:
                case SessionState.SessionInProgress:
                    IsPolling = false;
                    _navigateToProgress(session, _deviceSerialNumber);
                    break;

                default:
                    StatusMessage = $"Status: {session.State} — awaiting coupling…";
                    break;
            }
        }
        catch (HttpRequestException ex) when
            (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            ShowCertError("The boot media certificate was rejected by the server. " +
                          "Regenerate the boot image using Cloud Imaging Media Builder " +
                          "and re-prepare the USB drive with the new image.");
        }
        catch (DeviceGatewayApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Both an mTLS certificate rejection and a revoked/expired device-session Bearer
            // token surface as HTTP 401 — disambiguate using the RFC7807 problem "type" the
            // server includes, instead of always showing the (often wrong) cert-mismatch text.
            if (string.Equals(ex.ProblemType, DeviceGatewayApiException.TokenProblemType, StringComparison.Ordinal))
            {
                ShowCertError("The device session token was rejected or has expired. " +
                              "Restart the Cloud Imaging Client to begin a new session.");
            }
            else
            {
                ShowCertError("Certificate mismatch (HTTP 401). " +
                              "Regenerate the boot image and re-prepare USB media.");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Poll error: {ex.Message}";
        }
    }

    private void ShowCertError(string message)
    {
        IsPolling      = false;
        HasCertError   = true;
        CertErrorMessage = message;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

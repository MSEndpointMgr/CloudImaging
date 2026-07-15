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
    private const int PollingIntervalSeconds = 5;

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly Action<ResultsViewModel.Outcome, string?, string?> _navigateToResults;
    private readonly CancellationTokenSource _cts = new();

    private bool _isPolling = true;
    private string? _statusMessage;
    private bool _hasCertError;
    private string? _certErrorMessage;

    public SessionInitViewModel(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        string passcode,
        Action<ResultsViewModel.Outcome, string?, string?> navigateToResults)
    {
        _gatewayClient     = gatewayClient;
        _sessionId         = sessionId;
        Passcode           = passcode;
        _navigateToResults = navigateToResults;

        RefreshCommand = new RelayCommand(async _ => await PollOnceAsync());

        // Start background polling loop
        _ = PollLoopAsync(_cts.Token);
    }

    public Guid    SessionId   => _sessionId;
    public string  Passcode    { get; }

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

            switch (session.State)
            {
                case SessionState.SessionNotAuthorized:
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.NotAuthorized,
                        session.DeviceSerialNumber,
                        null);
                    break;

                case SessionState.SessionCompleted:
                    IsPolling = false;
                    _navigateToResults(ResultsViewModel.Outcome.Success, null, null);
                    break;

                case SessionState.SessionFailed:
                    IsPolling = false;
                    _navigateToResults(
                        ResultsViewModel.Outcome.Failure,
                        null,
                        session.SasTokenUrl); // repurposed field carries error detail
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
        catch (HttpRequestException ex) when
            (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            ShowCertError("Certificate mismatch (HTTP 401). " +
                          "Regenerate the boot image and re-prepare USB media.");
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

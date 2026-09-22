using CloudImaging.Client.Services;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Client.Services;

/// <summary>
/// Background polling service that monitors session status during the assignment phase (T044, FR-031).
/// Polls at 5-second intervals (configurable) with an immediate manual-refresh option.
/// Transitions the caller to the next view on terminal states.
/// </summary>
public sealed class SessionStatusPoller : IDisposable
{
    /// <summary>
    /// Represents the current status received from a status poll.
    /// </summary>
    public sealed record StatusResult(
        SessionState State,
        string? SasTokenUrl,
        string? Sha256Hash,
        int OverallProgressPercent,
        string? CurrentStep);

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly int _pollIntervalSeconds;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;

    /// <summary>Raised with the current status whenever a poll succeeds.</summary>
    public event EventHandler<StatusResult>? StatusReceived;

    /// <summary>Raised when a poll fails with the exception that caused the failure.</summary>
    public event EventHandler<Exception>? PollError;

    /// <summary>
    /// Creates a new <see cref="SessionStatusPoller"/> polling every
    /// <paramref name="pollIntervalSeconds"/> seconds.
    /// </summary>
    public SessionStatusPoller(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        int pollIntervalSeconds = 30)
    {
        _gatewayClient      = gatewayClient;
        _sessionId          = sessionId;
        _pollIntervalSeconds = pollIntervalSeconds;
    }

    /// <summary>Starts the background polling loop.</summary>
    public void Start() => _pollTask = RunLoopAsync(_cts.Token);

    /// <summary>Triggers an immediate poll outside the regular interval.</summary>
    public async Task RefreshAsync() => await PollOnceAsync(CancellationToken.None);

    // ── Private polling logic ─────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);

            // Stop polling on terminal states
            var lastState = _lastResult?.State;
            if (lastState is SessionState.SessionCompleted
                          or SessionState.SessionFailed
                          or SessionState.SessionNotAuthorized
                          or SessionState.SessionExpired)
            {
                break;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private StatusResult? _lastResult;

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var session = await _gatewayClient.GetSessionStatusAsync(_sessionId, ct);
            if (session is null) return;

            if (!Enum.TryParse<SessionState>(
                    session.State ?? string.Empty,
                    ignoreCase: true,
                    out var state))
            {
                state = SessionState.SessionInit;
            }

            _lastResult = new StatusResult(
                State:                  state,
                SasTokenUrl:            session.SasTokenUrl,
                Sha256Hash:             session.Sha256Hash,
                OverallProgressPercent: session.OverallProgressPercent,
                CurrentStep:            session.CurrentStep);

            StatusReceived?.Invoke(this, _lastResult);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PollError?.Invoke(this, ex);
        }
    }

    /// <summary>Stops the polling loop and releases resources.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

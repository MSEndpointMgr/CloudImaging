using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Monitors the SAS token expiry and proactively refreshes it when &lt; 15 minutes remain (T055, FR-025).
/// Runs on a background loop and does NOT block the imaging download/apply pipeline.
/// </summary>
public sealed partial class SasRefreshCoordinator : IDisposable
{
    public static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval     = TimeSpan.FromMinutes(5);

    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly Guid _sessionId;
    private readonly ILogger<SasRefreshCoordinator> _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string? CurrentSasUrl { get; private set; }
    public DateTimeOffset? SasExpiresAt  { get; private set; }

    public SasRefreshCoordinator(
        DeviceGatewayApiClient gatewayClient,
        Guid sessionId,
        string initialSasUrl,
        DateTimeOffset? initialExpiresAt,
        ILogger<SasRefreshCoordinator> logger)
    {
        _gatewayClient = gatewayClient;
        _sessionId     = sessionId;
        CurrentSasUrl  = initialSasUrl;
        SasExpiresAt   = initialExpiresAt;
        _logger        = logger;
    }

    /// <summary>Starts the background SAS refresh loop.</summary>
    public void Start() => _ = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct);

                var timeLeft = SasExpiresAt.HasValue
                    ? SasExpiresAt.Value - DateTimeOffset.UtcNow
                    : TimeSpan.Zero;

                if (timeLeft < RefreshThreshold)
                {
                    LogRefreshing(_logger, _sessionId, (int)timeLeft.TotalMinutes);
                    var result = await _gatewayClient.RefreshSasTokenAsync(_sessionId, ct);
                    if (result.SasTokenUrl is not null)
                    {
                        CurrentSasUrl = result.SasTokenUrl;
                        // Prefer the server-computed expiry; only guess if the server ever omits it.
                        SasExpiresAt  = result.ExpiresAt ?? DateTimeOffset.UtcNow + TimeSpan.FromMinutes(60);
                        LogRefreshed(_logger, _sessionId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogRefreshFailed(_logger, ex, _sessionId);
            }
        }
    }

    public void Dispose()
    {
        // Disposed from both RunAsync's finally block and ImagingWorkflowViewModel.Dispose, and
        // CancellationTokenSource.Cancel throws once disposed.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SAS token for session {SessionId} expires in {MinutesLeft} min — refreshing.")]
    private static partial void LogRefreshing(ILogger logger, Guid sessionId, int minutesLeft);

    [LoggerMessage(Level = LogLevel.Information, Message = "SAS token refreshed for session {SessionId}.")]
    private static partial void LogRefreshed(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SAS token refresh failed for session {SessionId}.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex, Guid sessionId);
}
